using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AsyncImageLoader.Loaders;
using Avalonia.Media.Imaging;

namespace Baird.NewUi.Controls
{
    /// <summary>
    /// Replaces the library's default loader, which keeps every picture it has ever decoded for the life of the process
    /// (on the Pi that grew Baird to several GB of native memory and made Home hang in swap) and forgets failures, so a
    /// broken artwork link was fetched again every few minutes. This one keeps a byte-budgeted set of recent pictures,
    /// shares a fetch already under way, and leaves failed links alone for a while.
    /// </summary>
    public sealed class BoundedImageLoader : BaseWebImageLoader
    {
        public const long DefaultBudgetBytes = 240L * 1024 * 1024;
        public static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(30);

        /// <summary>Counters for resources.log.</summary>
        public static int CachedCount => Current?._cache.Count ?? 0;
        public static long CachedBytes => Current?._cache.Bytes ?? 0;
        public static long Decoded, Evicted, Failed, FailedSkipped;
        public static BoundedImageLoader? Current { get; private set; }

        /// <summary>Pictures are decoded no taller than the screen: a 2000x3000 poster is 24 MB of pixels, 720x1080 is 3 MB.</summary>
        public const int MaxDecodedHeight = 1080;

        private readonly ConcurrentDictionary<string, int> _requests = new();

        /// <summary>The most-requested links ("3x url"), to find what keeps asking for the same picture.</summary>
        public static string TopRequested(int count = 3) => Current == null ? "" : string.Join(" | ", System.Linq.Enumerable.Select(
            System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(Current._requests, r => r.Value), count), r => $"{r.Value}x {Shorten(r.Key)}"));

        private readonly ByteBudgetLru<Bitmap> _cache;
        private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inFlight = new();
        private readonly Dictionary<string, DateTime> _failedAt = new();
        private readonly Func<DateTime> _now;

        public BoundedImageLoader(long budgetBytes = DefaultBudgetBytes, Func<DateTime>? now = null)
        {
            _cache = new ByteBudgetLru<Bitmap>(budgetBytes, b => (long)b.PixelSize.Width * b.PixelSize.Height * 4);
            _now = now ?? (() => DateTime.UtcNow);
        }

        /// <summary>Make this the loader every AsyncImageLoader control and Baird's own artwork use.</summary>
        public static void Install()
        {
            var loader = new BoundedImageLoader();
            Current = loader;
            global::AsyncImageLoader.ImageLoader.AsyncImageLoader = loader;
        }

        public override async Task<Bitmap?> ProvideImageAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            _requests.AddOrUpdate(url, 1, (_, n) => n + 1);
            if (_cache.TryGet(url, out var hit)) return hit;
            lock (_failedAt)
            {
                if (_failedAt.TryGetValue(url, out var when))
                {
                    if (_now() - when < FailureMemory) { System.Threading.Interlocked.Increment(ref FailedSkipped); return null; }
                    _failedAt.Remove(url);
                }
            }
            return await _inFlight.GetOrAdd(url, LoadOnceAsync).ConfigureAwait(false);
        }

        private async Task<Bitmap?> LoadOnceAsync(string url)
        {
            try
            {
                var bmp = await base.ProvideImageAsync(url).ConfigureAwait(false);
                if (bmp == null) { RememberFailure(url); return null; }
                System.Threading.Interlocked.Increment(ref Decoded);
                var dropped = _cache.Put(url, bmp);
                if (dropped > 0) System.Threading.Interlocked.Add(ref Evicted, dropped);
                return bmp;
            }
            catch (Exception ex)
            {
                RememberFailure(url);
                Console.WriteLine($"[Images] {Shorten(url)}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally { _inFlight.TryRemove(url, out _); }
        }

        /// <summary>Fetch, then decode at screen size rather than full size (local files keep the library's way).</summary>
        protected override async Task<Bitmap?> LoadAsync(string url)
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return await base.LoadAsync(url).ConfigureAwait(false);
            var bytes = await LoadDataFromExternalAsync(url).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return null;
            using var stream = new System.IO.MemoryStream(bytes);
            return Bitmap.DecodeToHeight(stream, MaxDecodedHeight, BitmapInterpolationMode.MediumQuality);
        }

        private void RememberFailure(string url)
        {
            System.Threading.Interlocked.Increment(ref Failed);
            lock (_failedAt) _failedAt[url] = _now();
        }

        public static string Shorten(string url) => url.Length <= 80 ? url : url[..80] + "…";
    }
}
