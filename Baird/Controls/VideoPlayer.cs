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
        
        public Baird.Services.IHistoryService? HistoryService { get; set; }

        public VideoPlayer()
        {
            _player = new MpvPlayer();
            // _renderUpdateDelegate = UpdateCallback; // Moved to MpvPlayer internal logic

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

                case Avalonia.Input.Key.Up:
                    SearchRequested?.Invoke(this, string.Empty);
                    UserActivity?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;
                
                case Avalonia.Input.Key.Left:
                case Avalonia.Input.Key.MediaPreviousTrack:
                    PerformScan(-10);
                    // PerformScan invokes UserActivity
                    e.Handled = true;
                    break;

                case Avalonia.Input.Key.Right:
                case Avalonia.Input.Key.MediaNextTrack:
                    PerformScan(10);
                    // PerformScan invokes UserActivity
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
        private string? _currentMediaId;
        private Baird.Services.MediaItem? _currentMediaItem;
        
        public void SetCurrentMediaItem(Baird.Services.MediaItem item)
        {
            // Save progress of previous item before switching
            if (_currentMediaItem != null && item != null && _currentMediaItem.Id != item.Id)
            {
                SaveProgress();
            }
            _currentMediaItem = item;
            _currentMediaId = item.Id;
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

            // State
            var state = _player.State;
            var stateStr = state.ToString();
            
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
        
        public async void SaveProgress()
        {
            if (HistoryService == null || _currentMediaItem == null) 
            {
                Console.WriteLine($"[VideoPlayer] SaveProgress skipped. Service={HistoryService!=null}, Item={_currentMediaItem?.Name}");
                return;
            }
            
            // Get current pos/dur
            var posStr = _player.TimePosition;
            var durStr = _player.Duration;
            
            Console.WriteLine($"[VideoPlayer] SaveProgress Raw: Pos='{posStr}', Dur='{durStr}'");

            if (double.TryParse(posStr, out double pos) && double.TryParse(durStr, out double dur))
            {
                 Console.WriteLine($"[VideoPlayer] Saving {pos} / {dur} for {_currentMediaItem.Name}");
                 await HistoryService.UpsertAsync(_currentMediaItem, TimeSpan.FromSeconds(pos), TimeSpan.FromSeconds(dur));
            }
            else
            {
                 Console.WriteLine($"[VideoPlayer] Failed to parse pos/dur.");
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
                var enabled = (bool)change.NewValue;
                SetSubtitle(enabled);
            }

            if (change.Property == IsPausedProperty)
            {
                var paused = (bool)change.NewValue;
                if (paused && (_player.State == PlaybackState.Playing || _player.State == PlaybackState.Buffering))
                {
                    _player.Pause();
                }
                else if (!paused && _player.State == PlaybackState.Paused)
                {
                    _player.Resume();
                }
                
                // Save progress on Pause
                if (paused) SaveProgress();
            }
        }

        public void Play(string url) 
        {
            _player.Play(url);
            IsPaused = false;
        }

        public void Play(string url, TimeSpan startTime)
        {
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
            catch(Exception ex)
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
            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            int w = (int)(Bounds.Width * scaling);
            int h = (int)(Bounds.Height * scaling);
            
            _player.Render(fb, w, h);
        }
        
        // private void UpdateCallback(IntPtr ctx)
        // {
        //     // Request render
        //     Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering, Avalonia.Threading.DispatcherPriority.Render);
        // }
    }
}
