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
        private string _lastLoggedState = "Idle";

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

        private void UpdateHud()
        {
            if (_player == null) return;

            // State
            var state = _player.State.ToString();
            
            if (state == "Playing" && _lastLoggedState != "Playing")
            {
                _player.LogAudioTracks();
            }
            _lastLoggedState = state;
            
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
            
            PlayerState = state;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SourceProperty)
            {
                var url = change.NewValue as string;
                if (!string.IsNullOrEmpty(url))
                {
                    Play(url);
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
        }

        public void Play(string url) 
        {
            _player.Play(url);
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
