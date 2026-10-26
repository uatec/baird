using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class IntroTests
    {
        private static async Task Wait(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); } }

        private static (Intro Intro, Window Window) Shown()
        {
            var intro = new Intro();
            var w = new Window { Width = 1920, Height = 1080, Content = intro };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            return (intro, w);
        }

        [AvaloniaFact]
        public void The_first_frame_is_the_boot_splash_the_wordmark_centred_on_black()
        {
            var (intro, _) = Shown();
            var m = intro.Wordmark.TransformToVisual(intro)!.Value;
            var centre = new Point(intro.Wordmark.Bounds.Width / 2, intro.Wordmark.Bounds.Height / 2).Transform(m);
            Assert.InRange(centre.X, 955, 965);
            Assert.InRange(centre.Y, 535, 545);
            Assert.Equal(1, intro.Opacity);
        }

        [AvaloniaFact]
        public async Task It_plays_then_fades_and_says_it_is_done()
        {
            var (intro, _) = Shown();
            var done = 0;
            intro.Play(() => done++);
            await Wait(500);
            Assert.Equal(0, done);
            await Wait((int)(Intro.Duration + Intro.FadeOut).TotalMilliseconds + 300);
            Assert.Equal(1, done);
            Assert.False(intro.IsVisible);
        }

        [AvaloniaFact]
        public async Task A_key_skips_it()
        {
            var (intro, _) = Shown();
            var done = 0;
            intro.Play(() => done++);
            intro.Skip();
            await Wait((int)Intro.FadeOut.TotalMilliseconds + 300);
            Assert.Equal(1, done);
            await Wait((int)Intro.Duration.TotalMilliseconds);
            Assert.Equal(1, done);
        }

        [AvaloniaFact]
        public async Task The_disc_turns_in_around_the_wordmark()
        {
            var (intro, w) = Shown();
            intro.Play(() => { });
            for (var i = 0; i < 60; i++) { await Task.Delay(20); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
            var frame = w.CaptureRenderedFrame()!;
            var path = Environment.GetEnvironmentVariable("BAIRD_WRITE_INTRO_FRAME");
            if (!string.IsNullOrEmpty(path)) frame.Save(path);
            var stride = frame.PixelSize.Width * 4;
            var bytes = new byte[stride * frame.PixelSize.Height];
            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
            try
            {
                frame.CopyPixels(new PixelRect(frame.PixelSize), buf, bytes.Length, stride);
                System.Runtime.InteropServices.Marshal.Copy(buf, bytes, 0, bytes.Length);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
            // Somewhere on the disc's ring (radius ~320–440 px around the centre), something is lit.
            var lit = false;
            for (var y = 540 - 440; y < 540 + 440 && !lit; y += 2)
                for (var x = 960 - 440; x < 960 + 440 && !lit; x += 2)
                {
                    var r = Math.Sqrt((x - 960) * (x - 960) + (y - 540) * (y - 540));
                    if (r < 320 || r > 440) continue;
                    if (bytes[y * stride + x * 4] > 40) lit = true;
                }
            Assert.True(lit, "the disc never appeared");
        }

        [AvaloniaFact]
        public async Task It_waits_with_a_spinner_until_home_is_ready()
        {
            var (intro, _) = Shown();
            var ready = new TaskCompletionSource();
            var done = 0;
            intro.Play(() => done++, ready.Task);
            await Wait((int)(Intro.Fallback + Intro.FadeOut).TotalMilliseconds + 300);
            Assert.Equal(0, done);
            Assert.True(intro.IsVisible);
            Assert.True(intro.Waiting);
            ready.SetResult();
            await Wait((int)Intro.FadeOut.TotalMilliseconds + 400);
            Assert.Equal(1, done);
        }

        /// <summary>Writes the boot splash (the intro's first frame) when BAIRD_WRITE_SPLASH names a PNG path.</summary>
        [AvaloniaFact]
        public void Renders_the_boot_splash()
        {
            var path = Environment.GetEnvironmentVariable("BAIRD_WRITE_SPLASH");
            var (intro, w) = Shown();
            var frame = w.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(path)) frame!.Save(path);
        }
    }
}
