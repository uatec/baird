using System.IO;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Baird.ViewModels;
using Lottie = Avalonia.Labs.Lottie.Lottie;
using Xunit;

namespace Baird.Tests.Controls
{
    public class LottieRenderTests
    {
        public static TheoryData<string> Assets => new()
        {
            "avares://Baird/Assets/Lottie/loading-ring.json",
            "avares://Baird/Assets/Lottie/audio-bars.json",
            "avares://Baird/Assets/Lottie/play-pause.json",
            "avares://Baird/Assets/Lottie/wave-loader.json",
        };

        [AvaloniaTheory]
        [MemberData(nameof(Assets))]
        public void Asset_IsEmbeddedAndParsesWithSkottie(string path)
        {
            var r = LottieProbe.Probe(path);
            Assert.True(r.Loaded, r.Summary);
            Assert.True(r.DurationSeconds > 0);
            Assert.True(r.Width > 0 && r.Height > 0);
        }

        [AvaloniaTheory]
        [MemberData(nameof(Assets))]
        public void Asset_RendersNonBlank_AndFramesDiffer(string path)
        {
            var a = RenderAt(path, 0.15f);
            var b = RenderAt(path, 0.6f);

            var nonBlankA = CountNonBackground(a);
            var nonBlankB = CountNonBackground(b);
            var diff = CountDiff(a, b);
            System.Console.WriteLine($"[render] {Path.GetFileName(path)}: nonBlank@0.15={nonBlankA} nonBlank@0.6={nonBlankB} differingPixels={diff}");

            Assert.True(nonBlankA > 50, $"frame@0.15 blank for {path}");
            Assert.True(nonBlankB > 50, $"frame@0.6 blank for {path}");
            Assert.True(diff > 100, $"frames identical for {path}");
        }

        [AvaloniaFact]
        public void ViewModel_ReportsLoadStatus_ForEveryAnimation_AndFailureIsVisible()
        {
            var vm = new LottieSpikeViewModel();
            foreach (var a in vm.Animations)
            {
                vm.SelectedAnimation = a;
                Assert.True(vm.LoadSucceeded, vm.LoadStatusText);
                Assert.Contains("Loaded OK", vm.LoadStatusText);
            }

            var bad = LottieProbe.Probe("avares://Baird/Assets/Lottie/does-not-exist.json");
            Assert.False(bad.Loaded);
            Assert.Contains("FAILED", bad.Summary);
        }

        private static byte[] RenderAt(string path, float progress)
        {
            var lottie = new Lottie(new System.Uri("avares://Baird/"))
            {
                Path = path,
                Width = 200,
                Height = 200,
                Stretch = Stretch.Uniform,
                AutoPlay = true,
                RepeatCount = 1,
            };
            var window = new Window
            {
                Width = 200,
                Height = 200,
                Background = Brushes.Black,
                Content = lottie,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            lottie.Pause();
            lottie.SeekToProgress(progress);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame()!;
            using var ms = new MemoryStream();
            frame.Save(ms);
            window.Close();
            using var bmp = SkiaSharp.SKBitmap.Decode(ms.ToArray());
            return bmp.Bytes;
        }

        private static int CountNonBackground(byte[] px)
        {
            int n = 0;
            // Count pixels that differ from the top-left corner pixel (the effective background).
            for (int i = 4; i + 3 < px.Length; i += 4)
                if (px[i] != px[0] || px[i + 1] != px[1] || px[i + 2] != px[2]) n++;
            return n;
        }

        private static int CountDiff(byte[] a, byte[] b)
        {
            int n = 0;
            for (int i = 0; i + 3 < a.Length && i + 3 < b.Length; i += 4)
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) n++;
            return n;
        }
    }
}
