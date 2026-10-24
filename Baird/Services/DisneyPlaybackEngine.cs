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
    /// Plays Disney+ titles for Baird's normal video player: the twin of <see cref="NetflixPlaybackEngine"/>. The video plays in
    /// the shared browser behind Baird's (transparent) window; Baird keeps the keys and this engine turns them into commands to
    /// Disney+'s player over CDP. It reads the title's page to find what to play (for a series, the next episode for this
    /// profile) and its real length, runs the proven playback flow (<see cref="DisneySpikeViewModel"/>), polls the video, and
    /// returns the browser to its idle page when playback stops. Disney+ resumes at its own saved place, so a start position is ignored.
    /// </summary>
    public sealed class DisneyPlaybackEngine : IBrowserPlaybackEngine, IPromptingEngine
    {
        /// <summary>Polls in a row without a video before playback counts as ended (Disney+ left the player page).</summary>
        private const int MissingPollsForEnd = 3;
        /// <summary>The same on the player page (about a minute): Disney+ switching titles can drop the video for a moment.</summary>
        private const int MissingPollsOnPlayerPageForEnd = 120;
        /// <summary>Failed polls in a row (no answer, an error, or no reply within <see cref="PollTimeout"/>) before giving up.</summary>
        private const int BadPollsForFailure = 2;
        private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);

        private readonly DisneyService _service;
        private readonly Func<DisneySpikeViewModel> _createFlow;
        private readonly object _commandLock = new();
        private Task _commands = Task.CompletedTask;
        private DisneySpikeViewModel? _flow;
        private CancellationTokenSource? _pollCts;
        private int _generation;
        private int _missingPolls;
        private int _badPolls;
        private Task _stopping = Task.CompletedTask;
        private bool _failedRaised;
        private bool _paused;
        private double _position;
        private double _duration;
        private double _runtime;
        private string? _playId; // the title being played; the page leaving it means this title is over
        private volatile PlaybackState _state = PlaybackState.Idle;

        public DisneyPlaybackEngine(DisneyService service, Func<DisneySpikeViewModel> createFlow)
        {
            _service = service;
            _createFlow = createFlow;
        }

        public static bool Handles(string? url) => DisneySearchParser.IsDisneyUrl(url);

        public string Name => DisneyService.Owner;
        public bool CanPlay(string? url) => Handles(url);

        public PlaybackState State => _state;
        public double TimePositionSeconds => Volatile.Read(ref _position);
        public double DurationSeconds => Volatile.Read(ref _duration);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        private string _statusText = "";
        public string StatusText => _statusText;

        public event EventHandler? FileLoaded;
        public event EventHandler? StreamEnded;
        public event EventHandler<bool>? PauseStateChanged;
        public event EventHandler<double>? DurationChanged;
        public event EventHandler? StatusChanged;
        public event EventHandler<string>? Failed;

        private string? _prompt;
        /// <summary>The question Disney+ has put on the video ("Skip intro", "Next episode"), or null.</summary>
        public string? PromptText => _prompt;
        public event EventHandler? PromptChanged;

        /// <summary>Answers the prompt on screen (the viewer pressed OK). False when there is none.</summary>
        public bool ActivatePrompt()
        {
            if (_prompt == null || _state is PlaybackState.Idle or PlaybackState.Loading) return false;
            var label = _prompt;
            lock (_commandLock)
            {
                _commands = _commands.ContinueWith(async _ =>
                {
                    try
                    {
                        var clicked = await DisneyPlayerControl.ActivatePromptAsync(_service.PageDriver);
                        Console.WriteLine($"[DisneyEngine] Prompt \"{label}\": {(clicked ? "clicked" : "not on screen any more")}.");
                        SetPrompt(null); // the next poll shows what is there now
                    }
                    catch (Exception ex) { Console.WriteLine($"[DisneyEngine] Prompt \"{label}\" failed: {ex.Message}"); }
                }, TaskScheduler.Default).Unwrap();
            }
            return true;
        }

        private void SetPrompt(string? prompt)
        {
            if (_prompt == prompt) return;
            _prompt = prompt;
            if (prompt != null) Console.WriteLine($"[DisneyEngine] Disney+ asks: \"{prompt}\".");
            PromptChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Play(string url, double? startSeconds = null) => _ = PlayAsync(url); // Disney+ resumes at its own place

        public async Task PlayAsync(string url)
        {
            if (_flow != null || _state != PlaybackState.Idle) _ = StopAsync();
            var stopping = _stopping; // the browser must be back on its idle page before it opens the next title
            var generation = Interlocked.Increment(ref _generation);
            _failedRaised = false;
            _missingPolls = 0;
            _badPolls = 0;
            _paused = false;
            _runtime = 0;
            _playId = null;
            SetPrompt(null);
            Volatile.Write(ref _position, 0);
            Volatile.Write(ref _duration, 0);
            _state = PlaybackState.Loading;
            SetStatus("Starting Disney+…");
            Console.WriteLine($"[DisneyEngine] Playing {url}.");

            try
            {
                // Another service (Netflix) may still be stopping: its tab must be back on its idle page before Disney+ takes it.
                if (!await _service.Hub.WaitForOtherPlaybackToEndAsync(Name, TimeSpan.FromSeconds(15)))
                {
                    Fail(generation, $"{_service.Hub.ActivePlayback} is still playing.");
                    return;
                }
                await stopping;
                if (generation != _generation) return;

                // What to play: an entity page (from search) is read for the play id and the real length; a play URL plays as it is.
                var playUrl = url;
                if (DisneySearchParser.TryParseEntityUrl(url, out var entityId, out var isFilm))
                {
                    SetStatus("Finding what to play…");
                    using var resolveCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    DisneyPlayTarget? target;
                    string? resolveFailure;
                    try { (target, resolveFailure) = await _service.ResolvePlayTargetAsync(entityId, isFilm, 0, resolveCts.Token); }
                    catch (OperationCanceledException) { (target, resolveFailure) = (null, "Disney+ did not answer within 2 minutes."); }
                    if (generation != _generation) return;
                    if (target == null)
                    {
                        Fail(generation, resolveFailure ?? "Disney+ gave nothing to play.");
                        return;
                    }
                    playUrl = target.PlayUrl;
                    _playId = target.PlayId;
                    _runtime = target.RuntimeSeconds;
                    SetStatus("Starting Disney+…");
                }
                else if (DisneySearchParser.TryParsePlayId(url, out var directId))
                {
                    // An episode from the episode list: its length was read with that list. (Else the length stays unknown.)
                    _playId = directId;
                    _runtime = _service.KnownRuntimeSeconds(directId);
                }

                var flow = _createFlow();
                flow.UseBairdHud = true;
                flow.KeepBrowserOpen = true;
                flow.TargetUrl = playUrl;
                flow.PropertyChanged += OnFlowChanged;
                flow.PlaybackConfirmed += OnPlaybackConfirmed;
                _flow = flow;

                await flow.LaunchPlaybackFlowAsync();
                if (generation != _generation || _state != PlaybackState.Loading) return;

                // The flow settled without the video playing: it says why.
                var reason = flow.StatusMessage is { Length: > 0 } m ? m : "Disney+ did not start the video.";
                await ReleaseFlowAsync(generation);
                Fail(generation, reason);
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                Console.WriteLine($"[DisneyEngine] Playing failed: {ex}");
                await ReleaseFlowAsync(generation);
                Fail(generation, $"Disney+ could not play: {ex.Message}");
            }
        }

        public void Stop() => _ = StopAsync();

        /// <summary>Stops at once (Idle); the browser's return to its idle page completes with the task.</summary>
        public Task StopAsync()
        {
            var generation = Interlocked.Increment(ref _generation);
            if (_state != PlaybackState.Idle) Console.WriteLine("[DisneyEngine] Stopping; the browser goes back to its idle page.");
            _state = PlaybackState.Idle;
            SetPrompt(null);
            var previous = _stopping;
            return _stopping = StopAfterAsync(previous, generation);
        }

        private async Task StopAfterAsync(Task previous, int generation)
        {
            await previous;
            await ReleaseFlowAsync(generation);
        }

        // The state changes at once, so a quick second press is not lost; the next poll corrects it if Disney+ refused. Not raised as
        // an event: the player asked for it, and echoing it back made VideoPlayer's pause handler flip the state again.
        public void Pause() => TogglePause(true);
        public void Resume() => TogglePause(false);

        private void TogglePause(bool pause)
        {
            if (_state is not (PlaybackState.Playing or PlaybackState.Paused)) return;
            SetPaused(pause, raise: false);
            Command(pause ? NetflixPlayerAction.Pause : NetflixPlayerAction.Play, 0, () => { });
        }

        public void SeekRelative(double seconds) => Command(NetflixPlayerAction.SeekBy, seconds, () =>
        {
            // Show the new position at once; the next poll corrects it.
            var max = DurationSeconds > 0 ? DurationSeconds : double.MaxValue;
            Volatile.Write(ref _position, Math.Clamp(TimePositionSeconds + seconds, 0, max));
        });

        public void Seek(double seconds) => SeekRelative(seconds - TimePositionSeconds);

        public void SetSubtitle(bool enabled) { } // not wired to Disney+ yet

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
                status = await DisneyPlayerControl.PollAsync(_service.PageDriver, timeout.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DisneyEngine] Poll failed: {ex.Message}");
                status = null;
            }
            if (generation != _generation || _state == PlaybackState.Idle) return;

            if (status == null)
            {
                if (++_badPolls < BadPollsForFailure) return;
                Fail(generation, "No contact with the Disney+ browser.");
                await StopAsync();
                return;
            }
            _badPolls = 0;
            if (!status.Found || status.Ended)
            {
                var limit = status.Url.Contains("/play/", StringComparison.Ordinal) ? MissingPollsOnPlayerPageForEnd : MissingPollsForEnd;
                if (!status.Ended && ++_missingPolls < limit) return;
                Console.WriteLine(status.Ended ? "[DisneyEngine] The video ended." : "[DisneyEngine] No video on the page any more: playback ended.");
                await StopAsync();
                StreamEnded?.Invoke(this, EventArgs.Empty);
                return;
            }

            _missingPolls = 0;
            // Disney+ moved on by itself (autoplay, or "Next episode" pressed): the title Baird was playing is over. Baird then plays
            // the next episode of its own list, so the length, position, history and title stay those of the episode on screen.
            if (_playId != null && status.Url.Length > 0 && !status.Url.Contains("/play/" + _playId, StringComparison.Ordinal))
            {
                Console.WriteLine($"[DisneyEngine] The page left {_playId} ({status.Url}): playback ended.");
                await StopAsync();
                StreamEnded?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (double.IsFinite(status.CurrentTime)) Volatile.Write(ref _position, status.CurrentTime);
            SetPrompt(status.Prompt);
            SetPaused(status.Paused);
        }

        private void OnPlaybackConfirmed()
        {
            _state = PlaybackState.Playing;
            SetStatus("Playing.");
            Console.WriteLine($"[DisneyEngine] The video plays (length {(_runtime > 0 ? _runtime.ToString("F0") + " s" : "unknown")}).");
            // The page's video.duration is the buffered end, not the length: the length comes from Disney's API.
            if (_runtime > 0)
            {
                Volatile.Write(ref _duration, _runtime);
                DurationChanged?.Invoke(this, _runtime);
            }
            FileLoaded?.Invoke(this, EventArgs.Empty);
            StartPolling();
        }

        private void OnFlowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DisneySpikeViewModel.State) || sender is not DisneySpikeViewModel flow) return;
            var text = flow.State switch
            {
                DisneySpikeState.SigningIn => "Starting Disney+…",
                DisneySpikeState.Launching => "Opening the title…",
                DisneySpikeState.BufferingVideo => "Loading the video…",
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
                catch (Exception ex) { Console.WriteLine($"[DisneyEngine] Polling stopped: {ex.Message}"); }
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
                        var path = await DisneyPlayerControl.ControlAsync(_service.PageDriver, action, seconds, _runtime);
                        Console.WriteLine($"[DisneyEngine] {action} {seconds:+0;-0;0} via {path ?? "nothing"}.");
                        if (path != null) onAccepted();
                    }
                    catch (Exception ex) { Console.WriteLine($"[DisneyEngine] {action} failed: {ex.Message}"); }
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

        /// <summary>Detaches the flow at once, then returns the browser to its idle page; frees the hub if still current.</summary>
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
                try { await flow.StopPlaybackAsync(); } // the browser goes back to its idle page, then the playing flag is cleared
                catch (Exception ex) { Console.WriteLine($"[DisneyEngine] Stopping the flow failed: {ex.Message}"); }
                flow.Dispose();
            }
            if (generation == _generation) _service.IsPlaybackActive = false;
        }

        private void Fail(int generation, string reason)
        {
            if (generation != _generation) return; // a newer play or a stop came since
            _service.IsPlaybackActive = false;
            _state = PlaybackState.Idle;
            SetPrompt(null);
            if (_failedRaised) return;
            _failedRaised = true;
            Console.WriteLine($"[DisneyEngine] Cannot play: {reason}");
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
