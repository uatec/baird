using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Baird.Services
{
    public class SimpleImageLoader
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly ConcurrentDictionary<string, Bitmap> _memoryCache = new ConcurrentDictionary<string, Bitmap>();

        public static readonly AttachedProperty<string?> SourceProperty =
            AvaloniaProperty.RegisterAttached<SimpleImageLoader, Image, string?>("Source");

        public static string? GetSource(Image element)
        {
            return element.GetValue(SourceProperty);
        }

        public static void SetSource(Image element, string? value)
        {
            element.SetValue(SourceProperty, value);
        }

        static SimpleImageLoader()
        {
            SourceProperty.Changed.AddClassHandler<Image>(OnSourceChanged);
        }

        private static async void OnSourceChanged(Image image, AvaloniaPropertyChangedEventArgs args)
        {
            var url = args.NewValue as string;

            if (string.IsNullOrWhiteSpace(url))
            {
                image.Source = null;
                return;
            }

            try
            {
                var bitmap = await LoadImageAsync(url);
                Dispatcher.UIThread.Post(() => image.Source = bitmap);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimpleImageLoader] Error loading image from {url}: {ex.Message}");
                // Optionally set a placeholder or null
                image.Source = null;
            }
        }

        private static async Task<Bitmap> LoadImageAsync(string url)
        {
            if (_memoryCache.TryGetValue(url, out var cachedBitmap))
            {
                return cachedBitmap;
            }

            var data = await _httpClient.GetByteArrayAsync(url);
            using var stream = new MemoryStream(data);
            var bitmap = new Bitmap(stream);
            
            _memoryCache.TryAdd(url, bitmap);
            return bitmap;
        }
    }
}
