using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Baird.Mpv;
using Baird.Playback;

namespace Baird.Services
{
    /// <summary>
    /// Plays ITVX titles for Baird's normal video player: the twin of <see cref="Channel4PlaybackEngine"/>. The video plays in the shared
    /// browser behind Baird's (transparent) window; Baird keeps the keys and this engine turns them into commands to the page's
    /// &lt;video&gt; over CDP. It opens the title's page, presses its Play button (pages do not start by themselves), answers the
    /// "Parental Controls are off" dialog, signs in if the page asks for it, waits for a video to play (an advert counts: the viewer
    /// sees it), hides everything else on the page, polls the video, and returns the browser to its idle page when playback stops.
    /// ITVX resumes at its own saved place, so a start position is ignored. Adverts are shown as they come (handling them is deferred);
    /// the position and length are the programme's own.
    /// </summary>
    public sealed class ItvxPlaybackEngine : IBrowserPlaybackEngine
    {
        /// <summary>Polls in a row without a video before playback counts as ended (the page left the player).</summary>
        private const int MissingPollsForEnd = 6;
        /// <summary>Failed polls in a row (no answer, an error, or no reply within <see cref="PollTimeout"/>) before giving up.</summary>
        private const int BadPollsForFailure = 3;
        private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);
        /// <summary>
        /// Presses of the page's Play button while nothing plays. Measured on the Pi: a press before the page is ready does nothing, and
        /// after "Parental Controls are off" is answered the page can go back to its Play button without starting.
        /// </summary>
        private const int MaxPlayPresses = 8;

        private readonly ItvxService _service;
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
        private string? _pageId;
        private volatile PlaybackState _state = PlaybackState.Idle;

        public ItvxPlaybackEngine(ItvxService service) => _service = service;

        public static bool Handles(string? url) => ItvxCatalogue.IsItvxUrl(url);

        public string Name => ItvxService.Owner;
        public bool CanPlay(string? url) => Handles(url);

        public PlaybackState State => _state;
        public double TimePositionSeconds => Volatile.Read(ref _position);
        public double DurationSeconds => Volatile.Read(ref _duration);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
        /// <summary>How long the page may take to play a video (a sign-in included) before the play fails.</summary>
        public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(90);
        /// <summary>The wait between presses of the page's Play button (and after answering the parental prompt).</summary>
        public TimeSpan PlayPressGap { get; set; } = TimeSpan.FromSeconds(5);

        private string _statusText = "";
        public string StatusText => _statusText;

        public event EventHandler? FileLoaded;
        public event EventHandler? StreamEnded;
        public event EventHandler<bool>? PauseStateChanged;
        public event EventHandler<double>? DurationChanged;
        public event EventHandler? StatusChanged;
        public event EventHandler<string>? Failed;

        /// <summary>True while an advert plays instead of the programme (the position then stays where the programme was).</summary>
        public bool IsAdvertPlaying => _advert;

        public void Play(string url, double? startSeconds = null) => _ = PlayAsync(url); // ITVX resumes at its own place

        public async Task PlayAsync(string url)
        {
            if (_state != PlaybackState.Idle || _ownsBrowser) _ = StopAsync();
            var stopping = _stopping; // the browser must be back on its idle page before it opens the next title
            var generation = Interlocked.Increment(ref _generation);
            var cts = _playCts = new CancellationTokenSource();
            _failedRaised = false;
            _missingPolls = 0;
            _badPolls = 0;
            _paused = false;
            _advert = false;
            _pageId = null;
            Volatile.Write(ref _position, 0);
            Volatile.Write(ref _duration, 0);
            _state = PlaybackState.Loading;
            SetStatus("Starting ITVX…");
            Console.WriteLine($"[ItvxEngine] Playing {url}.");

            try
            {
                if (!ItvxCatalogue.TryParseWatchUrl(url, out _, out var id, out var episodeId))
                {
                    Fail(generation, "This is not an ITVX title.");
                    return;
                }
                // The page stays on this id while it plays; leaving it means ITVX moved on (the next episode).
                _pageId = episodeId ?? id;

                // Another service (Netflix, Disney+, Channel 4) may still be stopping: its tab must be back on its idle page first.
                if (!await _service.Hub.WaitForOtherPlaybackToEndAsync(Name, TimeSpan.FromSeconds(15), cts.Token))
                {
                    Fail(generation, $"{_service.Hub.ActivePlayback} is still playing.");
                    return;
                }
                await stopping;
                if (generation != _generation) return;

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
                if (!await _service.Browser.NavigateAsync(url, cts.Token))
                {
                    await FailAndReleaseAsync(generation, "The browser did not open ITVX's page.");
                    return;
                }

                var outcome = await WaitForVideoAsync(url, generation, cts.Token);
                if (generation != _generation) return;
                if (outcome != null)
                {
                    await FailAndReleaseAsync(generation, outcome);
                    return;
                }

                var styled = await _service.PageDriver.EvaluateAsync(CdpBrowserService.LoadItvxPageStyleScript(), cts.Token);
                if (styled is not { ValueKind: JsonValueKind.True })
                    Console.WriteLine("[ItvxEngine] WARNING: the page style was not applied; ITVX's own page may show.");
                if (generation != _generation) return;
                OnPlaybackConfirmed();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                Console.WriteLine($"[ItvxEngine] Playing failed: {ex}");
                await FailAndReleaseAsync(generation, $"ITVX could not play: {ex.Message}");
            }
        }

        /// <summary>
        /// Waits until a video plays on the page: the programme or an advert (the programme waits paused at 0 while the adverts play), its
        /// time above 0.1 s and advancing between two reads. Presses the page's Play button and answers the parental dialog; signs in once
        /// if the page asks for it, then opens the title again. Returns null when it plays, else the reason it did not.
        /// </summary>
        private async Task<string?> WaitForVideoAsync(string url, int generation, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double? previous = null;
            ItvxPageState? last = null;
            var playPresses = 0;
            var lastPress = TimeSpan.Zero;
            var lastParental = TimeSpan.FromDays(-1);
            var signedIn = false;
            while (sw.Elapsed < StartTimeout)
            {
                if (generation != _generation) return null;
                await Task.Delay(_service.PollInterval, ct);
                var state = await ItvxPlayerControl.ReadStateAsync(_service.PageDriver, ct);
                if (state == null) continue;
                last = state;

                if (state.SignInPage)
                {
                    if (signedIn) return $"ITVX still asks for a sign-in after signing in ({state.Url}).";
                    signedIn = true;
                    SetStatus("Signing in to ITVX…");
                    if (await _service.SignInAsync(ct) is { } signInFailure) return signInFailure;
                    if (generation != _generation) return null;
                    SetStatus("Opening the programme…");
                    if (!await _service.Browser.NavigateAsync(url, ct)) return "The browser did not open ITVX's page.";
                    playPresses = 0;
                    lastPress = sw.Elapsed;
                    previous = null;
                    continue;
                }

                if (state.ParentalPrompt)
                {
                    if (sw.Elapsed - lastParental < PlayPressGap) continue; // answered: give the page time
                    lastParental = sw.Elapsed;
                    lastPress = sw.Elapsed; // the next Play press, if one is needed, waits a full gap
                    var answered = await _service.PageDriver.EvaluateAsync(ItvxPlayerControl.AcceptParentalScript, ct);
                    Console.WriteLine($"[ItvxEngine] Answered the parental-controls prompt ({(answered is { ValueKind: JsonValueKind.String } ? "clicked" : "not there")}).");
                    continue;
                }

                // A title page does not start by itself: press its Play button (again, a few seconds apart, while nothing plays).
                if (state.PlayButton && !state.AdvertPlaying && (!state.HasVideo || state.Paused) && playPresses < MaxPlayPresses
                    && (playPresses == 0 || sw.Elapsed - lastPress > PlayPressGap))
                {
                    playPresses++;
                    lastPress = sw.Elapsed;
                    var pressed = await _service.PageDriver.EvaluateAsync(ItvxPlayerControl.PressPlayScript, ct);
                    Console.WriteLine($"[ItvxEngine] Pressed the page's Play button ({(pressed is { ValueKind: JsonValueKind.String } ? "clicked" : "not there")}).");
                    continue;
                }

                if (!state.HasVideo && !state.AdvertPlaying)
                {
                    previous = null;
                    continue;
                }
                // An advert counts as playing: the viewer sees it, and the programme starts after it.
                var time = state.AdvertPlaying ? state.AdvertTime : state.CurrentTime;
                SetStatus(state.AdvertPlaying ? "Advert…" : "Loading the video…");
                if (time > 0.1 && previous is { } p && time > p)
                {
                    _advert = state.AdvertPlaying; // known from the start, not only from the first poll
                    if (state.Duration > 0) Volatile.Write(ref _duration, state.Duration);
                    Console.WriteLine($"[ItvxEngine] The video plays ({p:F2} s -> {time:F2} s, programme length {state.Duration:F0} s, advert {state.AdvertPlaying}).");
                    return null;
                }
                previous = time;
            }
            return $"No video played within {StartTimeout.TotalSeconds:F0} s. Last page: {last?.Url ?? "unreadable"} \"{last?.Text}\"";
        }

        public void Stop() => _ = StopAsync();

        /// <summary>Stops at once (Idle); the browser's return to its idle page completes with the task.</summary>
        public Task StopAsync()
        {
            Interlocked.Increment(ref _generation);
            if (_state != PlaybackState.Idle) Console.WriteLine("[ItvxEngine] Stopping; the browser goes back to its idle page.");
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

        public void SetSubtitle(bool enabled) { } // not wired to ITVX yet

        /// <summary>Completes when the commands sent so far have been answered (for tests).</summary>
        public Task WhenCommandsDoneAsync()
        {
            lock (_commandLock) return _commands;
        }

        public async Task PollOnceAsync()
        {
            if (_state is PlaybackState.Idle or PlaybackState.Loading) return;
            var generation = _generation;
            ItvxPageState? state;
            using (var timeout = new CancellationTokenSource(PollTimeout))
            {
                try { state = await ItvxPlayerControl.ReadStateAsync(_service.PageDriver, timeout.Token); }
                catch (OperationCanceledException) { state = null; }
            }
            if (generation != _generation || _state == PlaybackState.Idle) return;

            if (state == null)
            {
                if (++_badPolls < BadPollsForFailure) return;
                Fail(generation, "No contact with the ITVX browser.");
                await StopAsync();
                return;
            }
            _badPolls = 0;

            // ITVX moved on by itself (the next episode): the title Baird was playing is over. Baird then plays the next episode of its own
            // list, so the length, position, history and title stay those of the episode on screen.
            var onTitle = _pageId == null || state.Url.Contains("/" + _pageId, StringComparison.Ordinal);
            if (!onTitle || state.Ended || (!state.HasVideo && ++_missingPolls >= MissingPollsForEnd))
            {
                Console.WriteLine(!onTitle ? $"[ItvxEngine] The page left {_pageId} ({state.Url}): playback ended."
                    : state.Ended ? "[ItvxEngine] The video ended." : "[ItvxEngine] No video on the page any more: playback ended.");
                await StopAsync();
                StreamEnded?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (!state.HasVideo) return;
            _missingPolls = 0;

            if (state.AdvertPlaying != _advert)
            {
                _advert = state.AdvertPlaying;
                Console.WriteLine(_advert ? "[ItvxEngine] An advert plays." : "[ItvxEngine] The programme plays.");
            }
            if (!_advert) Volatile.Write(ref _position, state.CurrentTime);
            // The programme video's length is the programme's, even while an advert plays in another element.
            if (state.Duration > 0 && Math.Abs(state.Duration - DurationSeconds) > 1)
            {
                Volatile.Write(ref _duration, state.Duration);
                DurationChanged?.Invoke(this, state.Duration);
            }
            SetPaused(state.Paused && !state.AdvertPlaying);
        }

        private void OnPlaybackConfirmed()
        {
            _state = PlaybackState.Playing;
            SetStatus("Playing.");
            if (DurationSeconds > 0) DurationChanged?.Invoke(this, DurationSeconds);
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
                catch (Exception ex) { Console.WriteLine($"[ItvxEngine] Polling stopped: {ex.Message}"); }
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
                        var path = await ItvxPlayerControl.ControlAsync(_service.PageDriver, action, seconds);
                        Console.WriteLine($"[ItvxEngine] {action} {seconds:+0;-0;0} via {path ?? "nothing"}.");
                    }
                    catch (Exception ex) { Console.WriteLine($"[ItvxEngine] {action} failed: {ex.Message}"); }
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
            Console.WriteLine($"[ItvxEngine] Cannot play: {reason}");
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
