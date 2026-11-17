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

        private static async Task RunAll(List<(string Key, Func<CancellationToken, Task> Work)> refreshes, CancellationToken ct = default)
        {
            (string Key, Func<CancellationToken, Task> Work)[] taken;
            lock (refreshes) { taken = refreshes.ToArray(); refreshes.Clear(); }
            foreach (var r in taken) await r.Work(ct);
        }

        [Fact]
        public async Task A_sink_that_never_runs_the_work_is_asked_again_after_two_minutes()
        {
            var (_, shows, title, clock, refreshes) = await Setup();
            await shows.LibrarySeasonsAsync(title);
            clock.Now += TimeSpan.FromSeconds(61);
            await shows.LibrarySeasonsAsync(title);
            var first = refreshes.Count;
            Assert.True(first > 0);

            clock.Now += TimeSpan.FromSeconds(100); // under 2 minutes since the request: still one per key
            await shows.LibrarySeasonsAsync(title);
            Assert.Equal(first, refreshes.Count);

            clock.Now += TimeSpan.FromSeconds(30); // over 2 minutes: the lost refresh is given up on
            await shows.LibrarySeasonsAsync(title);
            Assert.Equal(first * 2, refreshes.Count);
        }

        [Fact]
        public async Task A_hung_refresh_of_seasons_leaves_the_old_answer()
        {
            var (w, shows, title, clock, refreshes) = await Setup();
            var before = await shows.LibrarySeasonsAsync(title);
            clock.Now += TimeSpan.FromSeconds(61);
            await shows.LibrarySeasonsAsync(title);
            w.Jellyfin.HangChildren = true; w.IPlayer.HangChildren = true;
            await RunAll(refreshes); // times out after 200 ms

            var after = await shows.LibrarySeasonsAsync(title);
            Assert.Equal(before.Select(x => (x.Number, x.Episodes.Count)), after.Select(x => (x.Number, x.Episodes.Count)));
            Assert.NotEmpty(refreshes); // still stale: asks for another refresh
        }

        [Fact]
        public async Task A_hung_refresh_of_status_leaves_the_old_answer()
        {
            var (w, shows, title, clock, refreshes) = await Setup();
            var before = await shows.DetailsAsync(title, fresh: true);
            clock.Now += TimeSpan.FromSeconds(61);
            await shows.DetailsAsync(title with { Key = "again" });
            w.Seerr.HangStatus = true;
            await RunAll(refreshes);

            var after = await shows.DetailsAsync(title with { Key = "third" });
            Assert.Equal(before.Seasons.Select(x => (x.Number, x.State)), after.Seasons.Select(x => (x.Number, x.State)));
        }

        [Fact]
        public async Task A_refresh_that_succeeds_replaces_the_answer_and_makes_it_fresh()
        {
            var (w, shows, title, clock, refreshes) = await Setup();
            await shows.LibrarySeasonsAsync(title);
            clock.Now += TimeSpan.FromSeconds(61);
            await shows.LibrarySeasonsAsync(title);
            w.Jellyfin.Children["simp-s2"].Add(CatalogueTests.Item("s2e2", "Another"));
            await RunAll(refreshes);

            var calls = w.Jellyfin.ChildrenCalls + w.IPlayer.ChildrenCalls;
            var after = await shows.LibrarySeasonsAsync(title);
            Assert.Equal(2, after.First(x => x.Number == 2).Episodes.Count);
            Assert.Equal(calls, w.Jellyfin.ChildrenCalls + w.IPlayer.ChildrenCalls);
            Assert.Empty(refreshes);
        }

        [Fact]
        public async Task A_refresh_that_throws_leaves_the_answer_and_frees_the_key()
        {
            var (w, shows, title, clock, refreshes) = await Setup();
            var before = await shows.DetailsAsync(title, fresh: true);
            clock.Now += TimeSpan.FromSeconds(61);
            await shows.DetailsAsync(title with { Key = "again" });
            w.Seerr.HangStatus = true;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel(); // a hung call under a cancelled token rethrows the cancellation: the refresh faults
            await RunAll(refreshes, cancelled.Token);

            var after = await shows.DetailsAsync(title with { Key = "third" });
            Assert.Equal(before.Seasons.Select(x => (x.Number, x.State)), after.Seasons.Select(x => (x.Number, x.State)));
            Assert.Contains(refreshes, r => r.Key.StartsWith("tv:", StringComparison.Ordinal)); // the key was freed: a new stale hit asks again
        }
    }
}
