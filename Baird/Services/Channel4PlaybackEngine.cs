using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Baird.Mpv;
using Baird.Playback;

namespace Baird.Services
{
    /// <summary>
    /// Plays Channel 4 programmes for Baird's normal video player: the twin of <see cref="DisneyPlaybackEngine"/>. The video plays in
    /// the shared browser behind Baird's (transparent) window; Baird keeps the keys and this engine turns them into commands to the
    /// page's &lt;video&gt; over CDP. It opens the programme's on-demand page, waits for a video to play
    /// (an advert counts: the viewer sees it), hides everything else on the page, polls the video, and returns the browser to its idle
    /// page when playback stops. Channel 4 resumes at its own saved place, so a start position is ignored.
    /// <para>When the page asks for a sign-in, the play fails with "sign in to Channel 4" as its <see cref="FailureAction"/>: OK on the
    /// failure screen shows Channel 4's sign-in page for the viewer (<see cref="Channel4Service.SignInByHandAsync"/>).</para>
    /// </summary>
    public sealed class Channel4PlaybackEngine : IFailureActionEngine
    {
        /// <summary>Polls in a row without a video before playback counts as ended (the page left the player).</summary>
        private const int MissingPollsForEnd = 6;
        /// <summary>Failed polls in a row (no answer, an error, or no reply within <see cref="PollTimeout"/>) before giving up.</summary>
        private const int BadPollsForFailure = 3;
        private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);

        private readonly Channel4Service _service;
        private readonly object _commandLock = new();
        private Task _commands = Task.CompletedTask;
        private CancellationTokenSource? _playCts;
        private CancellationTokenSource? _pollCts;
        private int _generation;
        private int _missingPolls;
        private int _badPolls;
        private Task _stopping = Task.CompletedTask;
        private bool _failedRaised;
        private bool _paused;
        private bool _advert;
        private bool _ownsBrowser;
        private double _position;
        private double _duration;
        private double _runtime;
        private string? _programmeId;
        private string? _slug;
        private string? _failureAction;
        private volatile PlaybackState _state = PlaybackState.Idle;

        public Channel4PlaybackEngine(Channel4Service service) => _service = service;

        public static bool Handles(string? url) => Channel4Catalogue.IsChannel4Url(url);

        public string Name => Channel4Service.Owner;
        public bool CanPlay(string? url) => Handles(url);

        public PlaybackState State => _state;
        public double TimePositionSeconds => Volatile.Read(ref _position);
        public double DurationSeconds => Volatile.Read(ref _duration);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
        /// <summary>How long the page may take to play a video (adverts included) before the play fails.</summary>
        public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(90);

        private string _statusText = "";
        public string StatusText => _statusText;

        public event EventHandler? FileLoaded;
        public event EventHandler? StreamEnded;
        public event EventHandler<bool>? PauseStateChanged;
        public event EventHandler<double>? DurationChanged;
        public event EventHandler? StatusChanged;
        public event EventHandler<string>? Failed;

        /// <summary>What OK offers on the failure screen when Channel 4 asked for a sign-in.</summary>
        public const string SignInAction = "sign in to Channel 4";

        public string? FailureAction => _failureAction;

        /// <summary>
        /// Shows Channel 4's sign-in page for the viewer (see <see cref="Channel4Service.SignInByHandAsync"/>). Null when they signed in
        /// and the programme can be played again; else why not (and OK offers the sign-in again).
        /// </summary>
        public async Task<string?> RunFailureActionAsync(CancellationToken ct = default)
        {
            if (_failureAction != SignInAction || _slug == null) return "There is nothing to do.";
            _failureAction = null;
            await _stopping; // the failed play is still giving the tab back
            Console.WriteLine("[Channel4Engine] Showing Channel 4's sign-in page for the viewer.");
            var outcome = await _service.SignInByHandAsync(Channel4Catalogue.ProgrammeUrl(_slug), ct);
            Console.WriteLine($"[Channel4Engine] Sign-in by hand: {(outcome.IsSignedIn ? "signed in" : outcome.Message)}");
            if (!outcome.IsSignedIn) _failureAction = SignInAction;
            return outcome.Message;
        }

        /// <summary>True while an advert plays instead of the programme (the position then stays where the programme was).</summary>
        public bool IsAdvertPlaying => _advert;

        public void Play(string url, double? startSeconds = null) => _ = PlayAsync(url); // Channel 4 resumes at its own place

        public async Task PlayAsync(string url)
        {
            if (_state != PlaybackState.Idle || _ownsBrowser) _ = StopAsync();
            var stopping = _stopping; // the browser must be back on its idle page before it opens the next programme
            var generation = Interlocked.Increment(ref _generation);
            var cts = _playCts = new CancellationTokenSource();
            _failedRaised = false;
            _missingPolls = 0;
            _badPolls = 0;
            _paused = false;
            _advert = false;
            _runtime = 0;
            _programmeId = null;
            _failureAction = null;
            Volatile.Write(ref _position, 0);
            Volatile.Write(ref _duration, 0);
            _state = PlaybackState.Loading;
            SetStatus("Starting Channel 4…");
            Console.WriteLine($"[Channel4Engine] Playing {url}.");

            try
            {
                // Another service (Netflix, Disney+) may still be stopping: its tab must be back on its idle page before Channel 4 takes it.
                if (!await _service.Hub.WaitForOtherPlaybackToEndAsync(Name, TimeSpan.FromSeconds(15), cts.Token))
                {
                    Fail(generation, $"{_service.Hub.ActivePlayback} is still playing.");
                    return;
                }
                await stopping;
                if (generation != _generation) return;

                // What to play: a programme page (a film from search) is read for its episode; an on-demand page plays as it is.
                if (!Channel4Catalogue.TryParsePageUrl(url, out var slug, out var programmeId))
                {
                    Fail(generation, "This is not a Channel 4 programme.");
                    return;
                }
                _slug = slug;
                if (programmeId == null)
                {
                    SetStatus("Finding what to play…");
                    Channel4Programme programme;
                    try { programme = await _service.Catalogue.GetProgrammeAsync(slug, cts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Fail(generation, $"Channel 4's page could not be read: {ex.Message}");
                        return;
                    }
                    if (generation != _generation) return;
                    var first = programme.Episodes.FirstOrDefault();
                    if (first == null)
                    {
                        Fail(generation, $"Channel 4 has nothing to watch for {programme.Title} now.");
                        return;
                    }
                    programmeId = first.ProgrammeId;
                }
                _programmeId = programmeId;
                _runtime = _service.Catalogue.KnownRuntimeSeconds(programmeId);
                var playUrl = Channel4Catalogue.OnDemandUrl(slug, programmeId);

                var failure = await _service.BeginPlaybackAsync(cts.Token);
                if (generation != _generation)
                {
                    if (failure == null) await _service.EndPlaybackAsync(); // a newer play or a stop came while waiting for the tab
                    return;
                }
                if (failure != null)
                {
                    Fail(generation, failure);
                    return;
                }
                _ownsBrowser = true;

                SetStatus("Opening the programme…");
                if (!await _service.Browser.NavigateAsync(playUrl, cts.Token))
                {
                    await FailAndReleaseAsync(generation, "The browser did not open Channel 4's page.");
                    return;
                }

                var outcome = await WaitForVideoAsync(playUrl, generation, cts.Token);
                if (generation != _generation) return;
                if (outcome != null)
                {
                    if (outcome == Channel4Service.NotSignedInMessage) _failureAction = SignInAction; // OK on the failure screen
                    await FailAndReleaseAsync(generation, outcome);
                    return;
                }

                var styled = await _service.PageDriver.EvaluateAsync(CdpBrowserService.LoadChannel4PageStyleScript(), cts.Token);
                if (styled is not { ValueKind: JsonValueKind.True })
                    Console.WriteLine("[Channel4Engine] WARNING: the page style was not applied; Channel 4's own page may show.");
                if (generation != _generation) return;
                OnPlaybackConfirmed();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                Console.WriteLine($"[Channel4Engine] Playing failed: {ex}");
                await FailAndReleaseAsync(generation, $"Channel 4 could not play: {ex.Message}");
            }
        }

        /// <summary>
        /// Waits until a video plays on the page (its time above 0.1 s and advancing between two reads). Returns null when it plays, else
        /// the reason it did not (the page asking for a sign-in is one).
        /// </summary>
        private async Task<string?> WaitForVideoAsync(string playUrl, int generation, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double? previous = null;
            Channel4PageState? last = null;
            while (sw.Elapsed < StartTimeout)
            {
                if (generation != _generation) return null;
                await Task.Delay(_service.PollInterval, ct);
                var state = await Channel4PlayerControl.ReadStateAsync(_service.PageDriver, ct);
                if (state == null) continue;
                last = state;

                // Baird does not sign in (Channel 4 refuses automated sign-ins); the failure screen offers the viewer the sign-in page.
                if (state.NeedsSignIn) return Channel4Service.NotSignedInMessage;

                if (!state.HasVideo)
                {
                    previous = null;
                    continue;
                }
                SetStatus(state.OtherVideoPlaying ? "Advert…" : "Loading the video…");
                if (state.CurrentTime > 0.1 && previous is { } p && state.CurrentTime > p)
                {
                    Console.WriteLine($"[Channel4Engine] The video plays ({p:F2} s -> {state.CurrentTime:F2} s, length {state.Duration:F0} s, advert {state.OtherVideoPlaying}).");
                    return null;
                }
                previous = state.CurrentTime;
            }
            return $"No video played within {StartTimeout.TotalSeconds:F0} s. Last page: {last?.Url ?? "unreadable"} \"{last?.Text}\"";
        }

        public void Stop() => _ = StopAsync();

        /// <summary>Stops at once (Idle); the browser's return to its idle page completes with the task.</summary>
        public Task StopAsync()
        {
            Interlocked.Increment(ref _generation);
            if (_state != PlaybackState.Idle) Console.WriteLine("[Channel4Engine] Stopping; the browser goes back to its idle page.");
            _state = PlaybackState.Idle;
            _playCts?.Cancel();
            _pollCts?.Cancel();
            _pollCts = null;
            var release = _ownsBrowser;
            _ownsBrowser = false;
            var previous = _stopping;
            return _stopping = StopAfterAsync(previous, release);
        }

        private async Task StopAfterAsync(Task previous, bool release)
        {
            await previous;
            if (release) await _service.EndPlaybackAsync();
        }

        // The state changes at once, so a quick second press is not lost; the next poll corrects it if the page refused. Not raised as
        // an event: the player asked for it, and echoing it back would make VideoPlayer's pause handler flip the state again.
        public void Pause() => TogglePause(true);
        public void Resume() => TogglePause(false);

        private void TogglePause(bool pause)
        {
            if (_state is not (PlaybackState.Playing or PlaybackState.Paused)) return;
            SetPaused(pause, raise: false);
            Command(pause ? NetflixPlayerAction.Pause : NetflixPlayerAction.Play, 0);
        }

        public void SeekRelative(double seconds)
        {
            if (_state is PlaybackState.Idle or PlaybackState.Loading || _advert) return; // an advert cannot be skipped
            var max = DurationSeconds > 0 ? DurationSeconds : double.MaxValue;
            Volatile.Write(ref _position, Math.Clamp(TimePositionSeconds + seconds, 0, max));
            Command(NetflixPlayerAction.SeekBy, seconds);
        }

        public void Seek(double seconds) => SeekRelative(seconds - TimePositionSeconds);

        public void SetSubtitle(bool enabled) { } // not wired to Channel 4 yet

        /// <summary>Completes when the commands sent so far have been answered (for tests).</summary>
        public Task WhenCommandsDoneAsync()
        {
            lock (_commandLock) return _commands;
        }

        public async Task PollOnceAsync()
        {
            if (_state is PlaybackState.Idle or PlaybackState.Loading) return;
            var generation = _generation;
            Channel4PageState? state;
            using (var timeout = new CancellationTokenSource(PollTimeout))
            {
                try { state = await Channel4PlayerControl.ReadStateAsync(_service.PageDriver, timeout.Token); }
                catch (OperationCanceledException) { state = null; }
            }
            if (generation != _generation || _state == PlaybackState.Idle) return;

            if (state == null)
            {
                if (++_badPolls < BadPollsForFailure) return;
                Fail(generation, "No contact with the Channel 4 browser.");
                await StopAsync();
                return;
            }
            _badPolls = 0;

            // Channel 4 moved on by itself (autoplay of the next episode): the programme Baird was playing is over. Baird then plays
            // the next episode of its own list, so the length, position, history and title stay those of the episode on screen.
            var onProgramme = _programmeId == null || state.Url.Contains("/on-demand/" + _programmeId, StringComparison.Ordinal);
            if (!onProgramme || state.Ended || (!state.HasVideo && ++_missingPolls >= MissingPollsForEnd))
            {
                Console.WriteLine(!onProgramme ? $"[Channel4Engine] The page left {_programmeId} ({state.Url}): playback ended."
                    : state.Ended ? "[Channel4Engine] The video ended." : "[Channel4Engine] No video on the page any more: playback ended.");
                await StopAsync();
                StreamEnded?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (!state.HasVideo) return;
            _missingPolls = 0;

            var advert = Channel4PlayerControl.IsAdvert(state, _runtime > 0 ? _runtime : DurationSeconds);
            if (advert != _advert)
            {
                _advert = advert;
                Console.WriteLine(advert ? "[Channel4Engine] An advert plays." : "[Channel4Engine] The programme plays.");
            }
            if (!advert)
            {
                Volatile.Write(ref _position, state.CurrentTime);
                // The page's own length is exact; the programme page only gives minutes.
                if (state.Duration > 0 && Math.Abs(state.Duration - DurationSeconds) > 1)
                {
                    Volatile.Write(ref _duration, state.Duration);
                    DurationChanged?.Invoke(this, state.Duration);
                }
            }
            SetPaused(state.Paused && !state.OtherVideoPlaying);
        }

        private void OnPlaybackConfirmed()
        {
            _state = PlaybackState.Playing;
            SetStatus("Playing.");
            if (_runtime > 0)
            {
                Volatile.Write(ref _duration, _runtime);
                DurationChanged?.Invoke(this, _runtime);
            }
            FileLoaded?.Invoke(this, EventArgs.Empty);
            StartPolling();
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
                catch (Exception ex) { Console.WriteLine($"[Channel4Engine] Polling stopped: {ex.Message}"); }
            });
        }

        private void Command(NetflixPlayerAction action, double seconds)
        {
            if (_state is PlaybackState.Idle or PlaybackState.Loading) return;
            lock (_commandLock)
            {
                _commands = _commands.ContinueWith(async _ =>
                {
                    try
                    {
                        var path = await Channel4PlayerControl.ControlAsync(_service.PageDriver, action, seconds);
                        Console.WriteLine($"[Channel4Engine] {action} {seconds:+0;-0;0} via {path ?? "nothing"}.");
                    }
                    catch (Exception ex) { Console.WriteLine($"[Channel4Engine] {action} failed: {ex.Message}"); }
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

        private async Task FailAndReleaseAsync(int generation, string reason)
        {
            if (generation != _generation) return;
            var release = _ownsBrowser;
            _ownsBrowser = false;
            Fail(generation, reason);
            if (release) await (_stopping = ReleaseAfterAsync(_stopping));
        }

        private async Task ReleaseAfterAsync(Task previous)
        {
            await previous;
            await _service.EndPlaybackAsync();
        }

        private void Fail(int generation, string reason)
        {
            if (generation != _generation) return; // a newer play or a stop came since
            _state = PlaybackState.Idle;
            if (_failedRaised) return;
            _failedRaised = true;
            Console.WriteLine($"[Channel4Engine] Cannot play: {reason}");
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
