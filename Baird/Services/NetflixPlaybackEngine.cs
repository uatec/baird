using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Baird.Mpv;
using Baird.Playback;
using Baird.ViewModels;

namespace Baird.Services
{
    /// <summary>
    /// Plays Netflix titles for Baird's normal video player. The video plays in the shared Netflix browser behind Baird's
    /// (transparent) window; Baird keeps the keys and this engine turns them into commands to Netflix's player over CDP.
    /// It runs the proven playback flow (<see cref="NetflixSpikeViewModel"/>) on the browser that <see cref="NetflixSession"/>
    /// keeps open, polls the video, and returns the browser to its idle page when playback stops.
    /// </summary>
    public sealed class NetflixPlaybackEngine : IBrowserPlaybackEngine
    {
        private const string WatchPrefix = "https://www.netflix.com/watch/";
        /// <summary>Polls in a row without a video, off the watch page, before playback counts as ended.</summary>
        private const int MissingPollsForEnd = 3;
        /// <summary>
        /// The same on the watch page (about a minute): Netflix moving to the next episode can drop the video for a moment
        /// while staying on /watch/, which must not end playback.
        /// </summary>
        private const int MissingPollsOnWatchPageForEnd = 120;
        /// <summary>Failed polls in a row (no answer, an error, or no reply within <see cref="PollTimeout"/>) before giving up.</summary>
        private const int BadPollsForFailure = 2;
        private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);

        private readonly NetflixSession _session;
        private readonly Func<NetflixSpikeViewModel> _createFlow;
        private readonly object _commandLock = new();
        private Task _commands = Task.CompletedTask;
        private NetflixSpikeViewModel? _flow;
        private CancellationTokenSource? _pollCts;
        private int _generation;
        private int _missingPolls;
        private int _badPolls;
        private Task _stopping = Task.CompletedTask;
        private bool _failedRaised;
        private bool _paused;
        private double _position;
        private double _duration;
        private volatile PlaybackState _state = PlaybackState.Idle;

        public NetflixPlaybackEngine(NetflixSession session, Func<NetflixSpikeViewModel> createFlow)
        {
            _session = session;
            _createFlow = createFlow;
        }

        public static bool Handles(string? url) => url != null && url.StartsWith(WatchPrefix, StringComparison.Ordinal);

        public string Name => "Netflix";
        public bool CanPlay(string? url) => Handles(url);

        public PlaybackState State => _state;
        public double TimePositionSeconds => Volatile.Read(ref _position);
        public double DurationSeconds => Volatile.Read(ref _duration);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        private string _statusText = "";
        /// <summary>A simple text for the current step while the video loads.</summary>
        public string StatusText => _statusText;

        public event EventHandler? FileLoaded;
        public event EventHandler? StreamEnded;
        public event EventHandler<bool>? PauseStateChanged;
        public event EventHandler<double>? DurationChanged;
        public event EventHandler? StatusChanged;
        /// <summary>Netflix could not play (the reason, for the viewer). Raised once per play.</summary>
        public event EventHandler<string>? Failed;

        public void Play(string url, double? startSeconds = null) => _ = PlayAsync(url); // Netflix resumes at its own place

        public async Task PlayAsync(string url)
        {
            if (_flow != null || _state != PlaybackState.Idle) _ = StopAsync();
            var stopping = _stopping; // the browser must be back on its idle page before it opens the next title
            var generation = Interlocked.Increment(ref _generation);
            _failedRaised = false;
            _missingPolls = 0;
            _badPolls = 0;
            _paused = false;
            Volatile.Write(ref _position, 0);
            Volatile.Write(ref _duration, 0);
            _state = PlaybackState.Loading;
            SetStatus("Starting Netflix…");
            Console.WriteLine($"[NetflixEngine] Playing {url}.");

            try
            {
                // Another service (Disney+) may still be stopping: its tab must be back on its idle page before Netflix takes it.
                if (!await _session.Hub.WaitForOtherPlaybackToEndAsync("Netflix", TimeSpan.FromSeconds(15)))
                {
                    Fail(generation, $"{_session.Hub.ActivePlayback} is still playing.");
                    return;
                }
                _session.IsPlaybackActive = true;
                await stopping;
                if (generation != _generation) return;

                string? failure;
                using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                {
                    try { failure = await _session.EnsureReadyAsync(cts.Token); }
                    catch (OperationCanceledException) { failure = "The Netflix browser did not get ready within 2 minutes."; }
                }
                if (generation != _generation) return;
                if (failure != null)
                {
                    Fail(generation, failure);
                    return;
                }

                var flow = _createFlow();
                flow.UseBairdHud = true;
                flow.KeepBrowserOpen = true;
                flow.TargetUrl = url;
                flow.PropertyChanged += OnFlowChanged;
                flow.PlaybackConfirmed += OnPlaybackConfirmed;
                _flow = flow;

                await flow.LaunchPlaybackFlowAsync();
                if (generation != _generation || _state != PlaybackState.Loading) return;

                // The flow settled without the video playing: it says why.
                var reason = flow.IsMfaRequired
                    ? "Netflix asked for a verification code. Open the Netflix Spike tab in the menu, start playback there and enter the code, then play this again."
                    : flow.StatusMessage is { Length: > 0 } m ? m : "Netflix did not start the video.";
                await ReleaseFlowAsync(generation);
                Fail(generation, reason);
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                Console.WriteLine($"[NetflixEngine] Playing failed: {ex}");
                await ReleaseFlowAsync(generation);
                Fail(generation, $"Netflix could not play: {ex.Message}");
            }
        }

        public void Stop() => _ = StopAsync();

        /// <summary>Stops at once (Idle); the browser's return to its idle page completes with the task.</summary>
        public Task StopAsync()
        {
            var generation = Interlocked.Increment(ref _generation);
            if (_state != PlaybackState.Idle) Console.WriteLine("[NetflixEngine] Stopping; the browser goes back to its idle page.");
            _state = PlaybackState.Idle;
            var previous = _stopping;
            return _stopping = StopAfterAsync(previous, generation);
        }

        private async Task StopAfterAsync(Task previous, int generation)
        {
            await previous;
            await ReleaseFlowAsync(generation);
        }

        // The state changes at once, so a quick second press is not lost; the next poll corrects it if Netflix refused.
        public void Pause() => TogglePause(true);
        public void Resume() => TogglePause(false);

        private void TogglePause(bool pause)
        {
            if (_state is not (PlaybackState.Playing or PlaybackState.Paused)) return;
            SetPaused(pause, raise: false); // the player asked for it: echoing it back would loop
            Command(pause ? NetflixPlayerAction.Pause : NetflixPlayerAction.Play, 0, () => { });
        }

        public void SeekRelative(double seconds) => Command(NetflixPlayerAction.SeekBy, seconds, () =>
        {
            // Show the new position at once; the next poll corrects it.
            var max = DurationSeconds > 0 ? DurationSeconds : double.MaxValue;
            Volatile.Write(ref _position, Math.Clamp(TimePositionSeconds + seconds, 0, max));
        });

        public void Seek(double seconds) => SeekRelative(seconds - TimePositionSeconds);

        public void SetSubtitle(bool enabled) { } // not wired to Netflix yet

        /// <summary>Completes when the commands sent so far have been answered (for tests).</summary>
        public Task WhenCommandsDoneAsync()
        {
            lock (_commandLock) return _commands;
        }

        public async Task PollOnceAsync()
        {
            if (_state is PlaybackState.Idle or PlaybackState.Loading) return;
            var generation = _generation;
            VideoPlaybackStatus? status;
            try
            {
                using var timeout = new CancellationTokenSource(PollTimeout);
                status = await _session.Browser.PollVideoPlaybackAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetflixEngine] Poll failed: {ex.Message}");
                status = null;
            }
            if (generation != _generation || _state == PlaybackState.Idle) return;

            if (status == null)
            {
                if (++_badPolls < BadPollsForFailure) return;
                Fail(generation, "No contact with the Netflix browser.");
                await StopAsync();
                return;
            }
            _badPolls = 0;
            if (!status.Found)
            {
                var limit = Handles(status.Url) ? MissingPollsOnWatchPageForEnd : MissingPollsForEnd;
                if (++_missingPolls < limit) return;
                Console.WriteLine($"[NetflixEngine] No video on the page any more ({status.Url}): playback ended.");
                await StopAsync();
                StreamEnded?.Invoke(this, EventArgs.Empty);
                return;
            }

            _missingPolls = 0;
            if (double.IsFinite(status.CurrentTime)) Volatile.Write(ref _position, status.CurrentTime);
            if (double.IsFinite(status.Duration) && status.Duration > 0 && status.Duration != DurationSeconds)
            {
                Volatile.Write(ref _duration, status.Duration);
                DurationChanged?.Invoke(this, status.Duration);
            }
            SetPaused(status.Paused);
        }

        private void OnPlaybackConfirmed()
        {
            _state = PlaybackState.Playing;
            SetStatus("Playing.");
            Console.WriteLine("[NetflixEngine] The video plays.");
            FileLoaded?.Invoke(this, EventArgs.Empty);
            StartPolling();
        }

        private void OnFlowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(NetflixSpikeViewModel.State) || sender is not NetflixSpikeViewModel flow) return;
            var text = flow.State switch
            {
                NetflixSpikeState.Idle or NetflixSpikeState.LaunchingHiddenBrowser => "Starting Netflix…",
                NetflixSpikeState.CheckingAuth or NetflixSpikeState.SigningIn or NetflixSpikeState.SignedIn => "Signing in to Netflix…",
                NetflixSpikeState.BufferingVideo => "Loading the video…",
                _ => null,
            };
            if (text != null) SetStatus(text);
        }

        private void StartPolling()
        {
            _pollCts?.Cancel();
            var cts = _pollCts = new CancellationTokenSource();
            var interval = PollInterval;
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(interval);
                try
                {
                    while (await timer.WaitForNextTickAsync(cts.Token)) await PollOnceAsync();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.WriteLine($"[NetflixEngine] Polling stopped: {ex.Message}"); }
            });
        }

        private void Command(NetflixPlayerAction action, double seconds, Action onAccepted)
        {
            if (_state is PlaybackState.Idle or PlaybackState.Loading) return;
            lock (_commandLock)
            {
                _commands = _commands.ContinueWith(async _ =>
                {
                    try
                    {
                        var path = await _session.Browser.ControlNetflixPlayerAsync(action, seconds);
                        Console.WriteLine($"[NetflixEngine] {action} {seconds:+0;-0;0} via {path ?? "nothing"}.");
                        if (path != null) onAccepted();
                    }
                    catch (Exception ex) { Console.WriteLine($"[NetflixEngine] {action} failed: {ex.Message}"); }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        private void SetPaused(bool paused, bool raise = true)
        {
            if (_state == PlaybackState.Idle) return;
            _state = paused ? PlaybackState.Paused : PlaybackState.Playing;
            if (paused == _paused) return;
            _paused = paused;
            if (raise) PauseStateChanged?.Invoke(this, paused);
        }

        /// <summary>Detaches the flow at once, then returns the browser to its idle page; frees the session if still current.</summary>
        private async Task ReleaseFlowAsync(int generation)
        {
            _pollCts?.Cancel();
            _pollCts = null;
            var flow = _flow;
            _flow = null;
            if (flow != null)
            {
                flow.PropertyChanged -= OnFlowChanged;
                flow.PlaybackConfirmed -= OnPlaybackConfirmed;
                try { await flow.StopPlaybackAsync(); } // the browser goes back to its idle page
                catch (Exception ex) { Console.WriteLine($"[NetflixEngine] Stopping the flow failed: {ex.Message}"); }
                flow.Dispose();
            }
            if (generation == _generation) _session.IsPlaybackActive = false;
        }

        private void Fail(int generation, string reason)
        {
            if (generation != _generation) return; // a newer play or a stop came since
            _session.IsPlaybackActive = false;
            _state = PlaybackState.Idle;
            if (_failedRaised) return;
            _failedRaised = true;
            Console.WriteLine($"[NetflixEngine] Cannot play: {reason}");
            Failed?.Invoke(this, reason);
        }

        private void SetStatus(string text)
        {
            if (_statusText == text) return;
            _statusText = text;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
