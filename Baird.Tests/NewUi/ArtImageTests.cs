using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Baird.NewUi.Controls;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ArtImageTests
    {
        private static WriteableBitmap Bmp(int w, int h) => new(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        [AvaloniaFact]
        public void A_landscape_picture_fills_the_space()
        {
            var art = new ArtImage();
            art.Show(Bmp(1280, 720));
            Assert.False(art.IsPoster);
            Assert.Equal(Stretch.UniformToFill, art.Main.Stretch);
        }

        [AvaloniaFact]
        public void A_portrait_poster_is_shown_whole_over_a_dimmed_fill_so_nothing_is_cropped()
        {
            var art = new ArtImage();
            art.Show(Bmp(1280, 1920));
            Assert.True(art.IsPoster);
            Assert.Equal(Stretch.Uniform, art.Main.Stretch);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, art.Main.HorizontalAlignment);
            Assert.True(art.Backdrop.IsVisible);
            Assert.True(art.Backdrop.Opacity < 0.6);
        }
    }
}
