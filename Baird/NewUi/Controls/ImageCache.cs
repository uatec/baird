using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Baird.NewUi.Controls
{
    /// <summary>
    /// Decoded artwork kept for the pages you're about to open (prefetched while a card is lit), so a show page's
    /// backdrop and episode stills appear at once instead of fading in. Small (most recent first), loaded through the
    /// same loader cards use.
    /// </summary>
    public static class ImageCache
    {
        public const int Capacity = 48;
        private static readonly LinkedList<(string Url, Bitmap Bitmap)> Order = new();
        private static readonly Dictionary<string, LinkedListNode<(string Url, Bitmap Bitmap)>> ByUrl = new();

        /// <summary>How images are fetched and decoded (tests replace it).</summary>
        public static Func<string, Task<Bitmap?>> Load { get; set; } = url => AsyncImageLoader.ImageLoader.AsyncImageLoader.ProvideImageAsync(url);

        public static bool TryGet(string? url, out Bitmap? bitmap)
        {
            bitmap = null;
            if (string.IsNullOrEmpty(url)) return false;
            lock (Order)
            {
                if (!ByUrl.TryGetValue(url, out var node)) return false;
                Order.Remove(node); Order.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
        }

        public static void Put(string url, Bitmap bitmap)
        {
            lock (Order)
            {
                if (ByUrl.TryGetValue(url, out var old)) Order.Remove(old);
                ByUrl[url] = Order.AddFirst((url, bitmap));
                while (Order.Count > Capacity) { ByUrl.Remove(Order.Last!.Value.Url); Order.RemoveLast(); }
            }
        }

        public static bool Contains(string url) { lock (Order) return ByUrl.ContainsKey(url); }

        /// <summary>Fetch and keep one image (nothing if it's already here or can't be loaded).</summary>
        public static async Task WarmAsync(string url)
        {
            if (Contains(url)) return;
            try
            {
                var bmp = await Load(url).ConfigureAwait(false);
                if (bmp != null) Put(url, bmp);
            }
            catch (Exception ex) { Console.WriteLine($"[ImageCache] {url}: {ex.Message}"); }
        }

        /// <summary>How images are fetched to the disk cache without being decoded (tests replace it).</summary>
        public static Func<string, Task> FetchToDisk { get; set; } = url => BoundedImageLoader.Current is { } loader ? loader.WarmDiskAsync(url) : Task.CompletedTask;

        /// <summary>
        /// Fetch one image's bytes to the disk cache, if they aren't there yet: no decode, nothing kept here, so background warming of
        /// pages the viewer may never open costs a download and a file, never memory or CPU for pictures. Never throws.
        /// </summary>
        public static async Task WarmDiskAsync(string url)
        {
            if (string.IsNullOrEmpty(url) || Contains(url)) return;
            try { await FetchToDisk(url).ConfigureAwait(false); }
            catch (Exception ex) { Console.WriteLine($"[ImageCache] {url}: {ex.Message}"); }
        }

        public static void Clear() { lock (Order) { Order.Clear(); ByUrl.Clear(); } }
    }
}
