using System;
using System.Threading.Tasks;
using AsyncImageLoader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Baird.NewUi.Controls;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ImageSourceTests
    {
        private sealed class Loader : IAsyncImageLoader
        {
            public Func<string, Task<Bitmap?>> Provide = _ => Task.FromResult<Bitmap?>(null);
            public Task<Bitmap?> ProvideImageAsync(string url) => Provide(url);
            public void Dispose() { }
        }

        private static IDisposable Use(Loader loader)
        {
            var old = AsyncImageLoader.ImageLoader.AsyncImageLoader;
            AsyncImageLoader.ImageLoader.AsyncImageLoader = loader;
            return new Restore(old);
        }

        private sealed class Restore(IAsyncImageLoader old) : IDisposable { public void Dispose() => AsyncImageLoader.ImageLoader.AsyncImageLoader = old; }

        [AvaloniaFact]
        public async Task A_picture_that_arrives_on_another_thread_is_applied_on_the_ui_thread()
        {
            var bmp = new WriteableBitmap(new PixelSize(16, 9), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var _ = Use(new Loader { Provide = async _ => { await Task.Delay(20).ConfigureAwait(false); return bmp; } });
            var img = new Image();
            // Called from a background thread with no synchronization context, as a stray refresh does.
            await Task.Run(() => ImageSource.Set(img, "http://img/a"));
            for (var i = 0; i < 100 && img.Source == null; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Assert.Same(bmp, img.Source);
        }

        [AvaloniaFact]
        public async Task A_load_that_fails_is_logged_and_never_thrown()
        {
            using var _ = Use(new Loader { Provide = _ => throw new InvalidOperationException("boom") });
            var img = new Image();
            await ImageSource.Set(img, "http://img/b");   // completes normally
            Assert.Null(img.Source);
        }

        [AvaloniaFact]
        public async Task No_address_loads_nothing()
        {
            var called = false;
            using var _ = Use(new Loader { Provide = u => { called = true; return Task.FromResult<Bitmap?>(null); } });
            await ImageSource.Set(new Image(), null);
            await ImageSource.Set(new Image(), "");
            Assert.False(called);
        }
    }
}
