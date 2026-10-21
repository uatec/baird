using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Baird.Rive;
using SkiaSharp;
using Xunit;

namespace Baird.Tests.Controls
{
    public class RivePlayerControlTests
    {
        private const int W = 400, H = 200;

        private static RivePlayerControl Make()
        {
            var c = new RivePlayerControl { Source = "avares://Baird/Assets/Rive/rating.riv", StateMachine = "State Machine 1" };
            c.Load();
            Assert.True(c.IsLoaded, c.LastError);
            return c;
        }

        private static byte[] Frame(RivePlayerControl c, double dt)
        {
            using var bmp = new SKBitmap(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Black);
            c.RenderTo(canvas, W, H, dt);
            return bmp.Bytes;
        }

        private static int NonBlack(byte[] px)
        {
            int n = 0;
            for (int i = 0; i < px.Length; i += 4)
                if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0) n++;
            return n;
        }

        private static int Diff(byte[] a, byte[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i += 4)
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) n++;
            return n;
        }

        [AvaloniaFact]
        public void RivFile_RendersNonBlankPixels()
        {
            var c = Make();
            c.SetNumber("rating", 3);
            Frame(c, 0.0);
            var px = Frame(c, 1.0);
            Assert.True(NonBlack(px) > 500, "non-black pixels: " + NonBlack(px));
        }

        [AvaloniaFact]
        public void StateMachineInput_FromCSharp_ChangesRenderedFrame()
        {
            var c = Make();
            c.SetNumber("rating", 0);
            Frame(c, 0.0);
            var low = Frame(c, 2.0);

            c.SetNumber("rating", 5);
            Frame(c, 0.0);
            var high = Frame(c, 2.0);

            var changed = Diff(low, high);
            Console.WriteLine($"rating 0 vs 5: {changed} of {W * H} pixels differ; nonblack {NonBlack(low)} / {NonBlack(high)}");
            Assert.True(changed > 500, "differing pixels: " + changed);

            // Optional artefacts for visual inspection
            var dir = Environment.GetEnvironmentVariable("RIVE_OUT");
            if (!string.IsNullOrEmpty(dir))
            {
                Save(low, Path.Combine(dir, "rating0.png"));
                Save(high, Path.Combine(dir, "rating5.png"));
            }
        }

        [AvaloniaFact]
        public void Animation_AdvancesOverTime()
        {
            var c = Make();
            c.SetNumber("rating", 5);
            var a = Frame(c, 0.0);
            var b = Frame(c, 0.25);
            Console.WriteLine($"t=0 vs t=0.25 differ in {Diff(a, b)} pixels");
            Assert.True(Diff(a, b) > 0);
        }

        [AvaloniaFact]
        public void FullAvaloniaPipeline_RiveSpikeControl_RenderedFramesDifferWhenInputChanges()
        {
            var vm = new Baird.ViewModels.RiveSpikeViewModel { Rating = 0 };
            var view = new Baird.Controls.RiveSpikeControl { DataContext = vm, Width = 800, Height = 500 };
            var window = new Avalonia.Controls.Window { Width = 800, Height = 500, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window); // warm-up frame
            var f0 = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!;

            vm.Rating = 5;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            var f5 = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!;

            var player = Avalonia.VisualTree.VisualExtensions.FindDescendantOfType<RivePlayerControl>(view)!;
            Assert.True(player.IsLoaded, player.LastError);
            Assert.True(player.FramesDrawn > 0, "Rive never drew through Avalonia's Skia canvas");

            int changed = CountDiff(f0, f5);
            Console.WriteLine($"Avalonia headless: frames drawn={player.FramesDrawn}, differing pixels rating0->5 = {changed}");
            Assert.True(changed > 500);
            var dir = Environment.GetEnvironmentVariable("RIVE_OUT");
            if (!string.IsNullOrEmpty(dir)) { f0.Save(Path.Combine(dir, "avalonia0.png")); f5.Save(Path.Combine(dir, "avalonia5.png")); }
        }

        private static int CountDiff(Avalonia.Media.Imaging.WriteableBitmap a, Avalonia.Media.Imaging.WriteableBitmap b)
        {
            static byte[] Bytes(Avalonia.Media.Imaging.WriteableBitmap w)
            {
                using var ms = new MemoryStream(); w.Save(ms);
                using var bmp = SKBitmap.Decode(ms.ToArray());
                return bmp.Bytes;
            }
            var x = Bytes(a); var y = Bytes(b);
            int n = 0;
            for (int i = 0; i < Math.Min(x.Length, y.Length); i += 4)
                if (x[i] != y[i] || x[i + 1] != y[i + 1] || x[i + 2] != y[i + 2]) n++;
            return n;
        }

        private static void Save(byte[] px, string path)
        {
            using var bmp = new SKBitmap(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
            System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
            using var img = SKImage.FromBitmap(bmp);
            using var fs = File.OpenWrite(path);
            img.Encode(SKEncodedImageFormat.Png, 100).SaveTo(fs);
        }
    }
}
