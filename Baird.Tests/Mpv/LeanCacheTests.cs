using Baird.Mpv;
using Xunit;

namespace Baird.Tests.Mpv
{
    public class LeanCacheTests
    {
        [Fact]
        public void The_screensaver_cache_is_far_below_mpvs_150_MiB_default_and_keeps_nothing_behind()
        {
            // Measured on the Pi: default 451 MB resident after two minutes of screensaver files, 197 MB with these. See MpvPlayer.LimitCache.
            Assert.Equal("32MiB", MpvPlayer.LeanForwardCache);
            Assert.Equal("0", MpvPlayer.LeanBackCache);
        }
    }
}
