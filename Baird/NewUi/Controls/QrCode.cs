using System.Collections;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QRCoder;

namespace Baird.NewUi.Controls
{
    /// <summary>A QR code, drawn as squares (dark on white, with its quiet zone) so it scans from across the room.</summary>
    public sealed class QrCode : Control
    {
        private readonly List<BitArray> _modules;
        private static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#0A0A0C"));

        public QrCode(string text)
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            _modules = data.ModuleMatrix;
        }

        public int ModuleCount => _modules.Count;

        public override void Render(DrawingContext ctx)
        {
            var size = System.Math.Min(Bounds.Width, Bounds.Height);
            ctx.FillRectangle(Brushes.White, new Rect(0, 0, size, size));
            var n = _modules.Count;
            if (n == 0) return;
            // Whole-pixel squares, centred: no anti-aliased seams between neighbours (a camera reads those as gaps).
            var cell = System.Math.Max(1, System.Math.Floor(size / n));
            var offset = System.Math.Floor((size - cell * n) / 2);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.SetFillRule(FillRule.NonZero);
                for (var y = 0; y < n; y++)
                    for (var x = 0; x < n; x++)
                    {
                        if (!_modules[y][x]) continue;
                        var r = new Rect(offset + x * cell, offset + y * cell, cell, cell);
                        g.BeginFigure(r.TopLeft, true);
                        g.LineTo(r.TopRight); g.LineTo(r.BottomRight); g.LineTo(r.BottomLeft);
                        g.EndFigure(true);
                    }
            }
            ctx.DrawGeometry(Dark, null, geometry);
        }
    }
}
