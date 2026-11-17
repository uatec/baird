using System;
using Baird.NewUi.Adapters;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class StaleCacheTests
    {
        [Fact]
        public void Fresh_then_stale_after_60_seconds_and_still_served()
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var cache = new StaleCache<string>(TimeSpan.FromSeconds(60), () => now);
            Assert.False(cache.TryGet("k", out _, out _));
            cache.Put("k", "a");

            Assert.True(cache.TryGet("k", out var v, out var stale));
            Assert.Equal("a", v); Assert.False(stale);

            now += TimeSpan.FromSeconds(59);
            Assert.True(cache.TryGet("k", out _, out stale)); Assert.False(stale);

            now += TimeSpan.FromSeconds(2);
            Assert.True(cache.TryGet("k", out v, out stale));
            Assert.Equal("a", v); Assert.True(stale);

            now += TimeSpan.FromDays(30); // never expires
            Assert.True(cache.TryGet("k", out v, out stale));
            Assert.Equal("a", v); Assert.True(stale);
        }

        [Fact]
        public void Put_replaces()
        {
            var now = DateTimeOffset.UnixEpoch;
            var cache = new StaleCache<string>(TimeSpan.FromSeconds(60), () => now);
            cache.Put("k", "a");
            now += TimeSpan.FromSeconds(120);
            cache.Put("k", "b");
            Assert.True(cache.TryGet("k", out var v, out var stale));
            Assert.Equal("b", v); Assert.False(stale);
        }
    }
}
