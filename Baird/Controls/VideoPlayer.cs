using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Baird.Mpv;
using Baird.ViewModels;
using System;
using System.Runtime.InteropServices;

namespace Baird.Controls
{
    public class VideoPlayer : OpenGlControlBase
    {
        private MpvPlayer? _player;
        // private IntPtr _mpvRenderContext; // Moved to MpvPlayer
        // private LibMpv.MpvRenderUpdateFn _renderUpdateDelegate; // Moved to MpvPlayer

        private Avalonia.Threading.DispatcherTimer _hudTimer;
        private Avalonia.Threading.DispatcherTimer _loadTimeoutTimer;
        private bool _isScanning;
        private double _liveDelaySeconds = 0;  // accumulated behind-live time for the current live source
        private string _lastLiveSource = "";   // detect channel changes so we can reset the delay

        private string? _pendingUrl;
        private double? _pendingStartSeconds;
        private int _loadRetryCount;
        private const int MaxLoadRetries = 3;
        private const int LoadTimeoutSeconds = 15;

        public Baird.Services.IDataService? DataService { get; set; }

        public VideoPlayer()
        {
            if (!Avalonia.Controls.Design.IsDesignMode)
            {
                _player = new MpvPlayer();
            }
            // _renderUpdateDelegate = UpdateCallback; // Moved to MpvPlayer internal logic

            // Subscribe to player's StreamEnded event
            // StreamEnded fires on the MpvEventLoop background thread, so marshal to UI thread
            // since we access Avalonia properties (Duration) and invoke UI-bound events
            if (_player != null)
            {
                _player.StreamLoadFailed += (sender, errorCode) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        Console.WriteLine($"[VideoPlayer] StreamLoadFailed received (error={errorCode})");
                        HandleLoadFailure($"mpv error code {errorCode}");
                    });
                };

                _player.StreamEnded += (sender, args) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Console.WriteLine("[VideoPlayer] StreamEnded event received from MpvPlayer");

                    if (_currentMediaItem != null)
                    {
                        // Save progress immediately when stream ends naturally (EOF)
                        // We pass Duration as override to SaveProgress to ensure it marks as finished
                        SaveProgress(Duration);
                    }

                    StreamEnded?.Invoke(this, EventArgs.Empty);
                });
            };

                _player.FileLoaded += (sender, e) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        Console.WriteLine("[VideoPlayer] FileLoaded event received");
                        _loadTimeoutTimer.Stop();
                        _loadRetryCount = 0;
                        _player?.LogAudioTracks();
                        IsLoading = false;
                        PlayerState = PlaybackState.Playing.ToString();
                    });
                };

                _player.PauseStateChanged += (sender, isPaused) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        SetCurrentValue(IsPausedProperty, isPaused);
                    });
                };

                _player.DurationChanged += (sender, dur) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        var tsDur = TimeSpan.FromSeconds(dur);
                        Duration = tsDur;
                        DurationSeconds = dur;
                    });
                };
            }

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
                if (_player?.State == PlaybackState.Loading)
                {
                    Console.WriteLine($"[VideoPlayer] Load timed out after {LoadTimeoutSeconds}s for {_pendingUrl}");
                    HandleLoadFailure("timeout");
                }
            };

            Focusable = true;
        }

        public event EventHandler<string>? SearchRequested;
        public event EventHandler? UserActivity;
        public event EventHandler? HistoryRequested;
        public event EventHandler? StreamEnded;
        public event EventHandler? ConfigurationToggleRequested;
        public event EventHandler? ExitRequested;

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

                case Avalonia.Input.Key.Q:
                    Console.WriteLine("[VideoPlayer] Q pressed. Saving progress and requesting exit.");
                    SaveProgress();
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;
            }
        }

        private void PerformScan(double seconds)
        {
            UserActivity?.Invoke(this, EventArgs.Empty);

            // If not already scanning and currently playing, pause first
            if (!_isScanning)
            {
                _isScanning = true;
            }

            // For live streams, track the seek offset so the behind-live bar updates.
            if (IsLive)
            {
                _liveDelaySeconds = Math.Max(0, _liveDelaySeconds - seconds);
            }

            // Perform the seek
            _player?.Command("seek", seconds.ToString(), "relative");
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
            if (_player == null) return;

            // State — PlayerState and IsLoading are DirectProperties; SetAndRaise skips
            // notification when the value is unchanged, so this is cheap at 1 Hz.
            var state = _player.State;
            PlayerState = state.ToString();
            IsLoading = state == PlaybackState.Loading;

            if (IsLive)
            {
                // Live Stream Mode: Show Clock
                FormattedTime = DateTime.Now.ToString("HH:mm");

                // Reset delay counter when the channel changes.
                var currentSource = _player?.CurrentPath ?? "";
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

                // Poll time-pos; Duration/DurationSeconds are maintained by DurationChanged event.
                var posStr = _player.TimePosition;
                double.TryParse(posStr, out double pos);
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
                    if (_player != null)
                    {
                        if (paused && (_player.State == PlaybackState.Playing || _player.State == PlaybackState.Buffering))
                        {
                            _player.Pause();
                        }
                        else if (!paused && _player.State == PlaybackState.Paused)
                        {
                            _player.Resume();
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

        public void Play(string url)
        {
            _pendingUrl = url;
            _pendingStartSeconds = null;
            _loadRetryCount = 0;
            _currentVideoStartTime = DateTime.Now;
            _player?.Play(url);
            IsLoading = true;
            IsPaused = false;
            RestartLoadTimeout();
        }

        public void Play(string url, TimeSpan startTime)
        {
            _pendingUrl = url;
            _pendingStartSeconds = startTime.TotalSeconds;
            _loadRetryCount = 0;
            _currentVideoStartTime = DateTime.Now;
            _player?.Play(url, startTime.TotalSeconds);
            IsLoading = true;
            IsPaused = false;
            RestartLoadTimeout();
        }

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
                Console.WriteLine($"[VideoPlayer] Retrying load (attempt {_loadRetryCount}/{MaxLoadRetries}) after {reason}: {_pendingUrl}");
                _currentVideoStartTime = DateTime.Now;
                if (_pendingStartSeconds.HasValue)
                    _player?.Play(_pendingUrl, _pendingStartSeconds.Value);
                else
                    _player?.Play(_pendingUrl);
                RestartLoadTimeout();
            }
            else
            {
                Console.WriteLine($"[VideoPlayer] Giving up on stream after {MaxLoadRetries} retries ({reason}): {_pendingUrl}");
            }
        }

        public void Pause()
        {
            _player?.Pause();
            IsPaused = true;
        }

        public void Resume()
        {
            _player?.Resume();
            IsPaused = false;
        }

        public void SetSubtitle(bool enabled) => _player?.SetSubtitle(enabled);

        public void Seek(double s) => _player?.Seek(s);
        public void Stop()
        {
            Console.WriteLine("[VideoPlayer] Stopping");
            _loadTimeoutTimer.Stop();
            _pendingUrl = null;
            SaveProgress();
            _player?.Stop();
            IsLoading = false;
            IsPaused = true;
        }

        public PlaybackState GetState() => _player?.State ?? PlaybackState.Idle;
        // public bool IsMpvPaused => _player.IsMpvPaused; // Use IsPaused property instead
        public string GetTimePos() => _player?.TimePosition ?? "0";
        public string GetDuration() => _player?.Duration ?? "0";
        public string GetCurrentPath() => _player?.CurrentPath ?? string.Empty;

        public void ToggleStats()
        {
            _player?.Command("script-binding", "stats/display-stats-toggle");
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
                if (_player != null)
                {
                    _player.InitializeOpenGl(ptr, () =>
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
            _player?.Dispose(); // This frees the render context and the mpv handle
            base.OnOpenGlDeinit(gl);
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (Avalonia.Controls.Design.IsDesignMode || _player == null) return;

            // If Loading, clear to black
            if (_player.State == PlaybackState.Loading)
            {
                gl.ClearColor(0f, 0f, 0f, 1f);
                gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
                return;
            }

            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            int w = (int)(Bounds.Width * scaling);
            int h = (int)(Bounds.Height * scaling);

            _player.Render(fb, w, h);
        }
    }
}
