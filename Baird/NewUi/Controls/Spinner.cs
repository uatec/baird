using System;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace Baird.NewUi.Controls
{
    /// <summary>
    /// A quarter ring that turns while something loads. It turns by a rotation (the ring itself is drawn once), about
    /// 25 times a second: cheap enough for the Pi, where it used to stand still (Lite mode never started it).
    /// </summary>
    public sealed class Spinner : Arc
    {
        public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(40);
        private readonly RotateTransform _turn = new();
        private readonly DispatcherTimer _timer = new() { Interval = Tick };

        public Spinner(double size, double thickness)
        {
            Width = size; Height = size;
            StartAngle = 0; SweepAngle = 90;
            Stroke = Brushes.White; StrokeThickness = thickness;
            RenderTransform = _turn; // about the centre (the default origin)
            _timer.Tick += (_, _) => _turn.Angle = (_turn.Angle + 14) % 360;
        }

        public bool Spinning => _timer.IsEnabled;

        public void Start() { IsVisible = true; _timer.Start(); }

        public void Stop() { _timer.Stop(); IsVisible = false; }
    }
}
