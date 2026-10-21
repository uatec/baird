using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RiveSharp;
using SkiaSharp;

namespace Baird.Rive
{
    /// <summary>
    /// Renders a .riv file in-process through the native Rive runtime (librive.so, built from rive-cpp)
    /// onto Avalonia's own SkiaSharp canvas. State machine inputs are set from C#.
    /// </summary>
    public class RivePlayerControl : Control
    {
        public static readonly StyledProperty<string?> SourceProperty =
            AvaloniaProperty.Register<RivePlayerControl, string?>(nameof(Source));
        public static readonly StyledProperty<string?> StateMachineProperty =
            AvaloniaProperty.Register<RivePlayerControl, string?>(nameof(StateMachine));

        private readonly object _gate = new();
        private Scene? _scene;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _lastDraw;
        private bool _attached;

        public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
        public string? StateMachine { get => GetValue(StateMachineProperty); set => SetValue(StateMachineProperty, value); }
        public string? LastError { get; private set; }
        public bool IsLoaded { get { lock (_gate) return _scene?.IsLoaded ?? false; } }
        /// <summary>Number of frames actually drawn through Skia.</summary>
        public int FramesDrawn { get; private set; }

        static RivePlayerControl()
        {
            AffectsRender<RivePlayerControl>(SourceProperty);
            SourceProperty.Changed.AddClassHandler<RivePlayerControl>((c, _) => c.Load());
            StateMachineProperty.Changed.AddClassHandler<RivePlayerControl>((c, _) => c.Load());
        }

        public void Load()
        {
            lock (_gate)
            {
                _scene = null;
                LastError = null;
                if (string.IsNullOrEmpty(Source)) return;
                try
                {
                    var path = Path.IsPathRooted(Source) ? Source : Path.Combine(AppContext.BaseDirectory, Source);
                    var scene = new Scene();
                    if (!scene.LoadFile(File.ReadAllBytes(path))) { LastError = "LoadFile failed: " + path; return; }
                    if (!scene.LoadArtboard("")) { LastError = "LoadArtboard failed"; return; }
                    var sm = StateMachine ?? "";
                    if (!scene.LoadStateMachine(sm)) { LastError = "LoadStateMachine failed: " + sm; return; }
                    _scene = scene;
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or BadImageFormatException)
                {
                    LastError = e.GetType().Name + ": " + e.Message;
                }
            }
            InvalidateVisual();
        }

        public void SetNumber(string name, float value) { lock (_gate) _scene?.SetNumber(name, value); InvalidateVisual(); }
        public void SetBool(string name, bool value) { lock (_gate) _scene?.SetBool(name, value); InvalidateVisual(); }
        public void FireTrigger(string name) { lock (_gate) _scene?.FireTrigger(name); InvalidateVisual(); }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            if (_scene == null && Source != null) Load();
            Tick();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _attached = false;
            base.OnDetachedFromVisualTree(e);
        }

        private void Tick()
        {
            if (!_attached) return;
            InvalidateVisual();
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ => Tick());
        }

        /// <summary>Advance by real elapsed time (or the given override) and draw. Safe from the render thread.</summary>
        public void RenderTo(SKCanvas canvas, double width, double height, double? dtOverride = null)
        {
            lock (_gate)
            {
                if (_scene == null || !_scene.IsLoaded) return;
                var now = _clock.Elapsed;
                var dt = dtOverride ?? Math.Min((now - _lastDraw).TotalSeconds, 0.1);
                _lastDraw = now;
                _scene.AdvanceAndApply(dt);
                var r = new Renderer(canvas);
                r.Save();
                r.Align(Fit.Contain, Alignment.Center, new AABB(0, 0, (float)width, (float)height),
                        new AABB(0, 0, _scene.Width, _scene.Height));
                _scene.Draw(r);
                r.Restore();
                FramesDrawn++;
            }
        }

        public override void Render(DrawingContext context)
        {
            context.Custom(new DrawOp(this, new Rect(Bounds.Size)));
        }

        private sealed class DrawOp : ICustomDrawOperation
        {
            private readonly RivePlayerControl _owner;
            public DrawOp(RivePlayerControl owner, Rect bounds) { _owner = owner; Bounds = bounds; }
            public Rect Bounds { get; }
            public bool HitTest(Point p) => Bounds.Contains(p);
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }
            public void Render(ImmediateDrawingContext context)
            {
                var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
                if (lease == null) return;
                using (lease) _owner.RenderTo(lease.SkCanvas, Bounds.Width, Bounds.Height);
            }
        }
    }
}
