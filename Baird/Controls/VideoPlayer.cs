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
        private IntPtr _mpvRenderContext;
        private LibMpv.MpvRenderUpdateFn _renderUpdateDelegate;
        private Avalonia.Threading.DispatcherTimer _hudTimer;

        public VideoPlayer()
        {
            _player = new MpvPlayer();
            _renderUpdateDelegate = UpdateCallback;

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

        private void UpdateHud()
        {
            if (_player == null) return;

            // State
            var state = _player.State.ToString();
            
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

                FormattedTime = $"{tsPos:hh\\:mm\\:ss} / {tsDur:hh\\:mm\\:ss}";
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
                }
                else
                {
                    Stop();
                }
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

        private LibMpv.MpvGetProcAddressFn? _getProcAddress;

        protected override void OnOpenGlInit(GlInterface gl)
        {
            Console.WriteLine("[VideoPlayer] OnOpenGlInit called. Initializing MPV render context...");
            base.OnOpenGlInit(gl);

            // Keep delegate alive
            _getProcAddress = (ctx, name) => gl.GetProcAddress(name);

            IntPtr openglParams = IntPtr.Zero;
            
            var paramsStruct = new LibMpv.MpvOpenglInitParams
            {
                GetProcAddress = _getProcAddress,
                UserData = IntPtr.Zero
            };
            
            IntPtr pParams = Marshal.AllocCoTaskMem(Marshal.SizeOf(paramsStruct));
            Marshal.StructureToPtr(paramsStruct, pParams, false);

            // MPV Rendering API: Opengl
            IntPtr pApiType = Marshal.StringToCoTaskMemUTF8("opengl");
            
            var paramsArr = new LibMpv.MpvRenderParam[]
            {
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.ApiType, Data = pApiType },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.InitParams, Data = pParams },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.Invalid, Data = IntPtr.Zero }
            };

            // Call create
            Console.WriteLine("[VideoPlayer] Calling mpv_render_context_create...");
            int res = LibMpv.mpv_render_context_create(out _mpvRenderContext, _player.Handle, paramsArr);
            Console.WriteLine($"[VideoPlayer] mpv_render_context_create returned: {res}");
            
            // Clean up temporary pointers
            Marshal.FreeCoTaskMem(pParams);
            Marshal.FreeCoTaskMem(pApiType);

            if (res < 0)
            {
                // Init failed
                Console.WriteLine($"[VideoPlayer] FAILED to create render context: {res}");
                // We should probably check if it failed.
                return;
            }
            
            // Set update callback
            LibMpv.mpv_render_context_set_update_callback(_mpvRenderContext, _renderUpdateDelegate, IntPtr.Zero);
            Console.WriteLine("[VideoPlayer] Render context initialized successfully.");
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            Console.WriteLine("[VideoPlayer] OnOpenGlDeinit called. Freeing context.");
            if (_mpvRenderContext != IntPtr.Zero)
            {
                LibMpv.mpv_render_context_free(_mpvRenderContext);
                _mpvRenderContext = IntPtr.Zero;
            }
            base.OnOpenGlDeinit(gl);
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (_mpvRenderContext == IntPtr.Zero)
            {
                // console log only once to avoid spam?
                // Console.WriteLine("[VideoPlayer] OnOpenGlRender: Render Context is ZERO."); 
                return;
            }

            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            int w = (int)(Bounds.Width * scaling);
            int h = (int)(Bounds.Height * scaling);
            
            // FBO param
            var fbo = new LibMpv.MpvOpenglFbo { Fbo = fb, W = w, H = h, InternalFormat = 0 }; // 0 = default (usually GL_RGBA8)
            IntPtr pFbo = Marshal.AllocCoTaskMem(Marshal.SizeOf(fbo));
            Marshal.StructureToPtr(fbo, pFbo, false);

            // FlipY param (OpenGL usually needs this when rendering to FBOs in some toolkits)
            int flipY = 1;
            IntPtr pFlipY = Marshal.AllocCoTaskMem(sizeof(int));
            Marshal.WriteInt32(pFlipY, flipY);

            // Params for render
            var paramsArr = new LibMpv.MpvRenderParam[]
            {
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.Fbo, Data = pFbo },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.FlipY, Data = pFlipY },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.Invalid, Data = IntPtr.Zero }
            };
            // LibMpv.mpv_render_context_render(this._mpvRenderContext, paramsArr);
            LibMpv.mpv_render_context_render(_mpvRenderContext, paramsArr);
            
            Marshal.FreeCoTaskMem(pFbo);
            Marshal.FreeCoTaskMem(pFlipY);
        }
        
        private void UpdateCallback(IntPtr ctx)
        {
            // Request render
            Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering, Avalonia.Threading.DispatcherPriority.Render);
        }
    }
}
