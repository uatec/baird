using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Baird.Mpv;
using System;
using System.Runtime.InteropServices;

namespace Baird.Controls
{
    public class VideoPlayer : OpenGlControlBase
    {
        private MpvPlayer _player;
        // private IntPtr _mpvRenderContext; // Moved to MpvPlayer
        // private LibMpv.MpvRenderUpdateFn _renderUpdateDelegate; // Moved to MpvPlayer

        private Avalonia.Threading.DispatcherTimer _hudTimer;
        private bool _isScanning;
        private string _lastLoggedState = "Idle";

        public Baird.Services.IDataService? DataService { get; set; }

        public VideoPlayer()
        {
            _player = new MpvPlayer();
            // _renderUpdateDelegate = UpdateCallback; // Moved to MpvPlayer internal logic

            // Subscribe to player's StreamEnded event
            _player.StreamEnded += (sender, args) =>
            {
                Console.WriteLine("[VideoPlayer] StreamEnded event received from MpvPlayer");
                // Save progress immediately when stream ends naturally (EOF)
                // This ensures the final position/duration is captured before auto-play starts next episode

                if (_currentMediaItem == null)
                {
                    Console.WriteLine("[VideoPlayer] _currentMediaItem is null. Skipping SaveProgress.");
                    return;
                }

                // ensure we appear to have watched the entire video
                // We pass Duration as override to SaveProgress to ensure it marks as finished
                SaveProgress(this.Duration);
                StreamEnded?.Invoke(this, EventArgs.Empty);
            };

            _hudTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _hudTimer.Tick += (s, e) => UpdateHud();
            _hudTimer.Start();

            Focusable = true;
        }

        public event EventHandler<string>? SearchRequested;
        public event EventHandler? UserActivity;
        public event EventHandler? HistoryRequested;
        public event EventHandler? StreamEnded;
        public event EventHandler? ConfigurationToggleRequested;

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
                Console.WriteLine("Numeric key pressed: " + e.Key);
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

                case Avalonia.Input.Key.Tab:
                    ToggleStats();
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.CapsLock:
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

            // Perform the seek
            _player.Command("seek", seconds.ToString(), "relative");
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

        public static readonly StyledProperty<string> FormattedTimeProperty =
            AvaloniaProperty.Register<VideoPlayer, string>(nameof(FormattedTime), defaultValue: "00:00:00 / 00:00:00");

        public string FormattedTime
        {
            get => GetValue(FormattedTimeProperty);
            set => SetValue(FormattedTimeProperty, value);
        }

        public static readonly StyledProperty<string> PlayerStateProperty =
            AvaloniaProperty.Register<VideoPlayer, string>(nameof(PlayerState), defaultValue: "Idle");

        public string PlayerState
        {
            get => GetValue(PlayerStateProperty);
            set => SetValue(PlayerStateProperty, value);
        }

        // Helper to track current media item ID for history
        private Services.MediaItem? _currentMediaItem;

        public void SetCurrentMediaItem(Services.MediaItem item)
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

        public static readonly StyledProperty<TimeSpan> PositionProperty =
            AvaloniaProperty.Register<VideoPlayer, TimeSpan>(nameof(Position));

        public TimeSpan Position
        {
            get => GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        public static readonly StyledProperty<TimeSpan> DurationProperty =
            AvaloniaProperty.Register<VideoPlayer, TimeSpan>(nameof(Duration));

        public TimeSpan Duration
        {
            get => GetValue(DurationProperty);
            set => SetValue(DurationProperty, value);
        }

        public static readonly StyledProperty<double> PositionSecondsProperty =
            AvaloniaProperty.Register<VideoPlayer, double>(nameof(PositionSeconds));

        public double PositionSeconds
        {
            get => GetValue(PositionSecondsProperty);
            set => SetValue(PositionSecondsProperty, value);
        }

        public static readonly StyledProperty<double> DurationSecondsProperty =
            AvaloniaProperty.Register<VideoPlayer, double>(nameof(DurationSeconds));

        public double DurationSeconds
        {
            get => GetValue(DurationSecondsProperty);
            set => SetValue(DurationSecondsProperty, value);
        }

        public static readonly StyledProperty<string> FinishingAtProperty =
            AvaloniaProperty.Register<VideoPlayer, string>(nameof(FinishingAt), defaultValue: "");

        public string FinishingAt
        {
            get => GetValue(FinishingAtProperty);
            set => SetValue(FinishingAtProperty, value);
        }

        public static readonly StyledProperty<string> TimeRemainingProperty =
            AvaloniaProperty.Register<VideoPlayer, string>(nameof(TimeRemaining), defaultValue: "");

        public string TimeRemaining
        {
            get => GetValue(TimeRemainingProperty);
            set => SetValue(TimeRemainingProperty, value);
        }

        public static readonly StyledProperty<bool> IsSubtitlesEnabledProperty =
            AvaloniaProperty.Register<VideoPlayer, bool>(nameof(IsSubtitlesEnabled));

        public bool IsSubtitlesEnabled
        {
            get => GetValue(IsSubtitlesEnabledProperty);
            set => SetValue(IsSubtitlesEnabledProperty, value);
        }

        public static readonly StyledProperty<bool> IsLoadingProperty =
            AvaloniaProperty.Register<VideoPlayer, bool>(nameof(IsLoading));

        public bool IsLoading
        {
            get => GetValue(IsLoadingProperty);
            set => SetValue(IsLoadingProperty, value);
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

            // Check for state transitions (Loading -> Playing)
            _player.UpdateVideoStatus();

            // State
            var state = _player.State;
            var stateStr = state.ToString();

            // Update IsLoading property
            IsLoading = state == PlaybackState.Loading;

            // Sync IsPaused with actual player state (if changed externally or by internal logic)
            // Use SetCurrentValue to avoid overwriting binding if not necessary, or just SetValue
            bool shouldBePaused = state == PlaybackState.Paused || state == PlaybackState.Idle;
            if (IsPaused != shouldBePaused)
            {
                // We only update if it mismatches to avoid fighting with the binding?
                // But wait, if we are playing and user presses pause on headset?
                // We want ViewModel to know.
                SetCurrentValue(IsPausedProperty, shouldBePaused);
            }

            if (stateStr == "Playing" && _lastLoggedState != "Playing")
            {
                _player.LogAudioTracks();
            }
            _lastLoggedState = stateStr;

            if (IsLive)
            {
                // Live Stream Mode: Show Clock
                FormattedTime = DateTime.Now.ToString("HH:mm");
            }
            else
            {
                // VOD Mode: Show Position / Duration
                var posStr = _player.TimePosition;
                var durStr = _player.Duration;

                double.TryParse(posStr, out double pos);
                double.TryParse(durStr, out double dur);

                var tsPos = TimeSpan.FromSeconds(pos);
                var tsDur = TimeSpan.FromSeconds(dur);

                // Sync to MediaItem if we have one
                if (_currentMediaItem != null && dur > 0)
                {
                    if (_currentMediaItem.History == null)
                    {
                        _currentMediaItem.History = new Baird.Models.HistoryItem
                        {
                            Id = _currentMediaItem.Id,
                            LastPosition = tsPos,
                            Duration = tsDur,
                            IsFinished = false,
                            LastWatched = DateTime.Now
                        };
                    }
                    else
                    {
                        _currentMediaItem.History.LastPosition = tsPos;
                        _currentMediaItem.History.Duration = tsDur;
                    }
                }

                Position = tsPos;
                Duration = tsDur;
                PositionSeconds = pos;
                DurationSeconds = dur;

                FormattedTime = $"{tsPos:hh\\:mm\\:ss}"; // Just current time for row 1

                // Calculate finishing time
                if (dur > 0)
                {
                    var timeLeft = tsDur - tsPos;
                    var finishTime = DateTime.Now.Add(timeLeft);
                    FinishingAt = $"Finishing at: {finishTime:HH:mm}";
                    TimeRemaining = $"-{timeLeft:hh\\:mm\\:ss}";
                }
                else
                {
                    FinishingAt = "";
                    TimeRemaining = "";
                }
            }

            PlayerState = stateStr;

            // Update History periodically? Or just rely on Stop/Pause?
            // User requirement: "whenever we stop or change video stream... record the progress"
            // We should arguably do it on Pause too.
            // And maybe periodically in case of crash?
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
                    if (paused && (_player.State == PlaybackState.Playing || _player.State == PlaybackState.Buffering))
                    {
                        _player.Pause();
                    }
                    else if (!paused && _player.State == PlaybackState.Paused)
                    {
                        _player.Resume();
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
            _currentVideoStartTime = DateTime.Now;
            _player.Play(url);
            IsPaused = false;
        }

        public void Play(string url, TimeSpan startTime)
        {
            _currentVideoStartTime = DateTime.Now;
            _player.Play(url, startTime.TotalSeconds);
            IsPaused = false;
        }

        public void Pause()
        {
            _player.Pause();
            IsPaused = true;
        }

        public void Resume()
        {
            _player.Resume();
            IsPaused = false;
        }

        public void SetSubtitle(bool enabled) => _player.SetSubtitle(enabled);

        public void Seek(double s) => _player.Seek(s);
        public void Stop()
        {
            Console.WriteLine("[VideoPlayer] Stopping");
            SaveProgress();
            _player.Stop();
            IsPaused = true;
        }

        public PlaybackState GetState() => _player.State;
        // public bool IsMpvPaused => _player.IsMpvPaused; // Use IsPaused property instead
        public string GetTimePos() => _player.TimePosition;
        public string GetDuration() => _player.Duration;
        public string GetCurrentPath() => _player.CurrentPath;

        public void ToggleStats()
        {
            _player.Command("script-binding", "stats/display-stats-toggle");
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
            Console.WriteLine("[VideoPlayer] OnOpenGlInit called. Initializing MPV render context in MpvPlayer...");
            base.OnOpenGlInit(gl);

            // Keep delegate alive
            _getProcAddress = (ctx, name) => gl.GetProcAddress(name);

            // Get function pointer for the delegate
            IntPtr ptr = Marshal.GetFunctionPointerForDelegate(_getProcAddress);

            try
            {
                _player.InitializeOpenGl(ptr, () =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering, Avalonia.Threading.DispatcherPriority.Render);
                });
                Console.WriteLine("[VideoPlayer] Render context initialized successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VideoPlayer] Failed to initialize MpvPlayer OpenGL: {ex}");
            }
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            Console.WriteLine("[VideoPlayer] OnOpenGlDeinit called. Freeing context.");
            _player.Dispose(); // This frees the render context and the mpv handle
            base.OnOpenGlDeinit(gl);
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
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
