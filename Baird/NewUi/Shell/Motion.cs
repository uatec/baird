using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Shell
{
    /// <summary>
    /// Motion that animates in, out and between, with opacity and transforms only (compositor-friendly, cheap on the Pi).
    /// Lite mode (a weak or software GPU) shortens everything.
    /// </summary>
    public static class Motion
    {
        public static bool Lite { get; set; }
        public static TimeSpan Med => Lite ? T.Fast : T.Med;
        public static TimeSpan Fast => T.Fast;

        public static void Animate(Visual v, TimeSpan duration)
        {
            v.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Easing = T.Ease },
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = T.Ease },
            };
        }

        private static ITransform Translate(double x, double y) => TransformOperations.Parse($"translate({x}px, {y}px)");
        public static ITransform Scale(double s) => TransformOperations.Parse($"scale({s})");
        public static readonly ITransform None = TransformOperations.Parse("none");

        /// <summary>Fade and slide in from (dx, dy).</summary>
        public static void Enter(Visual v, double dx = 0, double dy = 0)
        {
            v.Transitions = null;
            v.Opacity = 0;
            v.RenderTransform = Translate(dx, dy);
            DispatcherTimer.RunOnce(() => { Animate(v, Med); v.Opacity = 1; v.RenderTransform = None; }, TimeSpan.FromMilliseconds(16));
        }

        /// <summary>Fade and slide out to (dx, dy), then run <paramref name="done"/>.</summary>
        public static void Exit(Visual v, double dx, double dy, Action done)
        {
            Animate(v, Lite ? T.Fast : TimeSpan.FromMilliseconds(260));
            v.Opacity = 0;
            v.RenderTransform = Translate(dx, dy);
            DispatcherTimer.RunOnce(done, Lite ? T.Fast : TimeSpan.FromMilliseconds(280));
        }
    }

    /// <summary>The lit element is always fully on screen: move a scroller just enough (prototype: keepInView).</summary>
    public static class KeepInView
    {
        public static void Ensure(Control scroller, Control lit, Visual stage, double lift = 0.07, double top = T.SafeTop, double bottom = T.SafeBottom)
        {
            var p = lit.TranslatePoint(new Point(0, 0), stage);
            if (p == null) return;
            var h = lit.Bounds.Height;
            var grow = h * lift + 8;
            var y = scroller.Tag is double d ? d : 0;
            var next = y;
            if (p.Value.Y - grow < top) next = y + (top - (p.Value.Y - grow));
            else if (p.Value.Y + h + grow > bottom) next = y - (p.Value.Y + h + grow - bottom);
            if (Math.Abs(next - y) < 0.5) return;
            scroller.Tag = next;
            if (scroller.Transitions == null) Motion.Animate(scroller, Motion.Med);
            scroller.RenderTransform = TransformOperations.Parse($"translate(0px, {next}px)");
        }
    }
}
