using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Baird.NewUi.Controls
{
    /// <summary>
    /// Line icons drawn as paths on a 24-unit grid (the bundled font has no ⌫ or 🎤, and the Pi has no emoji font).
    /// </summary>
    public sealed class Icon : Viewbox
    {
        public const string Search = "M17 10.5 A6.5 6.5 0 1 1 4 10.5 A6.5 6.5 0 1 1 17 10.5 Z M15.2 15.2 L21 21";
        /// <summary>The Mac delete key: a tag pointing left with a cross in it.</summary>
        public const string Delete = "M8.5 5 H19.5 A2 2 0 0 1 21.5 7 V17 A2 2 0 0 1 19.5 19 H8.5 L2.5 12 Z M11 9 L17 15 M17 9 L11 15";
        public const string Mic = "M12 3 A3 3 0 0 1 15 6 V11 A3 3 0 0 1 9 11 V6 A3 3 0 0 1 12 3 Z M5.5 11 A6.5 6.5 0 0 0 18.5 11 M12 17.5 V21 M8.5 21 H15.5";
        public const string Play = "M7 4.5 L19.5 12 L7 19.5 Z";
        public const string Get = "M12 3.5 V15 M7 10.5 L12 15.5 L17 10.5 M4.5 20 H19.5";
        public const string Check = "M5 12.5 L10 17.5 L19 7";
        public const string Plus = "M12 5 V19 M5 12 H19";
        public const string Series = "M3.5 6.5 H20.5 V17.5 H3.5 Z M8 20.5 H16";

        private readonly Avalonia.Controls.Shapes.Path _path;
        private readonly bool _filled;

        public Icon(string geometry, double size, IBrush brush, bool filled = false)
        {
            Width = size; Height = size; Stretch = Stretch.Uniform;
            _filled = filled;
            _path = new Avalonia.Controls.Shapes.Path { Data = StreamGeometry.Parse(geometry), StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
            Brush = brush;
            Child = new Canvas { Width = 24, Height = 24, Children = { _path } };
        }

        public IBrush Brush
        {
            set { _path.Stroke = value; _path.Fill = _filled ? value : null; }
        }
    }
}
