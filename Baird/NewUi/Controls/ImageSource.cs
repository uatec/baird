using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Baird.NewUi.Controls
{
    /// <summary>
    /// Puts artwork into an <see cref="Image"/> without the library's attached <c>ImageLoader.SetSource</c>. That one is an
    /// <c>async void</c> that touches the control again after its load, on whatever thread the load finished on when the
    /// caller had no synchronization context (a background refresh calling in); if that thread is not the UI thread the
    /// exception is thrown on the thread pool and ends the process. Here a failure is logged, and the picture is handed to
    /// the UI thread to apply, so a late or stray call can never take the app down.
    /// </summary>
    public static class ImageSource
    {
        public static Task Set(Image image, string? url) => string.IsNullOrEmpty(url) ? Task.CompletedTask : LoadAsync(image, url);

        private static async Task LoadAsync(Image image, string url)
        {
            try
            {
                var bmp = await AsyncImageLoader.ImageLoader.AsyncImageLoader.ProvideImageAsync(url).ConfigureAwait(false);
                if (bmp != null) Dispatcher.UIThread.Post(() => image.Source = bmp);
            }
            catch (Exception ex) { Console.WriteLine($"[ImageSource] {BoundedImageLoader.Shorten(url)}: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
