using System;
using Baird.NewUi.Diagnostics;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class FrameStatsTests
    {
        [Fact]
        public void SteadySixtyHz_ReportsSixtyFpsAndNoSlowFrames()
        {
            var stats = new FrameStats(TimeSpan.FromSeconds(1));
            string? line = null;
            for (var i = 0; i <= 60; i++) line = stats.Frame(TimeSpan.FromMilliseconds(i * 1000.0 / 60)) ?? line;
            Assert.NotNull(line);
            Assert.Contains("fps=60.", line);
            Assert.Contains("slow(>25ms)=0", line);
        }

        [Fact]
        public void Hitches_AreCountedAndTheWorstGapReported()
        {
            var stats = new FrameStats(TimeSpan.FromSeconds(1));
            var t = 0.0; string? line = null;
            foreach (var gap in new[] { 16.0, 16, 80, 16, 40, 16, 900, 16 }) { t += gap; line = stats.Frame(TimeSpan.FromMilliseconds(t)) ?? line; }
            Assert.NotNull(line);
            Assert.Contains("worst_gap=", line);
            Assert.Contains("slow(>25ms)=", line);
        }

        [Fact]
        public void BeforeTheWindowEnds_NothingIsReported()
        {
            var stats = new FrameStats(TimeSpan.FromSeconds(10));
            for (var i = 0; i < 100; i++) Assert.Null(stats.Frame(TimeSpan.FromMilliseconds(i * 16)));
        }

        [Fact]
        public void Breadcrumbs_RaiseAddedWithTheStoredText()
        {
            var crumbs = new Breadcrumbs();
            string? seen = null;
            crumbs.Added += t => seen = t;
            crumbs.Add("key Right");
            Assert.EndsWith("key Right", seen);
            Assert.EndsWith("key Right", Assert.Single(crumbs.Snapshot().Recent));
        }

        [Fact]
        public void Key_bursts_count_the_frames_after_a_key_and_the_gaps_between_them()
        {
            var b = new KeyBursts();
            Assert.Null(b.Frame(TimeSpan.FromMilliseconds(0)));          // no key yet: nothing is counted
            b.Key("Down");
            string? line = null;
            // the first frame after the key starts the count; then 16 ms frames with one 60 ms hitch
            var t = 100.0;
            line ??= b.Frame(TimeSpan.FromMilliseconds(t));
            foreach (var gap in new[] { 16, 16, 60, 16, 16, 16, 16 }) { t += gap; line ??= b.Frame(TimeSpan.FromMilliseconds(t)); }
            Assert.Null(line);                                           // not 700 ms yet
            while (line == null) { t += 16; line = b.Frame(TimeSpan.FromMilliseconds(t)); }
            Assert.StartsWith("keyburst Down frames=", line);
            Assert.Contains("worst_gap=60ms", line);
            Assert.Contains("slow(>25ms)=1", line);
        }

        [Fact]
        public void A_key_pressed_during_a_burst_does_not_restart_it_and_the_next_key_starts_the_next()
        {
            var b = new KeyBursts();
            b.Key("Down"); b.Frame(TimeSpan.Zero);
            b.Key("Right");                                              // held key: ignored while counting
            var line = b.Frame(KeyBursts.Window);
            Assert.StartsWith("keyburst Down ", line);
            b.Key("Right"); b.Frame(TimeSpan.FromSeconds(5));
            Assert.StartsWith("keyburst Right ", b.Frame(TimeSpan.FromSeconds(5) + KeyBursts.Window));
        }
    }
}
