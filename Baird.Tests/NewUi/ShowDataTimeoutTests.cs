using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ShowDataTimeoutTests
    {
        private static async Task<ShowDetails> Simpsons(CatalogueTests.World w, ShowData shows)
        {
            var r = await w.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow);
            return await shows.DetailsAsync(r.Top!.Title!, fresh: true);
        }

        [Fact]
        public async Task A_hung_season_listing_does_not_hold_the_page()
        {
            var w = CatalogueTests.Build();
            var shows = new ShowData(w.Data, w.Seerr, w.Search, TimeSpan.FromMilliseconds(200));
            var r = await w.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow);
            w.Jellyfin.HangChildren = true;

            var sw = Stopwatch.StartNew();
            var task = shows.DetailsAsync(r.Top!.Title!, fresh: true);
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(task, done);
            var d = await task;
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
            Assert.DoesNotContain(d.Seasons, s => s.State is SeasonState.Available or SeasonState.Partial);
        }

        [Fact]
        public async Task A_hung_seerr_status_leaves_the_library_seasons()
        {
            var w = CatalogueTests.Build();
            var shows = new ShowData(w.Data, w.Seerr, w.Search, TimeSpan.FromMilliseconds(200));
            var r = await w.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow);
            w.Seerr.HangStatus = true;

            var task = shows.DetailsAsync(r.Top!.Title!, fresh: true);
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(task, done);
            var d = await task;
            Assert.Equal(new[] { 1, 2 }, d.Seasons.Where(s => s.Episodes.Any(e => e.State == EpisodeState.Available)).Select(s => s.Number));
        }

        [Fact]
        public async Task Nothing_by_the_budget_is_an_error()
        {
            var w = CatalogueTests.Build();
            var shows = new ShowData(w.Data, w.Seerr, w.Search, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200));
            var r = await w.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow);
            w.Jellyfin.HangChildren = true; w.IPlayer.HangChildren = true; w.Seerr.HangStatus = true;

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAsync<ShowLoadTimeoutException>(() => shows.DetailsAsync(r.Top!.Title!, fresh: true));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
        }

        [Fact]
        public async Task Something_by_the_budget_is_returned_not_an_error()
        {
            var w = CatalogueTests.Build();
            var shows = new ShowData(w.Data, w.Seerr, w.Search, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300));
            w.Jellyfin.HangSearch = true; w.IPlayer.HangSearch = true;
            var severance = ShowData.FromCard(new HomeCard(CardKind.Series, "tmdb:tv:95396", "Severance", "2022", null, null, "get", "Seerr", PlayVia.None));

            var d = await shows.DetailsAsync(severance, fresh: true);
            Assert.False(d.CheckingLibrary);
            Assert.Equal(new[] { 1, 2 }, d.Seasons.Select(s => s.Number));
        }
    }
}
