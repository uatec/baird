using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Baird.Mpv;
using Baird.Playback;
using Baird.Services;
using Baird.ViewModels;
using System;
using System.Runtime.InteropServices;

namespace Baird.Controls
{
    public class VideoPlayer : OpenGlControlBase, IAvDiagnosticsSource
    {
        private MpvPlayer? _mpv;                // mpv-only features: rendering, stats, quality settings
        private IPlaybackEngine? _mpvEngine;    // mpv (or a test fake) as a playback engine
        private IPlaybackEngine? _engine;       // the engine playing now
        private NetflixPlaybackEngine? _netflixEngine;

        private Avalonia.Threading.DispatcherTimer _hudTimer;
        private Avalonia.Threading.DispatcherTimer _loadTimeoutTimer;
        private double _liveDelaySeconds = 0;  // accumulated behind-live time for the current live source
        private string _lastLiveSource = "";   // detect channel changes so we can reset the delay

        private string? _pendingUrl;
        private double? _pendingStartSeconds;
        private int _loadRetryCount;
        private const int MaxLoadRetries = 3;
        private const int LoadTimeoutSeconds = 15;
        private const string DefaultLoadingText = "Loading...";

        public Baird.Services.IDataService? DataService { get; set; }

        public VideoPlayer() : this(Avalonia.Controls.Design.IsDesignMode ? null : new MpvPlayer(ReadMpvOverrides()))
        {
        }

        /// <summary>Plays non-Netflix sources through <paramref name="mpvEngine"/> (tests pass a fake instead of mpv).</summary>
        internal VideoPlayer(IPlaybackEngine? mpvEngine)
        {
            _mpv = mpvEngine as MpvPlayer;
            _mpvEngine = mpvEngine;
            _engine = mpvEngine;

            if (_mpv != null)
            {
                _mpv.StreamLoadFailed += (sender, errorCode) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (_engine != _mpvEngine) return;
                        Console.WriteLine($"[VideoPlayer] StreamLoadFailed received (error={errorCode})");
                        HandleLoadFailure($"mpv error code {errorCode}");
                    });
                };
            }
            if (mpvEngine != null) SubscribeEngine(mpvEngine);

            _hudTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _hudTimer.Tick += (s, e) => UpdateHud();
            _hudTimer.Start();

            _loadTimeoutTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(LoadTimeoutSeconds)
            };
            _loadTimeoutTimer.Tick += (s, e) =>
            {
                _loadTimeoutTimer.Stop();
                if (_engine == _mpvEngine && _mpvEngine?.State == PlaybackState.Loading)
                {
                    Console.WriteLine($"[VideoPlayer] Load timed out after {LoadTimeout.TotalSeconds:0}s for {Baird.Services.SecretRedactor.Redact(_pendingUrl)}");
                    HandleLoadFailure("timeout");
                }
            };

            Focusable = true;
        }

        /// <summary>How long mpv may take to open a stream before a retry (mpv only; Netflix has its own timeouts).</summary>
        internal TimeSpan LoadTimeout
        {
            get => _loadTimeoutTimer.Interval;
            set => _loadTimeoutTimer.Interval = value;
        }

        /// <summary>
        /// Plays Netflix watch URLs in the shared Netflix browser behind Baird. Set once by MainView; null when Netflix is
        /// not configured (Netflix items never reach the player then).
        /// </summary>
        public NetflixPlaybackEngine? NetflixEngine
        {
            get => _netflixEngine;
            set
            {
                if (_netflixEngine != null || value == null) return;
                _netflixEngine = value;
                SubscribeEngine(value);
                value.StatusChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_engine == value && !IsShowingFailure && value.State == PlaybackState.Loading) LoadingText = value.StatusText;
                });
                value.Failed += (_, reason) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_engine != value) return;
                    Console.WriteLine($"[VideoPlayer] Netflix could not play: {reason}");
                    IsShowingFailure = true;
                    LoadingText = $"{reason}\nPress Back to return.";
                    IsLoading = true;
                    IsShowingBrowser = false;
                });
            }
        }

        private bool IsNetflixActive => _engine != null && _engine == _netflixEngine;

        /// <summary>Engine events come from background threads; only the engine playing now may change the player.</summary>
        private void SubscribeEngine(IPlaybackEngine engine)
        {
            engine.StreamEnded += (sender, args) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_engine != engine) return;
                    Console.WriteLine("[VideoPlayer] StreamEnded event received from the engine");
                    if (engine == _netflixEngine) IsShowingBrowser = false;

                    if (_currentMediaItem != null)
                    {
                        // Save progress immediately when stream ends naturally (EOF)
                        // We pass Duration as override to SaveProgress to ensure it marks as finished
                        SaveProgress(Duration);
                    }

                    StreamEnded?.Invoke(this, EventArgs.Empty);
                });
            };

            engine.FileLoaded += (sender, e) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_engine != engine) return;
                    Console.WriteLine("[VideoPlayer] FileLoaded event received");
                    _loadTimeoutTimer.Stop();
                    _loadRetryCount = 0;
                    IsLoading = false;
                    PlayerState = PlaybackState.Playing.ToString();
                    if (engine == _netflixEngine) IsShowingBrowser = true;
                });
            };

            engine.PauseStateChanged += (sender, isPaused) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_engine != engine) return;
                    SetCurrentValue(IsPausedProperty, isPaused);
                });
            };

            engine.DurationChanged += (sender, dur) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_engine != engine) return;
                    var tsDur = TimeSpan.FromSeconds(dur);
                    Duration = tsDur;
                    DurationSeconds = dur;
                });
            };
        }

        private bool _isShowingBrowser;
        public static readonly DirectProperty<VideoPlayer, bool> IsShowingBrowserProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, bool>(nameof(IsShowingBrowser), o => o._isShowingBrowser);

        /// <summary>
        /// The video plays in the Netflix browser behind Baird: this control draws nothing (fully transparent), and the
        /// window must be transparent too (MainView does that).
        /// </summary>
        public bool IsShowingBrowser
        {
            get => _isShowingBrowser;
            private set
            {
                if (SetAndRaise(IsShowingBrowserProperty, ref _isShowingBrowser, value)) RequestNextFrameRendering();
            }
        }

        private bool _isShowingFailure;
        public static readonly DirectProperty<VideoPlayer, bool> IsShowingFailureProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, bool>(nameof(IsShowingFailure), o => o._isShowingFailure);

        /// <summary>The loading overlay shows why the video could not play (no spinner), until the next play or stop.</summary>
        public bool IsShowingFailure
        {
            get => _isShowingFailure;
            private set => SetAndRaise(IsShowingFailureProperty, ref _isShowingFailure, value);
        }

        private string _loadingText = DefaultLoadingText;
        public static readonly DirectProperty<VideoPlayer, string> LoadingTextProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, string>(nameof(LoadingText), o => o._loadingText);

        /// <summary>Text on the loading overlay: "Loading...", Netflix's step, or why Netflix could not play.</summary>
        public string LoadingText
        {
            get => _loadingText;
            private set => SetAndRaise(LoadingTextProperty, ref _loadingText, value);
        }

        /// <summary>
        /// mpv options from the [Mpv] section of config.ini (e.g. <c>video-sync=audio</c>), for
        /// tuning playback on the device without a rebuild.
        /// </summary>
        private static Dictionary<string, string> ReadMpvOverrides()
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var config = (Application.Current as App)?.Configuration;
            if (config == null) return options;
            foreach (var entry in config.GetSection("Mpv").GetChildren())
            {
                if (entry.Value != null) options[entry.Key] = entry.Value;
            }
            return options;
        }

        public event EventHandler<string>? SearchRequested;
        public event EventHandler? UserActivity;
        public event EventHandler? HistoryRequested;
        public event EventHandler? StreamEnded;
        public event EventHandler? ConfigurationToggleRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler? EpgRequested;
        /// <summary>M key while watching: flag "that felt off" on the A/V debug page/journal.</summary>
        public event EventHandler? DiagnosticsMarkRequested;

        protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;

            // Handle Down key for History
            if (e.Key == Avalonia.Input.Key.Down)
            {
                Console.WriteLine("[VideoPlayer] Down key pressed -> HistoryRequested");
                HistoryRequested?.Invoke(this, EventArgs.Empty);
                UserActivity?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            // Notify activity on any key press that is handled by us
            // But we'll do it inside the specific cases or generally if it's a valid key?
            // Let's do it generally for now if we handle it.

            // Numeric keys
            if (IsNumericKey(e.Key))
            {
                Console.WriteLine($"[VideoPlayer] Numeric key pressed: {e.Key}");
                var digit = GetNumericChar(e.Key);
                SearchRequested?.Invoke(this, digit);
                UserActivity?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Avalonia.Input.Key.Space:
                case Avalonia.Input.Key.MediaPlayPause:
                    IsPaused = !IsPaused;
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.Play:
                    IsPaused = false;
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.Pause:
                    IsPaused = true;
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.Tab:
                    ToggleStats();
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.Apps:
                    IsSubtitlesEnabled = !IsSubtitlesEnabled;
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.Left:
                    PerformScan(-10);
                    // PerformScan invokes UserActivity
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.Right:
                    PerformScan(10);
                    // PerformScan invokes UserActivity
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.S:
                    ConfigurationToggleRequested?.Invoke(this, EventArgs.Empty);
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.M:
                    DiagnosticsMarkRequested?.Invoke(this, EventArgs.Empty);
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.Q:
                    Console.WriteLine("[VideoPlayer] Q pressed. Saving progress and requesting exit.");
                    SaveProgress();
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.G:
                case (Avalonia.Input.Key)100026: // MediaTvGuide
                    Console.WriteLine("[VideoPlayer] EPG key pressed -> EpgRequested");
                    EpgRequested?.Invoke(this, EventArgs.Empty);
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;
            }
        }

        private void PerformScan(double seconds)
        {
            UserActivity?.Invoke(this, EventArgs.Empty);

            // For live streams, track the seek offset so the behind-live bar updates.
            if (IsLive)
            {
                _liveDelaySeconds = Math.Max(0, _liveDelaySeconds - seconds);
            }

            // Perform the seek
            _engine?.SeekRelative(seconds);
        }


        private bool IsNumericKey(Avalonia.Input.Key key)
        {
            return (key >= Avalonia.Input.Key.D0 && key <= Avalonia.Input.Key.D9) || (key >= Avalonia.Input.Key.NumPad0 && key <= Avalonia.Input.Key.NumPad9);
        }

        private string GetNumericChar(Avalonia.Input.Key key)
        {
            if (key >= Avalonia.Input.Key.D0 && key <= Avalonia.Input.Key.D9) return ((int)key - (int)Avalonia.Input.Key.D0).ToString();
            if (key >= Avalonia.Input.Key.NumPad0 && key <= Avalonia.Input.Key.NumPad9) return ((int)key - (int)Avalonia.Input.Key.NumPad0).ToString();
            return string.Empty;
        }

        public static readonly StyledProperty<bool> IsPausedProperty =
            AvaloniaProperty.Register<VideoPlayer, bool>(nameof(IsPaused));

        public bool IsPaused
        {
            get => GetValue(IsPausedProperty);
            set => SetValue(IsPausedProperty, value);
        }

        public static readonly StyledProperty<string?> SourceProperty =
            AvaloniaProperty.Register<VideoPlayer, string?>(nameof(Source));

        public string? Source
        {
            get => GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        private string _formattedTime = "00:00:00 / 00:00:00";
        public static readonly DirectProperty<VideoPlayer, string> FormattedTimeProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, string>(nameof(FormattedTime),
                o => o._formattedTime, (o, v) => o._formattedTime = v);

        public string FormattedTime
        {
            get => _formattedTime;
            set => SetAndRaise(FormattedTimeProperty, ref _formattedTime, value);
        }

        private string _playerState = "Idle";
        public static readonly DirectProperty<VideoPlayer, string> PlayerStateProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, string>(nameof(PlayerState),
                o => o._playerState, (o, v) => o._playerState = v);

        public string PlayerState
        {
            get => _playerState;
            set => SetAndRaise(PlayerStateProperty, ref _playerState, value);
        }

        // Helper to track current media item ID for history
        private MediaItemViewModel? _currentMediaItem;

        public void SetCurrentMediaItem(MediaItemViewModel item)
        {
            ArgumentNullException.ThrowIfNull(item);

            // Save progress of previous item before switching
            if (_currentMediaItem != null && _currentMediaItem.Id != item.Id)
            {
                Console.WriteLine($"[VideoPlayer] Switching to new item. Saving progress for {_currentMediaItem.Name}");
                SaveProgress();
            }
            _currentMediaItem = item;
        }

        public static readonly StyledProperty<bool> IsLiveProperty =
            AvaloniaProperty.Register<VideoPlayer, bool>(nameof(IsLive));

        public bool IsLive
        {
            get => GetValue(IsLiveProperty);
            set => SetValue(IsLiveProperty, value);
        }

        private TimeSpan _position;
        public static readonly DirectProperty<VideoPlayer, TimeSpan> PositionProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, TimeSpan>(nameof(Position),
                o => o._position, (o, v) => o._position = v);

        public TimeSpan Position
        {
            get => _position;
            set => SetAndRaise(PositionProperty, ref _position, value);
        }

        private TimeSpan _duration;
        public static readonly DirectProperty<VideoPlayer, TimeSpan> DurationProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, TimeSpan>(nameof(Duration),
                o => o._duration, (o, v) => o._duration = v);

        public TimeSpan Duration
        {
            get => _duration;
            set => SetAndRaise(DurationProperty, ref _duration, value);
        }

        private double _positionSeconds;
        public static readonly DirectProperty<VideoPlayer, double> PositionSecondsProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, double>(nameof(PositionSeconds),
                o => o._positionSeconds, (o, v) => o._positionSeconds = v);

        public double PositionSeconds
        {
            get => _positionSeconds;
            set => SetAndRaise(PositionSecondsProperty, ref _positionSeconds, value);
        }

        private double _durationSeconds;
        public static readonly DirectProperty<VideoPlayer, double> DurationSecondsProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, double>(nameof(DurationSeconds),
                o => o._durationSeconds, (o, v) => o._durationSeconds = v);

        public double DurationSeconds
        {
            get => _durationSeconds;
            set => SetAndRaise(DurationSecondsProperty, ref _durationSeconds, value);
        }

        private string _finishingAt = "";
        public static readonly DirectProperty<VideoPlayer, string> FinishingAtProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, string>(nameof(FinishingAt),
                o => o._finishingAt, (o, v) => o._finishingAt = v);

        public string FinishingAt
        {
            get => _finishingAt;
            set => SetAndRaise(FinishingAtProperty, ref _finishingAt, value);
        }

        private string _timeRemaining = "";
        public static readonly DirectProperty<VideoPlayer, string> TimeRemainingProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, string>(nameof(TimeRemaining),
                o => o._timeRemaining, (o, v) => o._timeRemaining = v);

        public string TimeRemaining
        {
            get => _timeRemaining;
            set => SetAndRaise(TimeRemainingProperty, ref _timeRemaining, value);
        }

        private bool _isLiveBehind;
        public static readonly DirectProperty<VideoPlayer, bool> IsLiveBehindProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, bool>(nameof(IsLiveBehind),
                o => o._isLiveBehind, (o, v) => o._isLiveBehind = v);

        /// <summary>True when playing a live stream but behind the live edge (paused or rewound).</summary>
        public bool IsLiveBehind
        {
            get => _isLiveBehind;
            set => SetAndRaise(IsLiveBehindProperty, ref _isLiveBehind, value);
        }

        private double _liveBehindSeconds;
        public static readonly DirectProperty<VideoPlayer, double> LiveBehindSecondsProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, double>(nameof(LiveBehindSeconds),
                o => o._liveBehindSeconds, (o, v) => o._liveBehindSeconds = v);

        /// <summary>How many seconds behind the live edge the current position is.</summary>
        public double LiveBehindSeconds
        {
            get => _liveBehindSeconds;
            set => SetAndRaise(LiveBehindSecondsProperty, ref _liveBehindSeconds, value);
        }

        private double _liveProgressSeconds = 3600;
        public static readonly DirectProperty<VideoPlayer, double> LiveProgressSecondsProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, double>(nameof(LiveProgressSeconds),
                o => o._liveProgressSeconds, (o, v) => o._liveProgressSeconds = v);

        /// <summary>
        /// Position within a 1-hour (3600 s) window for the live progress bar.
        /// 3600 = at live edge; 0 = 60 minutes behind.
        /// </summary>
        public double LiveProgressSeconds
        {
            get => _liveProgressSeconds;
            set => SetAndRaise(LiveProgressSecondsProperty, ref _liveProgressSeconds, value);
        }

        private string _liveBehindFormatted = "";
        public static readonly DirectProperty<VideoPlayer, string> LiveBehindFormattedProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, string>(nameof(LiveBehindFormatted),
                o => o._liveBehindFormatted, (o, v) => o._liveBehindFormatted = v);

        /// <summary>Human-readable behind-live offset, e.g. "-00:15:30".</summary>
        public string LiveBehindFormatted
        {
            get => _liveBehindFormatted;
            set => SetAndRaise(LiveBehindFormattedProperty, ref _liveBehindFormatted, value);
        }

        private double _liveWindowSeconds = 300;
        public static readonly DirectProperty<VideoPlayer, double> LiveWindowSecondsProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, double>(nameof(LiveWindowSeconds),
                o => o._liveWindowSeconds, (o, v) => o._liveWindowSeconds = v);

        /// <summary>
        /// The current progress-bar window size in seconds. Steps up through
        /// 5 / 10 / 15 / 30 / 60 minute intervals to accommodate the delay.
        /// </summary>
        public double LiveWindowSeconds
        {
            get => _liveWindowSeconds;
            set => SetAndRaise(LiveWindowSecondsProperty, ref _liveWindowSeconds, value);
        }

        public static readonly StyledProperty<bool> IsSubtitlesEnabledProperty =
            AvaloniaProperty.Register<VideoPlayer, bool>(nameof(IsSubtitlesEnabled));

        public bool IsSubtitlesEnabled
        {
            get => GetValue(IsSubtitlesEnabledProperty);
            set => SetValue(IsSubtitlesEnabledProperty, value);
        }

        private bool _isLoading;
        public static readonly DirectProperty<VideoPlayer, bool> IsLoadingProperty =
            AvaloniaProperty.RegisterDirect<VideoPlayer, bool>(nameof(IsLoading),
                o => o._isLoading, (o, v) => o._isLoading = v);

        public bool IsLoading
        {
            get => _isLoading;
            set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
        }

        public static readonly StyledProperty<TimeSpan?> ResumeTimeProperty =
            AvaloniaProperty.Register<VideoPlayer, TimeSpan?>(nameof(ResumeTime));

        public TimeSpan? ResumeTime
        {
            get => GetValue(ResumeTimeProperty);
            set => SetValue(ResumeTimeProperty, value);
        }

        private void UpdateHud()
        {
            var engine = _engine;
            if (engine == null) return;

            // State — PlayerState and IsLoading are DirectProperties; SetAndRaise skips
            // notification when the value is unchanged, so this is cheap at 1 Hz.
            var state = engine.State;
            PlayerState = state.ToString();
            IsLoading = IsShowingFailure || state == PlaybackState.Loading;

            if (IsLive)
            {
                // Live Stream Mode: Show Clock
                FormattedTime = DateTime.Now.ToString("HH:mm");

                // Reset delay counter when the channel changes.
                var currentSource = _mpv?.CurrentPath ?? "";
                if (currentSource != _lastLiveSource)
                {
                    _liveDelaySeconds = 0;
                    _lastLiveSource = currentSource;
                }

                // Accumulate delay only while paused. Timer fires every 1 s so each tick = 1 s.
                if (state == PlaybackState.Paused)
                    _liveDelaySeconds += 1.0;

                var behindSeconds = _liveDelaySeconds;
                LiveBehindSeconds = behindSeconds;
                IsLiveBehind = behindSeconds > 0;

                // Pick the smallest window that comfortably contains the delay.
                double[] intervals = { 300, 600, 900, 1800, 3600 };
                double windowSeconds = intervals[intervals.Length - 1];
                foreach (var interval in intervals)
                {
                    if (behindSeconds <= interval) { windowSeconds = interval; break; }
                }
                LiveWindowSeconds = windowSeconds;
                LiveProgressSeconds = Math.Max(0, windowSeconds - behindSeconds);

                var behindSpan = TimeSpan.FromSeconds(behindSeconds);
                LiveBehindFormatted = behindSpan.TotalHours >= 1
                    ? $"-{behindSpan:hh\\:mm\\:ss}"
                    : $"-{behindSpan:mm\\:ss}";
            }
            else
            {
                // VOD Mode: reset live state
                IsLiveBehind = false;
                _liveDelaySeconds = 0;
                _lastLiveSource = "";

                // time-pos is cached from mpv's property observer (no blocking call on the UI
                // thread); Duration/DurationSeconds are maintained by the DurationChanged event.
                var pos = engine.TimePositionSeconds;
                var tsPos = TimeSpan.FromSeconds(pos);

                // Keep MediaItem history in sync with current position
                if (_currentMediaItem != null && Duration.TotalSeconds > 0)
                {
                    if (_currentMediaItem.History == null)
                    {
                        _currentMediaItem.History = new Baird.Models.HistoryItem
                        {
                            Id = _currentMediaItem.Id,
                            LastPosition = tsPos,
                            Duration = Duration,
                            IsFinished = false,
                            LastWatched = DateTime.Now
                        };
                    }
                    else
                    {
                        _currentMediaItem.History.LastPosition = tsPos;
                        _currentMediaItem.History.Duration = Duration;
                    }
                }

                Position = tsPos;
                PositionSeconds = pos;
                FormattedTime = $"{tsPos:hh\\:mm\\:ss}";

                if (Duration.TotalSeconds > 0)
                {
                    var timeLeft = Duration - tsPos;
                    FinishingAt = $"Finishing at: {DateTime.Now.Add(timeLeft):HH:mm}";
                    TimeRemaining = $"-{timeLeft:hh\\:mm\\:ss}";
                }
                else
                {
                    FinishingAt = "";
                    TimeRemaining = "";
                }
            }
        }

        // Track when the current video started playing
        private DateTime _currentVideoStartTime;

        public async void SaveProgress(TimeSpan? positionOverride = null)
        {
            try
            {
                if (DataService == null || _currentMediaItem == null)
                {
                    Console.WriteLine($"[VideoPlayer] SaveProgress skipped. Service={DataService != null}, Item={_currentMediaItem?.Name}");
                    return;
                }

                // Check if we should save history
                if (IsLive)
                {
                    // For live streams, check if we've been watching for at least 10 seconds this session
                    var timeWatched = DateTime.Now - _currentVideoStartTime;
                    if (timeWatched.TotalSeconds < 10)
                    {
                        Console.WriteLine($"[VideoPlayer] Skipping history save for live stream. Watched only {timeWatched.TotalSeconds:F1}s (needs 10s)");
                        return;
                    }
                }
                else
                {
                    // For VOD, check if we are at least 10 seconds into the video
                    // OR if we have watched for at least 10 seconds (to catch people who skip ahead immediately?)
                    // User said: "only add something to history if we have progressed more than 10 seconds in to the video."
                    // This implies position > 10s.
                    // However, "this prevents channel hopping from being added to history" usually implies time spent watching.
                    // But the user clarified: "for live streams, that's more than 10 seconds playback time elapsed this session."
                    // implying for VOD it denotes position.

                    var currentPos = positionOverride ?? Position;
                    if (currentPos.TotalSeconds < 10)
                    {
                        Console.WriteLine($"[VideoPlayer] Skipping history save for VOD. Position {currentPos.TotalSeconds:F1}s < 10s");
                        return;
                    }
                }

                var position = positionOverride ?? Position;
                var duration = Duration;

                Console.WriteLine($"[VideoPlayer] Saving {position} / {duration} for {_currentMediaItem.Name}");
                await DataService.UpsertHistoryAsync(_currentMediaItem, position, duration);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VideoPlayer] SaveProgress failed: {ex.Message}");
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SourceProperty)
            {
                var url = change.NewValue as string;
                if (!string.IsNullOrEmpty(url))
                {
                    // Defer play to allow other bindings (like ResumeTime) to update if they are changing simultaneously
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        // Double check source hasn't changed again
                        if (Source != url) return;

                        if (ResumeTime.HasValue)
                        {
                            var start = ResumeTime.Value;
                            Console.WriteLine($"[VideoPlayer] Playing with ResumeTime: {start}");
                            Play(url, start);
                        }
                        else
                        {
                            Play(url);
                        }
                    });

                    // Apply subtitle state when source changes/starts
                    SetSubtitle(IsSubtitlesEnabled);
                }
                else
                {
                    Stop();
                }
            }

            if (change.Property == IsSubtitlesEnabledProperty)
            {
                if (change.NewValue is bool enabled)
                {
                    SetSubtitle(enabled);
                }
            }

            if (change.Property == IsPausedProperty)
            {
                if (change.NewValue is bool paused)
                {
                    var engine = _engine;
                    if (engine != null)
                    {
                        // Pausing while Loading is allowed: mpv honours it once the file opens.
                        if (paused && (engine.State == PlaybackState.Playing || engine.State == PlaybackState.Buffering || engine.State == PlaybackState.Loading))
                        {
                            engine.Pause();
                        }
                        else if (!paused && engine.State == PlaybackState.Paused)
                        {
                            engine.Resume();
                        }
                    }

                    // Save progress on Pause
                    if (paused)
                    {
                        Console.WriteLine("[VideoPlayer] Saving progress on Pause");
                        SaveProgress();
                    }
                }
            }
        }

        public void Play(string url) => StartPlayback(url, null);

        public void Play(string url, TimeSpan startTime) => StartPlayback(url, startTime.TotalSeconds);

        /// <summary>Picks the engine for <paramref name="url"/> (Netflix watch pages: the Netflix browser; the rest: mpv).</summary>
        private void StartPlayback(string url, double? startSeconds)
        {
            if (_netflixEngine == null && NetflixPlaybackEngine.Handles(url))
            {
                // A Netflix item (history, Continue Watching) on a Baird without Netflix: mpv can't play it.
                Console.WriteLine("[VideoPlayer] Netflix item, but Netflix is not set up.");
                if (_engine != null && _engine != _mpvEngine) _engine.Stop();
                _mpvEngine?.Stop();
                _engine = _mpvEngine;
                _loadTimeoutTimer.Stop();
                _pendingUrl = null;
                IsShowingBrowser = false;
                IsShowingFailure = true;
                LoadingText = $"Netflix is not set up on this Baird: add {NetflixCredentials.UserKey} and {NetflixCredentials.PasswordKey} to ~/.baird/config.ini.\nPress Back to return.";
                IsLoading = true;
                return;
            }

            var next = _netflixEngine != null && NetflixPlaybackEngine.Handles(url) ? _netflixEngine : _mpvEngine;
            if (_engine != null && _engine != next)
            {
                Console.WriteLine($"[VideoPlayer] Switching engine to {(next == _netflixEngine ? "Netflix" : "mpv")}.");
                _engine.Stop();
            }
            _engine = next;
            IsShowingBrowser = false;
            IsShowingFailure = false;
            LoadingText = next != null && next == _netflixEngine ? "Starting Netflix…" : DefaultLoadingText;

            _pendingUrl = url;
            _pendingStartSeconds = startSeconds;
            _loadRetryCount = 0;
            _currentVideoStartTime = DateTime.Now;
            if (next == _mpvEngine)
            {
                _mpv?.ConfigureForSource(IsLive);
                next?.Play(url, startSeconds);
                RestartLoadTimeout();
            }
            else
            {
                _loadTimeoutTimer.Stop(); // the Netflix flow has its own timeouts
                next?.Play(url, startSeconds);
            }
            IsLoading = true;
            IsPaused = false;
        }

        // Video-quality feature flags, forwarded to the underlying mpv player. Driven live from the
        // Settings page (see MainView wiring) so each can be A/B tested on the device.
        public void SetHighQualityScaling(bool enabled) => _mpv?.SetHighQualityScaling(enabled);
        public void SetSharperDeinterlacing(bool enabled) => _mpv?.SetSharperDeinterlacing(enabled);
        public void SetRenderLogging(bool enabled) => _mpv?.SetRenderLogging(enabled);

        // A/V debug page
        public Task<AvStats?> SampleAvStatsAsync() => _mpv?.SampleAvStatsAsync() ?? Task.FromResult<AvStats?>(null);
        public void SetAudioDelay(double seconds) => _mpv?.SetAudioDelay(seconds);

        private void RestartLoadTimeout()
        {
            _loadTimeoutTimer.Stop();
            _loadTimeoutTimer.Start();
        }

        private void HandleLoadFailure(string reason)
        {
            _loadTimeoutTimer.Stop();
            _loadRetryCount++;
            if (_loadRetryCount <= MaxLoadRetries && !string.IsNullOrEmpty(_pendingUrl))
            {
                Console.WriteLine($"[VideoPlayer] Retrying load (attempt {_loadRetryCount}/{MaxLoadRetries}) after {reason}: {Baird.Services.SecretRedactor.Redact(_pendingUrl)}");
                _currentVideoStartTime = DateTime.Now;
                if (_pendingStartSeconds.HasValue)
                    _mpvEngine?.Play(_pendingUrl, _pendingStartSeconds.Value);
                else
                    _mpvEngine?.Play(_pendingUrl);
                RestartLoadTimeout();
            }
            else
            {
                Console.WriteLine($"[VideoPlayer] Giving up on stream after {MaxLoadRetries} retries ({reason}): {Baird.Services.SecretRedactor.Redact(_pendingUrl)}");
            }
        }

        public void Pause()
        {
            _engine?.Pause();
            IsPaused = true;
        }

        public void Resume()
        {
            _engine?.Resume();
            IsPaused = false;
        }

        public void SetSubtitle(bool enabled) => _engine?.SetSubtitle(enabled);

        public void Seek(double s) => _engine?.Seek(s);
        public void Stop()
        {
            Console.WriteLine("[VideoPlayer] Stopping");
            _loadTimeoutTimer.Stop();
            _pendingUrl = null;
            SaveProgress();
            _engine?.Stop();
            IsShowingBrowser = false;
            IsShowingFailure = false;
            IsLoading = false;
            IsPaused = true;
        }

        public PlaybackState GetState() => _engine?.State ?? PlaybackState.Idle;

        public void ToggleStats()
        {
            _mpv?.Command("script-binding", "stats/display-stats-toggle");
        }

        private LibMpv.MpvGetProcAddressFn? _getProcAddress;

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            Console.WriteLine("[VideoPlayer] OnDetachedFromVisualTree called. Saving progress.");
            SaveProgress();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnOpenGlInit(GlInterface gl)
        {
            if (Avalonia.Controls.Design.IsDesignMode) return;

            Console.WriteLine("[VideoPlayer] OnOpenGlInit called. Initializing MPV render context in MpvPlayer...");
            base.OnOpenGlInit(gl);

            // Keep delegate alive
            _getProcAddress = (ctx, name) => gl.GetProcAddress(name);

            // Get function pointer for the delegate
            IntPtr ptr = Marshal.GetFunctionPointerForDelegate(_getProcAddress);

            try
            {
                if (_mpv != null)
                {
                    _mpv.InitializeOpenGl(ptr, () =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering, Avalonia.Threading.DispatcherPriority.Render);
                    });
                    Console.WriteLine("[VideoPlayer] Render context initialized successfully.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VideoPlayer] Failed to initialize MpvPlayer OpenGL: {ex}");
            }
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            if (Avalonia.Controls.Design.IsDesignMode) return;

            Console.WriteLine("[VideoPlayer] OnOpenGlDeinit called. Freeing context.");
            // Stop timers first so nothing calls into the player after its handle is gone.
            _hudTimer.Stop();
            _loadTimeoutTimer.Stop();
            _mpv?.Dispose(); // This frees the render context and the mpv handle
            if (_engine == _mpvEngine && _mpvEngine == _mpv) _engine = null;
            if (_mpvEngine == _mpv) _mpvEngine = null;
            _mpv = null;
            base.OnOpenGlDeinit(gl);
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (Avalonia.Controls.Design.IsDesignMode) return;

            // Netflix: the video is in the browser behind Baird's window, so draw nothing at all.
            if (IsShowingBrowser)
            {
                gl.ClearColor(0f, 0f, 0f, 0f);
                gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
                return;
            }
            if (_mpv == null) return;

            // If Loading, clear to black
            if (_mpv.State == PlaybackState.Loading)
            {
                gl.ClearColor(0f, 0f, 0f, 1f);
                gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
                return;
            }

            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            int w = (int)(Bounds.Width * scaling);
            int h = (int)(Bounds.Height * scaling);

            _mpv.Render(fb, w, h);
        }
    }
}
