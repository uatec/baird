using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Baird.Mpv;

namespace Baird.NewUi.Playback
{
    /// <summary>
    /// Renders mpv into Avalonia's GL framebuffer, and nothing else. Created once for the app's lifetime and never
    /// removed from the visual tree: mpv is destroyed with its GL context. Clears to transparent when nothing is playing
    /// or a browser engine (Netflix/Disney, a window behind Baird) is showing.
    /// </summary>
    public sealed class VideoSurface : OpenGlControlBase
    {
        private readonly MpvPlayer _mpv;
        private LibMpv.MpvGetProcAddressFn? _getProcAddress; // kept alive for mpv
        private bool _ready;

        /// <summary>The GPU renderer string (e.g. "V3D 7.1" on the Pi), reported once GL is up; used to pick lite mode.</summary>
        public event Action<string>? Renderer;

        public bool ShowingBrowser { get; set; }
        public bool HasVideo { get; set; }

        public VideoSurface(MpvPlayer mpv) => _mpv = mpv;

        protected override void OnOpenGlInit(GlInterface gl)
        {
            base.OnOpenGlInit(gl);
            try
            {
                var renderer = gl.GetString(0x1F01) /* GL_RENDERER */ ?? "";
                Console.WriteLine($"[VideoSurface] GL renderer: {renderer}");
                Renderer?.Invoke(renderer);
            }
            catch (Exception ex) { Console.WriteLine($"[VideoSurface] Couldn't read the GL renderer: {ex.Message}"); }

            _getProcAddress = (_, name) => gl.GetProcAddress(name);
            try
            {
                _mpv.InitializeOpenGl(Marshal.GetFunctionPointerForDelegate(_getProcAddress),
                    () => Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render));
                _ready = true;
            }
            catch (Exception ex) { Console.WriteLine($"[VideoSurface] mpv render context failed: {ex}"); }
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            Console.WriteLine("[VideoSurface] GL going away: freeing mpv.");
            _ready = false;
            _mpv.Dispose();
            base.OnOpenGlDeinit(gl);
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (!_ready || ShowingBrowser || !HasVideo || _mpv.State == PlaybackState.Idle)
            {
                gl.ClearColor(0f, 0f, 0f, 0f);
                gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
                return;
            }
            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            _mpv.Render(fb, (int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));
        }
    }
}
