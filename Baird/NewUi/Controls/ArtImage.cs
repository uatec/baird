using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Controls
{
    /// <summary>
    /// Artwork that respects its shape. A landscape picture fills the space. A portrait poster (most guide pictures) is
    /// shown whole on the right, over a dimmed, zoomed copy of itself for colour, so faces and titles are never cropped.
    /// Fades in once loaded.
    /// </summary>
    public sealed class ArtImage : Panel
    {
        public Image Backdrop { get; } = new() { Stretch = Stretch.UniformToFill, IsVisible = false, Opacity = 0.35 };
        public Image Main { get; } = new() { Stretch = Stretch.UniformToFill };
        public bool IsPoster { get; private set; }

        /// <summary>Called with the picture's shape once known (e.g. to change the scrim).</summary>
        public event Action<bool>? Shown;

        public ArtImage()
        {
            ClipToBounds = true;
            Opacity = 0;
            Motion.Animate(this, T.Slow);
            Children.Add(Backdrop);
            Children.Add(Main);
        }

        public ArtImage(string url) : this() => _ = LoadAsync(url);

        private async Task LoadAsync(string url)
        {
            try
            {
                var bmp = await AsyncImageLoader.ImageLoader.AsyncImageLoader.ProvideImageAsync(url);
                if (bmp != null) Dispatcher.UIThread.Post(() => Show(bmp));
            }
            catch (Exception ex) { Console.WriteLine($"[ArtImage] {url}: {ex.Message}"); }
        }

        public void Show(Bitmap bmp)
        {
            IsPoster = bmp.Size.Height > bmp.Size.Width * 1.1;
            Main.Source = bmp;
            if (IsPoster)
            {
                Backdrop.Source = bmp;
                Backdrop.IsVisible = true;
                Main.Stretch = Stretch.Uniform;
                Main.HorizontalAlignment = HorizontalAlignment.Right;
                Main.VerticalAlignment = VerticalAlignment.Stretch;
            }
            else
            {
                Backdrop.IsVisible = false;
                Main.Stretch = Stretch.UniformToFill;
                Main.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            Opacity = 1;
            Shown?.Invoke(IsPoster);
        }
    }
}
