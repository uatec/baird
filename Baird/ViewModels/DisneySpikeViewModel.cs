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
    public enum DisneySpikeState
    {
        Idle,
        Launching,
        SigningIn,
        BufferingVideo,
        Playing,
        /// <summary>Terminal: the reason is in StatusMessage (shown in red, also in the journal).</summary>
        Failed,
        Stopped
    }

    /// <summary>
    /// Spike: play a Disney+ title in the hidden Chromium (Widevine) and reveal it over Baird once video.currentTime advances.
    /// Same shape as <see cref="NetflixSpikeViewModel"/>: shares its browser profile, window class and labwc rule, so the two
    /// spikes never run at the same time. Failures end in Failed with the cause; nothing fails silently.
    /// </summary>
    public class DisneySpikeViewModel : ReactiveObject
    {
        public const string DefaultTargetUrl = "https://www.disneyplus.com/en-gb/play/d830ec57-499e-498c-b6b3-2b85a18a5f51";

        private readonly DisneyService _service;
        private readonly ICdpBrowserService _browser;
        private readonly DisneySession _session;
        private readonly bool _shared;
        private Action<string>? _logHandler;
        private Action<string>? _signalHandler;
        private CancellationTokenSource? _activeCts;

        public event EventHandler? BackRequested;

        /// <summary>The video plays and Baird's player has taken over (see <see cref="UseBairdHud"/>): raised once per play.</summary>
        public event Action? PlaybackConfirmed;

        /// <summary>
        /// True (the engine's setting): Baird keeps focus and its own video player draws the HUD while the browser shows the
        /// video behind the transparent main window. Disney+'s controls are hidden and the window is not raised.
        /// False (the Disney spike tab): the browser is raised with focus and the injected HTML overlay is used.
        /// </summary>
        public bool UseBairdHud { get; set; }

        private string _targetUrl = DefaultTargetUrl;
        public string TargetUrl { get => _targetUrl; set => this.RaiseAndSetIfChanged(ref _targetUrl, value); }

        private DisneySpikeState _state = DisneySpikeState.Idle;
        public DisneySpikeState State
        {
            get => _state;
            private set
            {
                this.RaiseAndSetIfChanged(ref _state, value);
                this.RaisePropertyChanged(nameof(CanLaunch));
                this.RaisePropertyChanged(nameof(CanStop));
                this.RaisePropertyChanged(nameof(IsFailed));
                this.RaisePropertyChanged(nameof(StateDisplayName));
                this.RaisePropertyChanged(nameof(StateBadgeColor));
            }
        }

        public bool CanLaunch => State is DisneySpikeState.Idle or DisneySpikeState.Failed or DisneySpikeState.Stopped;
        public bool CanStop => !CanLaunch;
        public bool IsFailed => State == DisneySpikeState.Failed;

        public string StateDisplayName => State switch
        {
            DisneySpikeState.Launching => "Launching Browser",
            DisneySpikeState.SigningIn => "Signing In",
            DisneySpikeState.BufferingVideo => "Waiting for Video",
            DisneySpikeState.Playing => "Playing (Revealed)",
            DisneySpikeState.Failed => "FAILED",
            _ => State.ToString()
        };

        public string StateBadgeColor => State switch
        {
            DisneySpikeState.Launching => "#38BDF8",
            DisneySpikeState.SigningIn => "#A855F7",
            DisneySpikeState.BufferingVideo => "#FBBF24",
            DisneySpikeState.Playing => "#22C55E",
            DisneySpikeState.Failed => "#DC2626",
            DisneySpikeState.Stopped => "#475569",
            _ => "#64748B"
        };

        private string _statusMessage = "Ready. Press 'Launch hidden playback'.";
        public string StatusMessage { get => _statusMessage; private set => this.RaiseAndSetIfChanged(ref _statusMessage, value); }

        private string _drmStatus = "Widevine: not checked yet (checked after browser launch).";
        public string DrmStatus { get => _drmStatus; private set => this.RaiseAndSetIfChanged(ref _drmStatus, value); }

        private double _currentVideoTime;
        public double CurrentVideoTime { get => _currentVideoTime; private set => this.RaiseAndSetIfChanged(ref _currentVideoTime, value); }

        private string _videoInfo = "";
        /// <summary>Resolution of the playing stream, e.g. "854 px wide" (Widevine L3 on the Pi gets standard definition).</summary>
        public string VideoInfo { get => _videoInfo; private set => this.RaiseAndSetIfChanged(ref _videoInfo, value); }

        public ObservableCollection<string> LogEntries { get; } = new();

        public ReactiveCommand<Unit, Unit> LaunchCommand { get; }
        public ReactiveCommand<Unit, Unit> StopCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        /// <summary>The browser profile (shared with Netflix: Chromium keeps one session per site in one profile).</summary>
        public string ProfileDir { get => _service.Hub.ProfileDir; set => _service.Hub.ProfileDir = value; }

        public TimeSpan SignInTimeout { get => _service.SignInTimeout; set => _service.SignInTimeout = value; }
        public TimeSpan PollInterval { get => _service.PollInterval; set => _service.PollInterval = value; }
        public TimeSpan BufferingTimeout { get; set; } = TimeSpan.FromSeconds(90);

        /// <summary>
        /// True for Baird's shared browser (<see cref="BrowserHub"/>), which stays open: when playback ends the tab goes back to
        /// <see cref="DisneyService.IdleUrl"/> instead of the browser closing.
        /// </summary>
        public bool KeepBrowserOpen { get; set; }

        public string CredentialsStatus => _service.Credentials == null
            ? $"No Disney+ credentials configured (set {DisneyCredentials.UserKey} and {DisneyCredentials.PasswordKey} in ~/.baird/config.ini)."
            : $"Disney+ credentials configured for {_service.Credentials.User}.";

        public DisneySpikeViewModel(DisneyCredentials? credentials = null, string? profileName = null)
            : this(new CdpBrowserService(), credentials, profileName) { }

        /// <summary>A Disney+-only browser (tests, no shared browser). It closes the browser when playback ends.</summary>
        public DisneySpikeViewModel(ICdpBrowserService browser, DisneyCredentials? credentials = null, string? profileName = null)
            : this(new DisneyService(new BrowserHub(browser) { Port = 9224 }, credentials, profileName), shared: false)
        {
            if (browser is CdpBrowserService cdp) cdp.LogMessage += AddLog;
        }

        /// <summary>Disney+ on Baird's shared browser. The service's log reaches the journal once, from where it was created.</summary>
        public DisneySpikeViewModel(DisneyService service) : this(service, shared: true) { KeepBrowserOpen = true; }

        private DisneySpikeViewModel(DisneyService service, bool shared)
        {
            _service = service;
            _shared = shared;
            _browser = service.Browser;
            _session = service.Session;
            _logHandler = shared ? AddScreenLog : AddLog; // shared: the service's lines reach the journal once, from where it was created
            _service.Log += _logHandler;

            // The overlay's Back/Escape key sends {"type":"exit"} through the CDP binding the browser service always adds.
            // Without this handler Back does nothing on Disney+ (the overlay returns early when the binding exists).
            _signalHandler = payload => RxApp.MainThreadScheduler.Schedule(() => OnPageSignal(payload));
            _browser.PageSignalReceived += _signalHandler;

            AddLog($"[Init] {CredentialsStatus}");
            LaunchCommand = ReactiveCommand.CreateFromTask(LaunchPlaybackFlowAsync);
            StopCommand = ReactiveCommand.CreateFromTask(StopPlaybackAsync);
            BackCommand = ReactiveCommand.Create(() => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        /// <summary>Removes this flow's handlers from the shared browser and service (a new flow is created for each play).</summary>
        public void Dispose()
        {
            if (_logHandler != null) _service.Log -= _logHandler;
            if (_signalHandler != null) _browser.PageSignalReceived -= _signalHandler;
            _logHandler = null;
            _signalHandler = null;
        }

        private void AddScreenLog(string message)
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (LogEntries.Count > 200) LogEntries.RemoveAt(0);
        }

        public void AddLog(string message)
        {
            AddScreenLog(message);
            Console.WriteLine($"[DisneySpike] {message}"); // journal (journalctl -t baird); never contains the password
        }

        public async Task LaunchPlaybackFlowAsync()
        {
            if (!CanLaunch) return;
            _activeCts?.Cancel();
            _activeCts = new CancellationTokenSource();
            var ct = _activeCts.Token;
            CurrentVideoTime = 0;
            VideoInfo = "";

            try
            {
                SetState(DisneySpikeState.SigningIn, "Opening the browser and checking that Disney+ is signed in...");
                var failure = await _service.EnsureReadyAsync(ct, beginPlayback: true);
                if (failure != null)
                {
                    Fail(failure.StartsWith("FAILED", StringComparison.Ordinal) ? failure : "FAILED: " + failure);
                    await ReleaseBrowserAsync();
                    return;
                }

                var w = await _browser.CheckWidevineAsync(ct);
                DrmStatus = w.Available
                    ? $"Widevine: key-system access GRANTED ({w.Detail}). This does not show that playback works."
                    : $"Widevine: NOT available ({w.Detail})";
                AddLog("[DRM] " + DrmStatus);

                // The browser is ready on Disney+'s idle page, and the tab is ours until playback ends (EnsureReadyAsync marked it
                // as playing inside the gate).
                SetState(DisneySpikeState.Launching, $"Opening {TargetUrl} ...");
                await _browser.NavigateAsync(TargetUrl, ct);

                await WaitForPlaybackAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Fail($"Playback flow error: {ex.Message}");
                try { await ReleaseBrowserAsync(); } catch { }
            }
        }

        /// <summary>Waits for evidence of playback: currentTime above the threshold AND increasing between two polls.</summary>
        private async Task WaitForPlaybackAsync(CancellationToken ct)
        {
            SetState(DisneySpikeState.BufferingVideo, "Waiting to observe video.currentTime advance...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double? previous = null;
            DisneyPageStatus? last = null;
            while (sw.Elapsed < BufferingTimeout && !ct.IsCancellationRequested)
            {
                var st = await _session.ReadStateAsync(ct);
                if (st != null)
                {
                    last = st;
                    if (st.Kind == DisneyPageKind.LoginRequired || st.Kind == DisneyPageKind.ProfilePicker)
                    {
                        Fail($"Disney+ went back to {st.Kind} while loading the title (page: {st.Url}).");
                        await ReleaseBrowserAsync();
                        return;
                    }
                    if (st.IsVideo)
                    {
                        CurrentVideoTime = st.CurrentTime;
                        VideoInfo = $"{st.VideoWidth} px wide";
                        StatusMessage = $"Video found. currentTime {st.CurrentTime:F2}s (paused: {st.Paused}); need it to advance.";
                        if (st.CurrentTime > DisneyPageClassifier.PlayingThresholdSeconds && previous.HasValue && st.CurrentTime > previous.Value)
                        {
                            AddLog($"[Playback] currentTime advanced {previous.Value:F2}s -> {st.CurrentTime:F2}s ({VideoInfo}).");
                            await RevealAsync(ct);
                            return;
                        }
                        previous = st.CurrentTime;
                    }
                    else
                    {
                        StatusMessage = $"Waiting for a playing video... (page: {st.Url})";
                    }
                }
                await Task.Delay(PollInterval, ct);
            }

            if (!ct.IsCancellationRequested && State == DisneySpikeState.BufferingVideo)
            {
                Fail($"No video playback observed within {BufferingTimeout.TotalSeconds:F0}s. Last page: {last?.Url ?? "unreadable"} \"{last?.Text}\"");
                await ReleaseBrowserAsync();
            }
        }

        private async Task RevealAsync(CancellationToken ct)
        {
            if (UseBairdHud)
            {
                SetState(DisneySpikeState.Playing, "currentTime is advancing. Hiding Disney+'s controls for Baird's player...");
                var styled = await _browser.InjectOverlayScriptAsync(CdpBrowserService.LoadDisneyPageStyleScript(), ct);
                AddLog(styled ? "[Reveal] Disney+ page style applied." : "[Reveal] WARNING: the Disney+ page style was not applied; Disney+'s own controls may show.");
                SetState(DisneySpikeState.Playing, $"Playback observed (currentTime advancing, {VideoInfo}); Baird's player has the controls.");
                AddLog("[Reveal] Playback confirmed (Baird keeps focus).");
                PlaybackConfirmed?.Invoke();
                return;
            }

            SetState(DisneySpikeState.Playing, "currentTime is advancing. Injecting the HUD and revealing the window...");
            await _browser.InjectOverlayScriptAsync(null, ct);
            await _browser.RevealWindowAsync(ct);
            SetState(DisneySpikeState.Playing, $"Playback observed (currentTime advancing, {VideoInfo}); window revealed with the Baird HUD.");
            AddLog("[Reveal] Window revealed.");
        }

        /// <summary>Handles a message from the overlay. Back on the remote sends {"type":"exit"}: close the browser and return to Baird.</summary>
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
                AddLog("[Control] Exit requested from the overlay (remote Back).");
                _ = StopPlaybackAsync();
            }
        }

        public async Task StopPlaybackAsync()
        {
            AddLog("[Control] Stop requested.");
            _activeCts?.Cancel();
            _activeCts = null;
            await ReleaseBrowserAsync();
            SetState(DisneySpikeState.Stopped, KeepBrowserOpen ? "Stopped. The browser stays open on its idle page." : "Stopped. Browser closed.");
            CurrentVideoTime = 0;
        }

        /// <summary>Ends this flow's use of the browser: back to the idle page if it stays open, else close it.</summary>
        private async Task ReleaseBrowserAsync()
        {
            // The playing flag is cleared LAST: the other service waits for it, and must find the tab back on its idle page.
            try
            {
                if (KeepBrowserOpen && _browser.IsRunning)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await _browser.HideWindowAsync(cts.Token); // a revealed window is above Baird: put Baird back on top
                        if (await _browser.NavigateAsync(DisneyService.IdleUrl, cts.Token))
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
                await _browser.StopAsync();
            }
            finally
            {
                _service.IsPlaybackActive = false;
            }
        }

        private void Fail(string message)
        {
            SetState(DisneySpikeState.Failed, message);
            AddLog("[FAILED] " + message);
        }

        private void SetState(DisneySpikeState state, string message)
        {
            State = state;
            StatusMessage = message;
        }
    }
}
