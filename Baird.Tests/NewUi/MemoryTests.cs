using System;
using Baird.NewUi.Controls;
using Baird.NewUi.Diagnostics;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ByteBudgetLruTests
    {
        [Fact]
        public void Drops_the_least_recently_used_once_over_budget()
        {
            var lru = new ByteBudgetLru<string>(10, s => s.Length);
            lru.Put("a", "1234"); lru.Put("b", "1234");
            Assert.True(lru.TryGet("a", out _));            // a is now the most recent
            Assert.Equal(1, lru.Put("c", "1234"));           // 12 > 10: b goes
            Assert.False(lru.TryGet("b", out _));
            Assert.True(lru.TryGet("a", out _));
            Assert.True(lru.TryGet("c", out _));
            Assert.Equal(8, lru.Bytes);
        }

        [Fact]
        public void Always_keeps_the_newest_even_if_it_alone_is_over_budget()
        {
            var lru = new ByteBudgetLru<string>(3, s => s.Length);
            lru.Put("a", "12"); lru.Put("big", "123456789");
            Assert.True(lru.TryGet("big", out _));
            Assert.Equal(1, lru.Count);
        }

        [Fact]
        public void Replacing_a_key_does_not_double_count()
        {
            var lru = new ByteBudgetLru<string>(100, s => s.Length);
            lru.Put("a", "1234"); lru.Put("a", "12");
            Assert.Equal(2, lru.Bytes); Assert.Equal(1, lru.Count);
        }
    }

    public class MemoryGuardTests
    {
        private static readonly DateTime T = new(2026, 11, 10, 3, 0, 0);

        private static (MemoryGuard Guard, Func<int> Restarts) Make(long anonKb, bool playing, TimeSpan sinceInput)
        {
            var restarts = 0;
            var g = new MemoryGuard(() => anonKb, () => playing, () => T - sinceInput, () => T, () => restarts++, limitKb: 1000);
            return (g, () => restarts);
        }

        [Fact] public void Restarts_when_big_idle_and_not_playing()
        { var (g, n) = Make(2000, false, TimeSpan.FromMinutes(11)); Assert.True(g.Check()); Assert.Equal(1, n()); }

        [Fact] public void Not_while_under_the_limit()
        { var (g, n) = Make(500, false, TimeSpan.FromHours(2)); Assert.False(g.Check()); Assert.Equal(0, n()); }

        [Fact] public void Not_while_playing()
        { var (g, n) = Make(2000, true, TimeSpan.FromHours(2)); Assert.False(g.Check()); Assert.Equal(0, n()); }

        [Fact] public void Not_while_someone_is_using_it()
        { var (g, n) = Make(2000, false, TimeSpan.FromMinutes(2)); Assert.False(g.Check()); Assert.Equal(0, n()); }

        [Fact] public void Asks_only_once()
        { var (g, n) = Make(2000, false, TimeSpan.FromHours(1)); g.Check(); g.Check(); Assert.Equal(1, n()); }
    }
}
