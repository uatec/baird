using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels
{
    public enum NetflixSpikeState
    {
        Idle,
        LaunchingHiddenBrowser,
        CheckingAuth,
        MfaRequired,
        BufferingVideo,
        Playing,
        Stopped
    }

    public class NetflixSpikeViewModel : ReactiveObject
    {
        private readonly ICdpBrowserService _browserService;
        private CancellationTokenSource? _activeCts;

        public event EventHandler? BackRequested;

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

        public bool CanLaunch => State == NetflixSpikeState.Idle || State == NetflixSpikeState.Stopped;
        public bool CanStop => State != NetflixSpikeState.Idle && State != NetflixSpikeState.Stopped;

        public string StateDisplayName => State switch
        {
            NetflixSpikeState.Idle => "Idle",
            NetflixSpikeState.LaunchingHiddenBrowser => "Launching Hidden Browser",
            NetflixSpikeState.CheckingAuth => "Checking Authentication",
            NetflixSpikeState.MfaRequired => "MFA Required",
            NetflixSpikeState.BufferingVideo => "Buffering Video",
            NetflixSpikeState.Playing => "Playing (Revealed)",
            NetflixSpikeState.Stopped => "Stopped",
            _ => State.ToString()
        };

        public string StateBadgeColor => State switch
        {
            NetflixSpikeState.Idle => "#64748B",
            NetflixSpikeState.LaunchingHiddenBrowser => "#38BDF8",
            NetflixSpikeState.CheckingAuth => "#A855F7",
            NetflixSpikeState.MfaRequired => "#F59E0B",
            NetflixSpikeState.BufferingVideo => "#FBBF24",
            NetflixSpikeState.Playing => "#22C55E",
            NetflixSpikeState.Stopped => "#475569",
            _ => "#64748B"
        };

        private string _statusMessage = "Ready to launch hidden browser playback flow.";
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
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
        public ReactiveCommand<Unit, Unit> StopCommand { get; }
        public ReactiveCommand<Unit, Unit> SubmitMfaCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearMfaCommand { get; }
        public ReactiveCommand<string, Unit> AppendMfaDigitCommand { get; }
        public ReactiveCommand<Unit, Unit> BackspaceMfaCommand { get; }
        public ReactiveCommand<string, Unit> SelectPresetCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public NetflixSpikeViewModel() : this(new CdpBrowserService())
        {
        }

        public NetflixSpikeViewModel(ICdpBrowserService browserService)
        {
            _browserService = browserService;
            if (_browserService is CdpBrowserService cdp)
            {
                cdp.LogMessage += msg => AddLog(msg);
            }

            DetectedBrowser = _browserService.FindBrowserExecutable();
            AddLog($"[Init] Detected browser executable: {DetectedBrowser}");

            LaunchCommand = ReactiveCommand.CreateFromTask(LaunchPlaybackFlowAsync);
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

        public void AddLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogEntries.Add(line);
            if (LogEntries.Count > 200)
            {
                LogEntries.RemoveAt(0);
            }
        }

        public async Task LaunchPlaybackFlowAsync()
        {
            if (!CanLaunch) return;

            _activeCts?.Cancel();
            _activeCts = new CancellationTokenSource();
            var ct = _activeCts.Token;

            MfaPin = "";
            CurrentVideoTime = 0;
            VideoDuration = 0;

            SetState(NetflixSpikeState.LaunchingHiddenBrowser, "Launching Chromium browser offscreen at (-4000, -4000)...");
            AddLog($"Starting launch flow for URL: {TargetUrl}");

            var profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "baird", "browser-profile");

            var launched = await _browserService.LaunchAsync(DetectedBrowser, TargetUrl, 9222, profileDir, ct);
            if (!launched || ct.IsCancellationRequested)
            {
                SetState(NetflixSpikeState.Stopped, "Failed to launch hidden browser. Verify browser executable and CDP port.");
                return;
            }

            SetState(NetflixSpikeState.CheckingAuth, "Automating credential & session check in hidden browser...");
            AddLog("Browser is hidden offscreen. Checking page auth state...");

            // If user checked simulated MFA mode, trigger MFA immediately
            if (IsSimulateMfaEnabled)
            {
                AddLog("[Simulation] MFA simulation active. Requesting in-app PIN entry.");
                SetState(NetflixSpikeState.MfaRequired, "MFA Verification Required (Simulated). Enter PIN using the virtual keyboard.");
                return;
            }

            // Real auth check loop (up to 8 seconds)
            var authCheckStopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (authCheckStopwatch.Elapsed < TimeSpan.FromSeconds(8) && !ct.IsCancellationRequested)
            {
                var authStatus = await _browserService.CheckAuthStatusAsync(ct);
                if (authStatus != null)
                {
                    AddLog($"[Auth Check] URL: {authStatus.Url} | MFA: {authStatus.IsMfa} | Login: {authStatus.IsLogin} | Video: {authStatus.IsVideo}");

                    if (authStatus.IsMfa)
                    {
                        SetState(NetflixSpikeState.MfaRequired, "Two-Factor Authentication required by Netflix. Enter verification PIN.");
                        return;
                    }

                    if (authStatus.IsVideo && authStatus.CurrentTime > 0.1)
                    {
                        AddLog("[Auth Check] Video already active!");
                        await OnPlaybackConfirmedActiveAsync(ct);
                        return;
                    }

                    if (authStatus.IsLogin)
                    {
                        AddLog("[Auth Check] Netflix login screen detected. Awaiting profile session or manual login.");
                    }
                }

                await Task.Delay(1000, ct);
            }

            // Transition to BufferingVideo to await video play confirmation
            await RunBufferingLoopAsync(ct);
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

            if (!IsSimulateMfaEnabled)
            {
                await _browserService.SubmitMfaPinAsync(MfaPin, ct);
            }
            else
            {
                AddLog("[MFA Simulation] Simulated PIN successfully accepted.");
            }

            SetState(NetflixSpikeState.BufferingVideo, "MFA submitted. Waiting for video stream confirmation (> 0.1s)...");
            await RunBufferingLoopAsync(ct);
        }

        private async Task RunBufferingLoopAsync(CancellationToken ct)
        {
            SetState(NetflixSpikeState.BufferingVideo, "Buffering video stream in hidden browser. Monitoring video.currentTime...");
            AddLog("Polling video playback state in hidden browser plane...");

            var bufferingStopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (bufferingStopwatch.Elapsed < TimeSpan.FromSeconds(90) && !ct.IsCancellationRequested)
            {
                var status = await _browserService.PollVideoPlaybackAsync(ct);
                if (status != null)
                {
                    CurrentVideoTime = status.CurrentTime;
                    VideoDuration = status.Duration;

                    if (status.Found)
                    {
                        StatusMessage = $"Buffering video... currentTime: {status.CurrentTime:F2}s / 0.1s threshold (ReadyState: {status.ReadyState})";

                        if (status.CurrentTime > 0.1)
                        {
                            AddLog($"[Playback] Confirmation criterion met: currentTime ({status.CurrentTime:F2}s) > 0.1s!");
                            await OnPlaybackConfirmedActiveAsync(ct);
                            return;
                        }
                    }
                    else
                    {
                        StatusMessage = $"Waiting for HTML5 video element on page... ({status.Title})";
                    }
                }

                await Task.Delay(400, ct);
            }

            if (!ct.IsCancellationRequested && State == NetflixSpikeState.BufferingVideo)
            {
                SetState(NetflixSpikeState.Stopped, "Buffering timed out before video.currentTime > 0.1s was confirmed.");
                AddLog("[Playback] Buffering timed out. Aborting.");
                await _browserService.StopAsync();
            }
        }

        private async Task OnPlaybackConfirmedActiveAsync(CancellationToken ct)
        {
            SetState(NetflixSpikeState.Playing, "Active playback confirmed! Injecting Baird 10-Foot HUD and moving window to (0,0)...");
            AddLog("[Reveal] Injecting baird-overlay.js...");

            await _browserService.InjectOverlayScriptAsync(null, ct);
            await _browserService.RevealWindowAsync(ct);

            SetState(NetflixSpikeState.Playing, "Active playback running with Baird 10-Foot HUD on screen.");
            AddLog("[Reveal] Window revealed to (0,0) fullscreen. Baird 10-Foot HUD active!");
        }

        public async Task StopPlaybackAsync()
        {
            AddLog("[Control] Abort / Stop requested.");
            _activeCts?.Cancel();
            _activeCts = null;

            await _browserService.StopAsync();

            SetState(NetflixSpikeState.Stopped, "Playback stopped. Browser window closed cleanly.");
            CurrentVideoTime = 0;
            MfaPin = "";
        }

        private void SetState(NetflixSpikeState newState, string message)
        {
            State = newState;
            StatusMessage = message;
        }
    }
}
