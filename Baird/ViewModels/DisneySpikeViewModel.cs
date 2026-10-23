using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
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

        private readonly ICdpBrowserService _browser;
        private readonly DisneySession _session;
        private readonly DisneyCredentials? _credentials;
        private readonly string? _profileName;
        private CancellationTokenSource? _activeCts;

        public event EventHandler? BackRequested;

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

        /// <summary>Shared with the Netflix spikes: Chromium keeps one session per site in one profile.</summary>
        public string ProfileDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "baird", "browser-profile");

        public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan BufferingTimeout { get; set; } = TimeSpan.FromSeconds(90);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        public string CredentialsStatus => _credentials == null
            ? $"No Disney+ credentials configured (set {DisneyCredentials.UserKey} and {DisneyCredentials.PasswordKey} in ~/.baird/config.ini)."
            : $"Disney+ credentials configured for {_credentials.User}.";

        public DisneySpikeViewModel(DisneyCredentials? credentials = null, string? profileName = null)
            : this(new CdpBrowserService(), credentials, profileName) { }

        public DisneySpikeViewModel(ICdpBrowserService browser, DisneyCredentials? credentials = null, string? profileName = null)
        {
            _browser = browser;
            _credentials = credentials;
            _profileName = profileName;
            if (_browser is CdpBrowserService cdp) cdp.LogMessage += AddLog;
            _session = new DisneySession(browser, (browser as ICdpPageDriver) ?? throw new ArgumentException("browser must also implement ICdpPageDriver"), AddLog);

            AddLog($"[Init] {CredentialsStatus}");
            LaunchCommand = ReactiveCommand.CreateFromTask(LaunchPlaybackFlowAsync);
            StopCommand = ReactiveCommand.CreateFromTask(StopPlaybackAsync);
            BackCommand = ReactiveCommand.Create(() => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        public void AddLog(string message)
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            Console.WriteLine($"[DisneySpike] {message}"); // journal (journalctl -t baird); never contains the password
            if (LogEntries.Count > 200) LogEntries.RemoveAt(0);
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
                SetState(DisneySpikeState.Launching, $"Launching the browser on {TargetUrl} ...");
                // Port 9224: the Netflix spikes use 9222 and 9223. They share the profile, so never run at the same time.
                if (!await _browser.LaunchAsync(_browser.FindBrowserExecutable(), TargetUrl, 9224, ProfileDir, false, ct))
                {
                    Fail($"Browser launch failed: {_browser.LastError ?? "unknown error"} (is a Netflix spike still running?)");
                    return;
                }

                var w = await _browser.CheckWidevineAsync(ct);
                DrmStatus = w.Available
                    ? $"Widevine: key-system access GRANTED ({w.Detail}). This does not show that playback works."
                    : $"Widevine: NOT available ({w.Detail})";
                AddLog("[DRM] " + DrmStatus);

                SetState(DisneySpikeState.SigningIn, "Checking that Disney+ is signed in and a profile is chosen...");
                var problem = await _session.EnsureSignedInAsync(_credentials, _profileName, SignInTimeout, PollInterval, ct);
                if (problem != null)
                {
                    Fail(problem);
                    await _browser.StopAsync();
                    return;
                }

                // Login and the profile picker redirect away from the title: make sure we end on it.
                var now = await _session.ReadStateAsync(ct);
                if (now != null && !now.Url.Contains("/play/", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog($"[Nav] Opening {TargetUrl} (page was {now.Url}).");
                    await _browser.NavigateAsync(TargetUrl, ct);
                }

                await WaitForPlaybackAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Fail($"Playback flow error: {ex.Message}");
                try { await _browser.StopAsync(); } catch { }
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
                        await _browser.StopAsync();
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
                await _browser.StopAsync();
            }
        }

        private async Task RevealAsync(CancellationToken ct)
        {
            SetState(DisneySpikeState.Playing, "currentTime is advancing. Injecting the HUD and revealing the window...");
            await _browser.InjectOverlayScriptAsync(null, ct);
            await _browser.RevealWindowAsync(ct);
            SetState(DisneySpikeState.Playing, $"Playback observed (currentTime advancing, {VideoInfo}); window revealed with the Baird HUD.");
            AddLog("[Reveal] Window revealed.");
        }

        public async Task StopPlaybackAsync()
        {
            AddLog("[Control] Stop requested.");
            _activeCts?.Cancel();
            _activeCts = null;
            await _browser.StopAsync();
            SetState(DisneySpikeState.Stopped, "Stopped. Browser closed.");
            CurrentVideoTime = 0;
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
