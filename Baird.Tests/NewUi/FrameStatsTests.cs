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
    }
}
