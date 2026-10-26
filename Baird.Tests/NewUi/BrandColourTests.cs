using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Baird.NewUi.Controls;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class BrandColourTests
    {
        /// <summary>A logo-like bitmap: a coloured mark on transparent, with some white lettering.</summary>
        private static WriteableBitmap Logo(Color mark)
        {
            var bmp = new WriteableBitmap(new PixelSize(40, 20), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var fb = bmp.Lock();
            var px = new byte[40 * 20 * 4];
            for (var y = 0; y < 20; y++)
                for (var x = 0; x < 40; x++)
                {
                    var i = (y * 40 + x) * 4;
                    var c = x < 16 ? mark : x < 24 ? Colors.White : Color.FromArgb(0, 0, 0, 0);
                    px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = c.A;
                }
            for (var y = 0; y < 20; y++) Marshal.Copy(px, y * 40 * 4, fb.Address + y * fb.RowBytes, 40 * 4);
            return bmp;
        }

        [AvaloniaFact]
        public void The_brand_colour_is_the_logos_dominant_colour_not_its_white_or_transparency()
        {
            var c = BrandColour.Of(Logo(Color.Parse("#00A0A0")))!.Value; // a teal mark (ITV-ish)
            var hsv = c.ToHsv();
            Assert.InRange(hsv.H, 170, 190);
        }

        [AvaloniaFact]
        public void A_black_and_white_logo_has_no_brand_colour()
        {
            Assert.Null(BrandColour.Of(Logo(Colors.Black)));
        }
    }
}
