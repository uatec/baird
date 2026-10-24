using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels
{
    public enum NetflixSpikeState
    {
        Idle,
        LaunchingHiddenBrowser,
        CheckingAuth,
        /// <summary>Visible browser is open so the user can sign in by hand.</summary>
        SigningIn,
        /// <summary>Sign-in verified and the browser was closed (profile flushed to disk).</summary>
        SignedIn,
        /// <summary>Terminal: the page is a login page. Flow stopped; user must use Sign in.</summary>
        NotLoggedIn,
        /// <summary>Terminal: launch / CDP / timeout failure. StatusMessage holds the reason.</summary>
        Failed,
        MfaRequired,
        BufferingVideo,
        Playing,
        Stopped
    }

    public class NetflixSpikeViewModel : ReactiveObject, IDisposable
    {
        public const string SignInUrl = "https://www.netflix.com/login";

        private readonly ICdpBrowserService _browserService;
        private CancellationTokenSource? _activeCts;

        public event EventHandler? BackRequested;
        /// <summary>Playback started in Baird-HUD mode: the view must hide Baird's main window and show this HUD on top, with focus.</summary>
        public event Action<NetflixHudViewModel>? HudRequested;
        /// <summary>The video plays (Baird HUD mode): Baird's player takes over from here (see NetflixPlaybackEngine).</summary>
        public event Action? PlaybackConfirmed;

        /// <summary>
        /// True (default): Baird keeps focus and draws the HUD in its own transparent window over the browser.
        /// False (BAIRD_NETFLIX_HUD=html): the old way, the browser is raised with focus and the HTML overlay.
        /// </summary>
        public bool UseBairdHud { get; set; } =
            !string.Equals(Environment.GetEnvironmentVariable("BAIRD_NETFLIX_HUD"), "html", StringComparison.OrdinalIgnoreCase);

        private NetflixHudViewModel? _hud;
        public NetflixHudViewModel? Hud => _hud;

        private string _targetUrl = "https://www.netflix.com/watch/80107105";
        public string TargetUrl
        {
            get => _targetUrl;
            set => this.RaiseAndSetIfChanged(ref _targetUrl, value);
        }

        private NetflixSpikeState _state = NetflixSpikeState.Idle;
        public NetflixSpikeState State
        {
            get => _state;
            set
            {
                this.RaiseAndSetIfChanged(ref _state, value);
                this.RaisePropertyChanged(nameof(IsIdle));
                this.RaisePropertyChanged(nameof(IsLaunching));
                this.RaisePropertyChanged(nameof(IsCheckingAuth));
                this.RaisePropertyChanged(nameof(IsMfaRequired));
                this.RaisePropertyChanged(nameof(IsBuffering));
                this.RaisePropertyChanged(nameof(IsPlaying));
                this.RaisePropertyChanged(nameof(IsStopped));
                this.RaisePropertyChanged(nameof(IsSigningIn));
                this.RaisePropertyChanged(nameof(IsNotLoggedIn));
                this.RaisePropertyChanged(nameof(IsFailed));
                this.RaisePropertyChanged(nameof(IsProblem));
                this.RaisePropertyChanged(nameof(CanLaunch));
                this.RaisePropertyChanged(nameof(CanStop));
                this.RaisePropertyChanged(nameof(StateBadgeColor));
                this.RaisePropertyChanged(nameof(StateDisplayName));
            }
        }

        public bool IsIdle => State == NetflixSpikeState.Idle;
        public bool IsLaunching => State == NetflixSpikeState.LaunchingHiddenBrowser;
        public bool IsCheckingAuth => State == NetflixSpikeState.CheckingAuth;
        public bool IsMfaRequired => State == NetflixSpikeState.MfaRequired;
        public bool IsBuffering => State == NetflixSpikeState.BufferingVideo;
        public bool IsPlaying => State == NetflixSpikeState.Playing;
        public bool IsStopped => State == NetflixSpikeState.Stopped;
        public bool IsSigningIn => State == NetflixSpikeState.SigningIn;
        public bool IsNotLoggedIn => State == NetflixSpikeState.NotLoggedIn;
        public bool IsFailed => State == NetflixSpikeState.Failed;
        /// <summary>True when the on-screen red banner should show StatusMessage.</summary>
        public bool IsProblem => IsNotLoggedIn || IsFailed;

        public bool CanLaunch => State is NetflixSpikeState.Idle or NetflixSpikeState.Stopped
            or NetflixSpikeState.NotLoggedIn or NetflixSpikeState.Failed or NetflixSpikeState.SignedIn;
        public bool CanStop => !CanLaunch;

        public string StateDisplayName => State switch
        {
            NetflixSpikeState.Idle => "Idle",
            NetflixSpikeState.LaunchingHiddenBrowser => "Launching Browser",
            NetflixSpikeState.CheckingAuth => "Checking Authentication",
            NetflixSpikeState.SigningIn => "Sign-in In Progress",
            NetflixSpikeState.SignedIn => "Signed In",
            NetflixSpikeState.NotLoggedIn => "NOT LOGGED IN",
            NetflixSpikeState.Failed => "FAILED",
            NetflixSpikeState.MfaRequired => "MFA Required",
            NetflixSpikeState.BufferingVideo => "Waiting for Video",
            NetflixSpikeState.Playing => "Playing (Revealed)",
            NetflixSpikeState.Stopped => "Stopped",
            _ => State.ToString()
        };

        public string StateBadgeColor => State switch
        {
            NetflixSpikeState.Idle => "#64748B",
            NetflixSpikeState.LaunchingHiddenBrowser => "#38BDF8",
            NetflixSpikeState.CheckingAuth => "#A855F7",
            NetflixSpikeState.SigningIn => "#0EA5E9",
            NetflixSpikeState.SignedIn => "#16A34A",
            NetflixSpikeState.NotLoggedIn => "#DC2626",
            NetflixSpikeState.Failed => "#DC2626",
            NetflixSpikeState.MfaRequired => "#F59E0B",
            NetflixSpikeState.BufferingVideo => "#FBBF24",
            NetflixSpikeState.Playing => "#22C55E",
            NetflixSpikeState.Stopped => "#475569",
            _ => "#64748B"
        };

        private string _statusMessage = "Ready. Use 'Sign in' once, then 'Launch hidden playback'.";
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        private string _drmStatus = "Widevine: not checked yet (checked after browser launch).";
        /// <summary>Separate, factual DRM-availability line. Says nothing about playback.</summary>
        public string DrmStatus
        {
            get => _drmStatus;
            set => this.RaiseAndSetIfChanged(ref _drmStatus, value);
        }

        private string _mfaPin = "";
        public string MfaPin
        {
            get => _mfaPin;
            set
            {
                this.RaiseAndSetIfChanged(ref _mfaPin, value);
                this.RaisePropertyChanged(nameof(MfaPinDisplay));
            }
        }

        public string MfaPinDisplay => string.IsNullOrEmpty(_mfaPin) ? "____" : _mfaPin;

        private bool _isSimulateMfaEnabled;
        /// <summary>When true the MFA step is a UI simulation only: no PIN is sent to any page.</summary>
        public bool IsSimulateMfaEnabled
        {
            get => _isSimulateMfaEnabled;
            set => this.RaiseAndSetIfChanged(ref _isSimulateMfaEnabled, value);
        }

        private double _currentVideoTime;
        public double CurrentVideoTime
        {
            get => _currentVideoTime;
            set => this.RaiseAndSetIfChanged(ref _currentVideoTime, value);
        }

        private double _videoDuration;
        public double VideoDuration
        {
            get => _videoDuration;
            set => this.RaiseAndSetIfChanged(ref _videoDuration, value);
        }

        private string _detectedBrowser = "";
        public string DetectedBrowser
        {
            get => _detectedBrowser;
            set => this.RaiseAndSetIfChanged(ref _detectedBrowser, value);
        }

        public ObservableCollection<string> LogEntries { get; } = new();

        public ReactiveCommand<Unit, Unit> LaunchCommand { get; }
        public ReactiveCommand<Unit, Unit> SignInCommand { get; }
        public ReactiveCommand<Unit, Unit> CompleteSignInCommand { get; }
        public ReactiveCommand<Unit, Unit> StopCommand { get; }
        public ReactiveCommand<Unit, Unit> SubmitMfaCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearMfaCommand { get; }
        public ReactiveCommand<string, Unit> AppendMfaDigitCommand { get; }
        public ReactiveCommand<Unit, Unit> BackspaceMfaCommand { get; }
        public ReactiveCommand<string, Unit> SelectPresetCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        /// <summary>Overridable for tests.</summary>
        public string ProfileDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "baird", "browser-profile");

        /// <summary>How long to wait for the page to classify as something other than Unknown.</summary>
        public TimeSpan AuthCheckTimeout { get; set; } = TimeSpan.FromSeconds(8);
        public TimeSpan BufferingTimeout { get; set; } = TimeSpan.FromSeconds(90);
        public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(400);

        private readonly NetflixCredentials? _credentials;
        private bool _autoSignInAttempted;
        private bool _siteDataCleared;
        private bool _loggedPlaybackError;
        private DateTime _errorGraceUntil = DateTime.MinValue;

        /// <summary>After the site-data clear and reload, how long the old error page may still be on screen before it counts as a second failure.</summary>
        public TimeSpan ErrorRetryGrace { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>True when NETFLIX_USER and NETFLIX_PASS are set in config.ini.</summary>
        public bool HasCredentials => _credentials != null;
        public string CredentialsStatus => _credentials == null
            ? $"No Netflix credentials configured (set {NetflixCredentials.UserKey} and {NetflixCredentials.PasswordKey} in ~/.baird/config.ini)."
            : $"Netflix credentials configured for {_credentials.User}.";

        public NetflixSpikeViewModel(NetflixCredentials? credentials = null) : this(new CdpBrowserService(), credentials)
        {
        }

        public NetflixSpikeViewModel(ICdpBrowserService browserService, NetflixCredentials? credentials = null)
        {
            _browserService = browserService;
            _credentials = credentials;
            if (_browserService is CdpBrowserService cdp)
            {
                cdp.LogMessage += OnBrowserLog;
            }
            _browserService.PageSignalReceived += OnPageSignalReceived;

            DetectedBrowser = _browserService.FindBrowserExecutable();
            AddLog($"[Init] Detected browser executable: {DetectedBrowser}");
            AddLog($"[Init] {CredentialsStatus}");

            LaunchCommand = ReactiveCommand.CreateFromTask(LaunchPlaybackFlowAsync);
            SignInCommand = ReactiveCommand.CreateFromTask(StartSignInAsync);
            CompleteSignInCommand = ReactiveCommand.CreateFromTask(CompleteSignInAsync);
            StopCommand = ReactiveCommand.CreateFromTask(StopPlaybackAsync);
            SubmitMfaCommand = ReactiveCommand.CreateFromTask(SubmitMfaAsync);
            ClearMfaCommand = ReactiveCommand.Create(() => { MfaPin = ""; });
            AppendMfaDigitCommand = ReactiveCommand.Create<string>(AppendMfaPin);
            BackspaceMfaCommand = ReactiveCommand.Create(BackspaceMfaPin);

            SelectPresetCommand = ReactiveCommand.Create<string>(url =>
            {
                TargetUrl = url;
                AddLog($"[Preset] Target URL set to: {url}");
            });

            BackCommand = ReactiveCommand.Create(() =>
            {
                if (IsMfaRequired)
                {
                    _ = StopPlaybackAsync();
                }
                else
                {
                    BackRequested?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        public void AppendMfaPin(string digit)
        {
            if (MfaPin.Length < 8)
            {
                MfaPin += digit;
            }
        }

        public void BackspaceMfaPin()
        {
            if (MfaPin.Length > 0)
            {
                MfaPin = MfaPin.Substring(0, MfaPin.Length - 1);
            }
        }

        /// <summary>
        /// False when the browser is shared (NetflixSession): it already writes its log to the journal once, so this screen
        /// only shows the browser's lines.
        /// </summary>
        public bool LogBrowserToJournal { get; set; } = true;

        private void OnBrowserLog(string message)
        {
            if (LogBrowserToJournal) AddLog(message);
            else AddScreenLog(message);
        }

        private void AddScreenLog(string message)
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (LogEntries.Count > 200) LogEntries.RemoveAt(0);
        }

        public void AddLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogEntries.Add(line);
            Console.WriteLine($"[NetflixSpike] {message}"); // also to the journal (journalctl -t baird); messages never contain the password
            if (LogEntries.Count > 200)
            {
                LogEntries.RemoveAt(0);
            }
        }

        private CancellationToken BeginFlow()
        {
            _activeCts?.Cancel();
            _activeCts = new CancellationTokenSource();
            MfaPin = "";
            CurrentVideoTime = 0;
            VideoDuration = 0;
            return _activeCts.Token;
        }

        /// <summary>
        /// True for Baird's shared Netflix browser (NetflixSession), which stays open: a playback navigates the open browser
        /// instead of starting it, and when playback ends it goes back to <see cref="NetflixSession.IdleUrl"/> instead of closing.
        /// </summary>
        public bool KeepBrowserOpen { get; set; }

        /// <summary>Ends this flow's use of the browser: back to the idle page if it stays open, else close it.</summary>
        private async Task ReleaseBrowserAsync()
        {
            if (KeepBrowserOpen && _browserService.IsRunning)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    if (await _browserService.NavigateAsync(NetflixSession.IdleUrl, cts.Token))
                    {
                        AddLog("[Control] Browser stays open, back on the idle page.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"[Control] Could not return the browser to the idle page ({ex.Message}); closing it.");
                }
            }
            await _browserService.StopAsync();
        }

        /// <summary>Launches the browser (or reuses the open one); on failure moves to Failed with the reason and returns false.</summary>
        private async Task<bool> TryLaunchAsync(string url, bool visible, CancellationToken ct)
        {
            bool launched;
            try
            {
                if (KeepBrowserOpen && !visible && _browserService.IsRunning)
                {
                    AddLog($"[Launch] Reusing the open browser: navigating to {url}");
                    launched = await _browserService.NavigateAsync(url, ct);
                }
                else
                {
                    launched = await _browserService.LaunchAsync(DetectedBrowser, url, 9222, ProfileDir, visible, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Fail($"Browser launch threw: {ex.Message}");
                return false;
            }

            if (ct.IsCancellationRequested) return false;
            if (!launched)
            {
                Fail($"Browser launch failed: {_browserService.LastError ?? "unknown error"} (browser: {DetectedBrowser})");
                return false;
            }
            return true;
        }

        private async Task CheckDrmAsync(CancellationToken ct)
        {
            var w = await _browserService.CheckWidevineAsync(ct);
            DrmStatus = w.Available
                ? $"Widevine: key-system access GRANTED ({w.Detail}). This does not show that playback works."
                : $"Widevine: NOT available ({w.Detail})";
            AddLog("[DRM] " + DrmStatus);
        }

        /// <summary>Visible-browser mode: user signs in by hand against the persistent profile.</summary>
        public async Task StartSignInAsync()
        {
            if (!CanLaunch) return;
            var ct = BeginFlow();
            try
            {
                SetState(NetflixSpikeState.LaunchingHiddenBrowser, "Opening a VISIBLE browser window for sign-in...");
                if (!await TryLaunchAsync(SignInUrl, visible: true, ct)) return;

                await CheckDrmAsync(ct);
                SetState(NetflixSpikeState.SigningIn,
                    "Sign in to Netflix in the browser window (including any verification), then choose 'I've signed in - verify'.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Fail($"Sign-in launch error: {ex.Message}");
            }
        }

        /// <summary>Re-checks auth after manual sign-in; on success closes the browser so the profile is flushed.</summary>
        public async Task CompleteSignInAsync()
        {
            if (State != NetflixSpikeState.SigningIn) return;
            var ct = _activeCts?.Token ?? CancellationToken.None;
            try
            {
                var status = await _browserService.CheckAuthStatusAsync(ct);
                if (status == null)
                {
                    Fail("Could not read the page state over CDP (browser closed or unresponsive?).");
                    return;
                }

                AddLog($"[SignIn] URL: {status.Url} | Kind: {status.Kind} | membership: {status.MembershipStatus ?? "n/a"} | sign-in link: {status.HasSignInLink}");
                switch (status.Kind)
                {
                    case NetflixPageKind.LoginRequired:
                        StatusMessage = "Still on a login page: sign-in not complete. Finish signing in, then verify again.";
                        return;
                    case NetflixPageKind.MfaRequired:
                        StatusMessage = "A verification step is still showing. Complete it in the browser window, then verify again.";
                        return;
                    case NetflixPageKind.Unknown:
                        StatusMessage = $"Cannot tell whether you are signed in (page: {status.Url}). Open the Netflix home page in the window, then verify again.";
                        return;
                }

                await _browserService.StopAsync(); // graceful Browser.close flushes cookies to the profile
                SetState(NetflixSpikeState.SignedIn,
                    $"Looks signed in (page: {status.Url}). Profile saved at {ProfileDir}. Persistence is confirmed only when a hidden launch skips the login page.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Fail($"Sign-in verification error: {ex.Message}");
            }
        }

        public async Task LaunchPlaybackFlowAsync()
        {
            if (!CanLaunch) return;
            var ct = BeginFlow();

            try
            {
                SetState(NetflixSpikeState.LaunchingHiddenBrowser, "Launching browser offscreen at (-4000, -4000)...");
                AddLog($"Starting launch flow for URL: {TargetUrl}");

                if (!await TryLaunchAsync(TargetUrl, visible: false, ct)) return;

                await CheckDrmAsync(ct);

                SetState(NetflixSpikeState.CheckingAuth, "Checking page login state in the hidden browser...");

                if (IsSimulateMfaEnabled)
                {
                    AddLog("[Simulation] MFA SIMULATION active: no real verification will be sent.");
                    SetState(NetflixSpikeState.MfaRequired, "SIMULATED MFA (UI test only, nothing is sent to a page). Enter any PIN.");
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                int nullReads = 0;
                while (sw.Elapsed < AuthCheckTimeout && !ct.IsCancellationRequested)
                {
                    var authStatus = await _browserService.CheckAuthStatusAsync(ct);
                    if (authStatus == null)
                    {
                        nullReads++;
                    }
                    else
                    {
                        var kind = authStatus.Kind;
                        AddLog($"[Auth Check] URL: {authStatus.Url} | Kind: {kind} | Video: {authStatus.IsVideo} t={authStatus.CurrentTime:F2}");

                        if (await HandleKindAsync(kind, authStatus.Url, ct, authStatus.MembershipStatus)) return;
                        if (await HandlePlaybackErrorAsync(authStatus, ct)) return;
                        if (kind is NetflixPageKind.LoggedIn or NetflixPageKind.VideoPlaying) break;
                    }

                    await Task.Delay(PollInterval, ct);
                }

                if (ct.IsCancellationRequested) return;

                if (nullReads > 0 && sw.Elapsed >= AuthCheckTimeout && nullReads * PollInterval.TotalMilliseconds >= AuthCheckTimeout.TotalMilliseconds / 2)
                {
                    Fail("Could not read the page state over CDP within the auth-check window.");
                    await ReleaseBrowserAsync();
                    return;
                }

                await RunBufferingLoopAsync(ct, mfaGrace: TimeSpan.Zero);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Fail($"Playback flow error: {ex.Message}");
                try { await ReleaseBrowserAsync(); } catch { }
            }
        }

        /// <summary>Netflix's own error page (e.g. E100 'not available to watch instantly'). Ends the flow with the reason.</summary>
        private async Task<bool> HandlePlaybackErrorAsync(AuthPageStatus auth, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(auth.PlaybackErrorCode)) return false;

            var reason = string.IsNullOrWhiteSpace(auth.PlaybackErrorMessage) ? "Netflix showed an error page." : auth.PlaybackErrorMessage;
            if (!_loggedPlaybackError)
            {
                _loggedPlaybackError = true;
                AddLog($"[Playback] Netflix error page: code {auth.PlaybackErrorCode}, message \"{reason}\".");
            }

            // On the Pi, Netflix keeps answering E100 until its saved site data is cleared (stale service worker/storage state).
            // Clear it (cookies stay, so the login survives) and reload once.
            if (!_siteDataCleared)
            {
                _siteDataCleared = true;
                AddLog("[Playback] Clearing Netflix's saved site data (cookies kept) and reloading the title once...");
                var detail = await _browserService.ClearSiteDataAsync("https://www.netflix.com", ct);
                AddLog($"[Playback] Site data: {detail}");
                await _browserService.NavigateAsync(TargetUrl, ct);
                _errorGraceUntil = DateTime.UtcNow + ErrorRetryGrace;
                return false; // keep polling: the reloaded page decides
            }

            // The reload is still loading, so the old error page may still be what we are reading. Give it time.
            if (DateTime.UtcNow < _errorGraceUntil) return false;

            Fail($"Netflix refused to play this title: \"{reason}\" (Error Code {auth.PlaybackErrorCode}) even after clearing its site data and reloading. You are signed in. " +
                 $"Try another title: edit the URL above, or set NETFLIX_TARGET_URL in ~/.baird/config.ini. Page: {auth.Url}");
            await ReleaseBrowserAsync();
            return true;
        }

        /// <summary>Handles terminal page kinds. Returns true if the flow was ended/redirected.</summary>
        private async Task<bool> HandleKindAsync(NetflixPageKind kind, string url, CancellationToken ct, string? membershipStatus = null)
        {
            if (kind == NetflixPageKind.LoginRequired)
            {
                if (_credentials != null && !_autoSignInAttempted)
                {
                    _autoSignInAttempted = true; // one attempt per run: never hammer Netflix with a wrong password
                    var outcome = await SignInWithConfiguredCredentialsAsync(ct);
                    if (outcome == null) return false; // signed in, keep going
                    SetState(outcome.Value.State, outcome.Value.Message);
                    if (outcome.Value.State != NetflixSpikeState.MfaRequired) await ReleaseBrowserAsync();
                    return true;
                }

                var why = string.Equals(membershipStatus, "ANONYMOUS", StringComparison.OrdinalIgnoreCase)
                    ? "Netflix sees an anonymous visitor: Baird's browser profile has no signed-in Netflix account."
                    : "Netflix is asking for credentials.";
                var credHint = _credentials == null
                    ? $"No Netflix credentials are configured: set {NetflixCredentials.UserKey} and {NetflixCredentials.PasswordKey} in ~/.baird/config.ini, or press 'Sign in (visible browser)' once."
                    : "The configured credentials were already tried this run.";
                SetState(NetflixSpikeState.NotLoggedIn,
                    $"NOT LOGGED IN: {why} {credHint} The session lives in the browser profile at {ProfileDir}. Playback was not attempted (page: {url}).");
                AddLog("[Auth Check] Login page detected. Stopping flow.");
                await ReleaseBrowserAsync();
                return true;
            }

            if (kind == NetflixPageKind.MfaRequired)
            {
                SetState(NetflixSpikeState.MfaRequired,
                    "Netflix is showing a verification step. Enter the PIN (submission to the page is a best-effort guess at its form fields; unverified).");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Types the configured credentials into the login page. Returns null when signed in (flow continues), otherwise
        /// the terminal state + message. Messages never contain the password.
        /// </summary>
        private async Task<(NetflixSpikeState State, string Message)?> SignInWithConfiguredCredentialsAsync(CancellationToken ct)
        {
            var creds = _credentials!;
            SetState(NetflixSpikeState.CheckingAuth, $"Not signed in. Signing in as {creds.User} with the configured credentials...");
            AddLog($"[SignIn] Using configured credentials for {creds.User} (password not logged).");

            if (!await _browserService.NavigateAsync("https://www.netflix.com/login", ct))
                return (NetflixSpikeState.Failed, "Could not open the Netflix login page over CDP.");

            var submit = await _browserService.SubmitCredentialsAsync(creds.User, creds.Password, ct);
            AddLog($"[SignIn] {submit.Detail}");
            if (!submit.Submitted) return (NetflixSpikeState.Failed, $"Could not fill in the Netflix login form: {submit.Detail}.");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < SignInTimeout && !ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);
                var st = await _browserService.CheckAuthStatusAsync(ct);
                if (st == null) continue;
                AddLog($"[SignIn] URL: {st.Url} | Kind: {st.Kind} | membership: {st.MembershipStatus ?? "n/a"}");

                if (st.Kind == NetflixPageKind.MfaRequired)
                    return (NetflixSpikeState.MfaRequired, "Netflix is asking for a verification code after sign-in. Enter it (best-effort, unverified).");

                if (st.IsSignedIn && st.Kind != NetflixPageKind.LoginRequired)
                {
                    AddLog($"[SignIn] Signed in (membership: {st.MembershipStatus}). Opening {TargetUrl}");
                    await _browserService.NavigateAsync(TargetUrl, ct);
                    return null;
                }
            }

            var err = await _browserService.GetLoginErrorTextAsync(ct);
            return (NetflixSpikeState.NotLoggedIn,
                err != null
                    ? $"NOT LOGGED IN: Netflix rejected the configured credentials for {creds.User}: \"{err}\". Check {NetflixCredentials.UserKey}/{NetflixCredentials.PasswordKey} in ~/.baird/config.ini."
                    : $"NOT LOGGED IN: signing in as {creds.User} did not complete within {SignInTimeout.TotalSeconds:F0}s (no error shown by Netflix; possibly a captcha or an extra check).");
        }

        public async Task SubmitMfaAsync()
        {
            if (string.IsNullOrWhiteSpace(MfaPin))
            {
                StatusMessage = "Please enter a PIN before submitting.";
                return;
            }

            AddLog($"[MFA] Submitting PIN ({MfaPin.Length} digits)...");
            var ct = _activeCts?.Token ?? CancellationToken.None;

            try
            {
                if (!IsSimulateMfaEnabled)
                {
                    var ok = await _browserService.SubmitMfaPinAsync(MfaPin, ct);
                    if (!ok)
                    {
                        Fail("Could not find a PIN input on the page to submit to.");
                        return;
                    }
                }
                else
                {
                    AddLog("[MFA Simulation] Simulated PIN 'accepted' (nothing was sent).");
                }

                SetState(NetflixSpikeState.BufferingVideo, "PIN submitted. Waiting to observe video.currentTime advance...");
                await RunBufferingLoopAsync(ct, mfaGrace: TimeSpan.FromSeconds(6));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Fail($"MFA submit error: {ex.Message}");
            }
        }

        /// <summary>
        /// Waits for evidence of playback: video.currentTime must be above the threshold AND increase
        /// between two successive polls. Re-classifies the page each loop so a login page can never sit here.
        /// </summary>
        private async Task RunBufferingLoopAsync(CancellationToken ct, TimeSpan mfaGrace)
        {
            SetState(NetflixSpikeState.BufferingVideo, "Waiting to observe video.currentTime advance in the hidden browser...");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double? previous = null;
            while (sw.Elapsed < BufferingTimeout && !ct.IsCancellationRequested)
            {
                var auth = await _browserService.CheckAuthStatusAsync(ct);
                if (auth != null)
                {
                    var kind = auth.Kind;
                    if (kind == NetflixPageKind.LoginRequired ||
                        (kind == NetflixPageKind.MfaRequired && sw.Elapsed >= mfaGrace))
                    {
                        if (await HandleKindAsync(kind, auth.Url, ct, auth.MembershipStatus)) return;
                    }

                    if (await HandlePlaybackErrorAsync(auth, ct)) return;
                }

                var status = await _browserService.PollVideoPlaybackAsync(ct);
                if (status != null)
                {
                    CurrentVideoTime = status.CurrentTime;
                    VideoDuration = status.Duration;

                    if (status.Found)
                    {
                        StatusMessage = $"Video element found. currentTime {status.CurrentTime:F2}s (readyState {status.ReadyState}, paused: {status.Paused}); need it to advance.";

                        if (status.CurrentTime > NetflixPageClassifier.PlayingThresholdSeconds &&
                            previous.HasValue && status.CurrentTime > previous.Value)
                        {
                            AddLog($"[Playback] currentTime advanced {previous.Value:F2}s -> {status.CurrentTime:F2}s.");
                            await OnPlaybackConfirmedActiveAsync(ct);
                            return;
                        }
                        previous = status.CurrentTime;
                    }
                    else
                    {
                        StatusMessage = $"Waiting for an HTML5 video element on the page... ({status.Title})";
                    }
                }

                await Task.Delay(PollInterval, ct);
            }

            if (!ct.IsCancellationRequested && State == NetflixSpikeState.BufferingVideo)
            {
                Fail($"No video playback observed within {BufferingTimeout.TotalSeconds:F0}s (video.currentTime never advanced). Page was not a login page.");
                await ReleaseBrowserAsync();
            }
        }

        private async Task OnPlaybackConfirmedActiveAsync(CancellationToken ct)
        {
            if (UseBairdHud)
            {
                SetState(NetflixSpikeState.Playing, "video.currentTime is advancing. Hiding Netflix's UI and opening Baird's HUD...");
                var styled = await _browserService.InjectOverlayScriptAsync(CdpBrowserService.LoadNetflixPageStyleScript(), ct);
                AddLog(styled ? "[Reveal] Netflix page style applied." : "[Reveal] WARNING: the Netflix page style was not applied; Netflix's own UI may show.");

                PlaybackConfirmed?.Invoke();
                _hud = new NetflixHudViewModel(_browserService) { Title = HudTitle };
                _hud.ExitRequested += () => _ = StopPlaybackAsync();
                HudRequested?.Invoke(_hud);

                SetState(NetflixSpikeState.Playing, "Playback observed (currentTime advancing); Baird HUD shown over the browser.");
                AddLog("[Reveal] Baird HUD requested (Baird keeps focus).");
                return;
            }

            SetState(NetflixSpikeState.Playing, "video.currentTime is advancing. Injecting HUD and moving window to (0,0)...");
            AddLog("[Reveal] Injecting baird-overlay.js...");

            await _browserService.InjectOverlayScriptAsync(null, ct);
            await _browserService.RevealWindowAsync(ct);

            SetState(NetflixSpikeState.Playing, "Playback observed (currentTime advancing); window revealed with Baird HUD.");
            AddLog("[Reveal] Window revealed.");
        }

        /// <summary>Title shown on Baird's HUD.</summary>
        public string HudTitle { get; set; } = "Netflix";

        /// <summary>Removes this flow's handlers from the browser service, which can be shared (see NetflixSession).</summary>
        public void Dispose()
        {
            if (_browserService is CdpBrowserService cdp) cdp.LogMessage -= OnBrowserLog;
            _browserService.PageSignalReceived -= OnPageSignalReceived;
        }

        private void OnPageSignalReceived(string payload) => RxApp.MainThreadScheduler.Schedule(() => OnPageSignal(payload));

        /// <summary>Handles a message from the overlay. Back on the remote sends {"type":"exit"}: close Netflix and return to Baird.</summary>
        private void OnPageSignal(string payload)
        {
            string? type = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    type = t.GetString();
                }
            }
            catch (JsonException) { }

            if (type == "exit" && CanStop)
            {
                AddLog("[Control] Exit requested from the Netflix overlay (remote Back).");
                _ = StopPlaybackAsync();
            }
        }

        public async Task StopPlaybackAsync()
        {
            AddLog("[Control] Abort / Stop requested.");
            _activeCts?.Cancel();
            _activeCts = null;

            await ReleaseBrowserAsync();
            _hud?.Close();
            _hud = null;

            SetState(NetflixSpikeState.Stopped, KeepBrowserOpen ? "Stopped. The browser stays open on its idle page." : "Stopped. Browser closed.");
            CurrentVideoTime = 0;
            MfaPin = "";
        }

        private void Fail(string message)
        {
            SetState(NetflixSpikeState.Failed, message);
            AddLog("[FAILED] " + message);
        }

        private void SetState(NetflixSpikeState newState, string message)
        {
            State = newState;
            StatusMessage = message;
        }
    }
}
