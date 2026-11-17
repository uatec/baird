using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ShowDataCacheTests
    {
        private sealed class Clock { public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); }

        private static async Task<(CatalogueTests.World W, ShowData Shows, CatalogueTitle Title, Clock Clock, List<(string Key, Func<CancellationToken, Task> Work)> Refreshes)> Setup(TimeSpan? piece = null)
        {
            var w = CatalogueTests.Build();
            var clock = new Clock();
            var shows = new ShowData(w.Data, w.Seerr, w.Search, piece ?? TimeSpan.FromMilliseconds(200), clock: () => clock.Now);
            var refreshes = new List<(string, Func<CancellationToken, Task>)>();
            shows.RefreshSink = (k, work) => { lock (refreshes) refreshes.Add((k, work)); };
            var r = await w.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow);
            return (w, shows, r.Top!.Title!, clock, refreshes);
        }

        [Fact]
        public async Task A_second_page_within_60_seconds_asks_nobody()
        {
            var (w, shows, title, clock, _) = await Setup();
            await shows.DetailsAsync(title, fresh: true);
            var status = w.Seerr.StatusCalls; var children = w.Jellyfin.ChildrenCalls + w.IPlayer.ChildrenCalls;
            Assert.True(status > 0 && children > 0);

            // Another page for the same title (its own page-cache entry) 30 s on: availability is shared and still fresh.
            clock.Now += TimeSpan.FromSeconds(30);
            await shows.DetailsAsync(title with { Key = "again" }, fresh: false);
            Assert.Equal(status, w.Seerr.StatusCalls);
            Assert.Equal(children, w.Jellyfin.ChildrenCalls + w.IPlayer.ChildrenCalls);
        }

        [Fact]
        public async Task A_stale_answer_is_served_at_once_and_refreshed_through_the_sink()
        {
            var (w, shows, title, clock, refreshes) = await Setup();
            await shows.LibrarySeasonsAsync(title);
            var calls = w.Jellyfin.ChildrenCalls + w.IPlayer.ChildrenCalls;

            clock.Now += TimeSpan.FromSeconds(61);
            w.Jellyfin.HangChildren = true; w.IPlayer.HangChildren = true;
            var task = shows.LibrarySeasonsAsync(title);
            Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))));
            var seasons = await task;
            Assert.Contains(seasons, s => s.Number == 1 && s.Episodes.Count == 2);
            Assert.Equal(calls, w.Jellyfin.ChildrenCalls + w.IPlayer.ChildrenCalls); // the refresh hasn't run: only requested
            Assert.NotEmpty(refreshes);

            // Asked again before the refresh ran: still one in flight per key.
            var n = refreshes.Count;
            await shows.LibrarySeasonsAsync(title);
            Assert.Equal(n, refreshes.Count);
        }

        [Fact]
        public async Task A_stale_answer_is_refreshed_in_the_background_when_there_is_no_sink()
        {
            var (w, shows, title, clock, _) = await Setup();
            shows.RefreshSink = null;
            await shows.LibrarySeasonsAsync(title);
            clock.Now += TimeSpan.FromSeconds(61);
            w.Jellyfin.Children["simp-s2"].Add(CatalogueTests.Item("s2e2", "Another"));
            await shows.LibrarySeasonsAsync(title); // stale: old value now, refresh behind it

            for (var i = 0; i < 100; i++)
            {
                var s = await shows.LibrarySeasonsAsync(title);
                if (s.First(x => x.Number == 2).Episodes.Count == 2) return;
                await Task.Delay(50);
            }
            Assert.Fail("the background refresh never landed");
        }

        [Fact]
        public async Task A_timed_out_listing_is_not_kept()
        {
            var (w, shows, title, _, _) = await Setup();
            w.Jellyfin.HangChildren = true; w.IPlayer.HangChildren = true;
            var empty = await shows.LibrarySeasonsAsync(title);
            Assert.Empty(empty);
            var calls = w.Jellyfin.ChildrenCalls;

            w.Jellyfin.HangChildren = false; w.IPlayer.HangChildren = false;
            var again = await shows.LibrarySeasonsAsync(title);
            Assert.True(w.Jellyfin.ChildrenCalls > calls);
            Assert.NotEmpty(again);
        }

        [Fact]
        public async Task A_timed_out_status_is_not_kept()
        {
            var (w, shows, title, _, _) = await Setup();
            w.Seerr.HangStatus = true;
            await shows.DetailsAsync(title, fresh: true);
            var calls = w.Seerr.StatusCalls;

            w.Seerr.HangStatus = false;
            await shows.DetailsAsync(title with { Key = "again" }, fresh: false);
            Assert.True(w.Seerr.StatusCalls > calls);
        }

        [Fact]
        public async Task Fresh_true_bypasses_the_availability_cache()
        {
            var (w, shows, title, _, _) = await Setup();
            await shows.LibrarySeasonsAsync(title);
            var calls = w.Jellyfin.ChildrenCalls;
            await shows.LibrarySeasonsAsync(title); // served from the cache
            Assert.Equal(calls, w.Jellyfin.ChildrenCalls);
            await shows.LibrarySeasonsAsync(title, fresh: true);
            Assert.True(w.Jellyfin.ChildrenCalls > calls);
        }

        [Fact]
        public async Task Forget_clears_the_availability_cache()
        {
            var (w, shows, title, _, _) = await Setup();
            await shows.LibrarySeasonsAsync(title);
            var calls = w.Jellyfin.ChildrenCalls;
            shows.Forget();
            await shows.LibrarySeasonsAsync(title);
            Assert.True(w.Jellyfin.ChildrenCalls > calls);
        }
    }
}
