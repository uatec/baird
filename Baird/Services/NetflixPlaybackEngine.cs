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
    /// It runs the proven playback flow (<see cref="NetflixPlaybackFlow"/>) on the browser that <see cref="NetflixSession"/>
    /// keeps open, polls the video, and returns the browser to its idle page when playback stops.
    /// <para>When Netflix asks for a verification code (after Baird's sign-in, or on the title), the play waits on
    /// <see cref="CodeRequest"/> for the viewer to type it, then carries on. When Netflix is not signed in and Baird cannot sign in
    /// itself, the failure offers OK to sign in by hand on the TV (<see cref="FailureAction"/>).</para>
    /// </summary>
    public sealed class NetflixPlaybackEngine : ICodeEntryEngine, IFailureActionEngine
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
        /// <summary>Codes Netflix may refuse in one play before it fails (Netflix locks the account after a few wrong ones).</summary>
        private const int CodeTries = 3;
        private const string FirstCodeRequest = NetflixSession.VerificationCodeMessage;
        private const string NotSignedInMessage = "Netflix is not signed in on this Baird.";
        private static readonly string CodesRefusedMessage = $"Netflix refused the verification code {CodeTries} times.";

        private readonly NetflixSession _session;
        private readonly Func<NetflixPlaybackFlow> _createFlow;
        private readonly object _commandLock = new();
        private Task _commands = Task.CompletedTask;
        private NetflixPlaybackFlow? _flow;
        private CancellationTokenSource? _pollCts;
        private int _generation;
        private int _missingPolls;
        private int _badPolls;
        private Task _stopping = Task.CompletedTask;
        private bool _failedRaised;
        private bool _paused;
        private int _codeTries;
        /// <summary>The code asked for is for the session's own sign-in (else for the flow's verification step on the title).</summary>
        private bool _codeForSession;
        private string? _url;
        private double _position;
        private double _duration;
        private volatile PlaybackState _state = PlaybackState.Idle;

        public NetflixPlaybackEngine(NetflixSession session, Func<NetflixPlaybackFlow> createFlow)
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

        private volatile string? _codeRequest;
        /// <summary>Netflix's verification step is waiting for the code the viewer was sent (the text says so), else null.</summary>
        public string? CodeRequest => _codeRequest;
        public event EventHandler? CodeRequestChanged;

        public void Play(string url, double? startSeconds = null) => _ = PlayAsync(url); // Netflix resumes at its own place

        public async Task PlayAsync(string url)
        {
            if (_flow != null || _state != PlaybackState.Idle) _ = StopAsync();
            var stopping = _stopping; // the browser must be back on its idle page before it opens the next title
            var generation = Interlocked.Increment(ref _generation);
            _url = url;
            _failedRaised = false;
            _failureAction = null;
            _missingPolls = 0;
            _badPolls = 0;
            _paused = false;
            _codeTries = 0;
            _codeForSession = false;
            SetCodeRequest(null);
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
                    // The viewer is here now: a sign-in that failed in the background is tried again (it may ask them for a code).
                    try { failure = await _session.EnsureReadyAsync(cts.Token, signInAgain: true); }
                    catch (OperationCanceledException) { failure = "The Netflix browser did not get ready within 2 minutes."; }
                }
                if (generation != _generation) return;
                if (failure != null)
                {
                    if (_session.NeedsVerificationCode)
                    {
                        _codeForSession = true;
                        AskForCode();
                        return;
                    }
                    Fail(generation, failure, offerSignIn: _session.NeedsSignIn);
                    return;
                }

                await StartFlowAsync(url, generation);
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                Console.WriteLine($"[NetflixEngine] Playing failed: {ex}");
                await ReleaseFlowAsync(generation);
                Fail(generation, $"Netflix could not play: {ex.Message}");
            }
        }

        /// <summary>The browser is ready (signed in, profile chosen): runs the playback flow on the title.</summary>
        private async Task StartFlowAsync(string url, int generation)
        {
            var flow = _createFlow();
            flow.UseBairdHud = true;
            flow.KeepBrowserOpen = true;
            flow.TargetUrl = url;
            flow.PropertyChanged += OnFlowChanged;
            flow.PlaybackConfirmed += OnPlaybackConfirmed;
            _flow = flow;

            await flow.LaunchPlaybackFlowAsync();
            await SettleAsync(flow, generation);
        }

        /// <summary>
        /// The flow returned: the video plays (nothing to do), Netflix wants a verification code (ask the viewer, keeping the
        /// browser on that page), or it failed and says why.
        /// </summary>
        private async Task SettleAsync(NetflixPlaybackFlow flow, int generation)
        {
            if (generation != _generation || _state != PlaybackState.Loading) return;
            if (flow.IsMfaRequired && _codeTries < CodeTries)
            {
                AskForCode();
                return;
            }

            if (flow.State == NetflixPlaybackFlowState.NotLoggedIn) Console.WriteLine($"[NetflixEngine] {flow.StatusMessage}");
            var signInProblem = flow.IsMfaRequired || flow.State == NetflixPlaybackFlowState.NotLoggedIn;
            var reason = flow.IsMfaRequired ? CodesRefusedMessage
                : flow.State == NetflixPlaybackFlowState.NotLoggedIn ? NotSignedInMessage
                : flow.StatusMessage is { Length: > 0 } m ? m : "Netflix did not start the video.";
            await ReleaseFlowAsync(generation);
            Fail(generation, reason, offerSignIn: signInProblem);
        }

        private void AskForCode()
        {
            Console.WriteLine($"[NetflixEngine] Netflix asks for a verification code (try {_codeTries + 1} of {CodeTries}).");
            SetCodeRequest(_codeTries == 0 ? FirstCodeRequest : "That code did not work. Type the code Netflix sent you again.");
        }

        public async Task SubmitCodeAsync(string code)
        {
            var generation = _generation;
            var flow = _flow;
            if (_codeRequest == null || _state != PlaybackState.Loading || string.IsNullOrWhiteSpace(code)) return;
            if (!_codeForSession && flow == null) return;
            _codeTries++;
            SetCodeRequest(null);
            SetStatus("Checking the code…");
            Console.WriteLine($"[NetflixEngine] Sending the verification code ({code.Length} digits)."); // never the code itself
            try
            {
                if (_codeForSession)
                {
                    // Netflix stopped Baird's own sign-in to ask for the code: the browser is still on that page.
                    string? failure;
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                    {
                        try { failure = await _session.SubmitVerificationCodeAsync(code, cts.Token); }
                        catch (OperationCanceledException) { failure = "Netflix did not answer within 2 minutes."; }
                    }
                    if (generation != _generation) return;
                    if (failure == null)
                    {
                        _codeForSession = false;
                        await StartFlowAsync(_url!, generation);
                        return;
                    }
                    if (_session.NeedsVerificationCode && _codeTries < CodeTries)
                    {
                        AskForCode();
                        return;
                    }
                    Fail(generation, _session.NeedsVerificationCode ? CodesRefusedMessage : failure, offerSignIn: true);
                    return;
                }

                flow!.MfaPin = code;
                await flow.SubmitMfaAsync();
                await SettleAsync(flow, generation);
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                Console.WriteLine($"[NetflixEngine] Sending the code failed: {ex}");
                await ReleaseFlowAsync(generation);
                Fail(generation, $"Netflix could not take the code: {ex.Message}");
            }
        }

        /// <summary>What OK offers on the failure screen when Netflix is not signed in (or refused the codes).</summary>
        public const string SignInAction = "sign in to Netflix";

        private volatile string? _failureAction;
        public string? FailureAction => _failureAction;

        /// <summary>
        /// Shows Netflix's sign-in page for the viewer (see <see cref="NetflixSession.SignInByHandAsync"/>). Null when they signed
        /// in and the title can be played again; else why not (and OK offers the sign-in again).
        /// </summary>
        public async Task<string?> RunFailureActionAsync(CancellationToken ct = default)
        {
            if (_failureAction != SignInAction) return "There is nothing to do.";
            _failureAction = null;
            await _stopping; // the failed play is still giving the browser back
            Console.WriteLine("[NetflixEngine] Showing Netflix's sign-in page for the viewer.");
            var outcome = await _session.SignInByHandAsync(ct);
            Console.WriteLine($"[NetflixEngine] Sign-in by hand: {outcome ?? "signed in"}");
            if (outcome != null) _failureAction = SignInAction;
            return outcome;
        }

        public void Stop() => _ = StopAsync();

        /// <summary>Stops at once (Idle); the browser's return to its idle page completes with the task.</summary>
        public Task StopAsync()
        {
            var generation = Interlocked.Increment(ref _generation);
            if (_state != PlaybackState.Idle) Console.WriteLine("[NetflixEngine] Stopping; the browser goes back to its idle page.");
            _state = PlaybackState.Idle;
            SetCodeRequest(null);
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
            if (e.PropertyName != nameof(NetflixPlaybackFlow.State) || sender is not NetflixPlaybackFlow flow) return;
            var text = flow.State switch
            {
                NetflixPlaybackFlowState.Idle or NetflixPlaybackFlowState.LaunchingHiddenBrowser => "Starting Netflix…",
                NetflixPlaybackFlowState.CheckingAuth or NetflixPlaybackFlowState.SigningIn or NetflixPlaybackFlowState.SignedIn => "Signing in to Netflix…",
                NetflixPlaybackFlowState.BufferingVideo => "Loading the video…",
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

        /// <param name="offerSignIn">A sign-in problem: OK on the failure screen offers Netflix's sign-in page (<see cref="SignInAction"/>).</param>
        private void Fail(int generation, string reason, bool offerSignIn = false)
        {
            if (generation != _generation) return; // a newer play or a stop came since
            if (offerSignIn) _failureAction = SignInAction;
            _session.IsPlaybackActive = false;
            _state = PlaybackState.Idle;
            SetCodeRequest(null);
            if (_failedRaised) return;
            _failedRaised = true;
            Console.WriteLine($"[NetflixEngine] Cannot play: {reason}");
            Failed?.Invoke(this, reason);
        }

        private void SetCodeRequest(string? text)
        {
            if (_codeRequest == text) return;
            _codeRequest = text;
            CodeRequestChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SetStatus(string text)
        {
            if (_statusText == text) return;
            _statusText = text;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
