using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Baird.Models;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Baird.Services;
using Baird.Tests.Mocks;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>
    /// Netflix listed its episodes naming the show only by id (no name), so Continue watching took them for films, and their page had
    /// nothing on it. Listings now carry the show's name, and what was cached before is named from the show pages kept.
    /// </summary>
    public sealed class SeriesNameTests : IDisposable
    {
        private const string ShowId = "netflix:81678020";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "seriesname-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static MediaItemData Episode(string id, string name, string? seriesName = null) => new()
        {
            Id = id,
            Name = name,
            Details = "S1:E2",
            ImageUrl = "",
            IsLive = false,
            StreamUrl = $"https://www.netflix.com/watch/{id}",
            Source = NetflixProvider.SourceName,
            Type = MediaType.Video,
            Synopsis = "",
            Subtitle = "",
            Duration = TimeSpan.FromMinutes(11),
            SeriesId = ShowId,
            SeriesName = seriesName,
        };

        /// <summary>A provider that knows nothing by id (as Netflix: history comes from the cache) and lists the given children.</summary>
        private sealed class Lister : IMediaProvider
        {
            private readonly IEnumerable<MediaItemData> _children;
            public Lister(params MediaItemData[] children) => _children = children;
            public string Name => "Netflix";
            public Task InitializeAsync() => Task.CompletedTask;
            public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItemData>());
            public Task<IEnumerable<MediaItemData>> SearchAsync(string q, System.Threading.CancellationToken ct = default) => Task.FromResult(Enumerable.Empty<MediaItemData>());
            public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => Task.FromResult(_children);
            public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
        }

        private static (DataService Data, MockMediaDataCache Cache, HomeData Home) World(params MediaItemData[] children)
        {
            var cache = new MockMediaDataCache();
            var history = new HomeDataTests.History();
            var data = new DataService(new List<IMediaProvider> { new Lister(children) }, history, new HomeDataTests.Watchlist(), new MediaItemCache(), cache);
            var home = new HomeData(data, new MockEpgService(), new HomeDataTests.Seerr());
            return (data, cache, home);
        }

        private static void Watched(DataService data, string id) =>
            ((HomeDataTests.History)typeof(DataService).GetField("_historyService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(data)!)
                .Items.Add(new HistoryItem { Id = id, LastPosition = TimeSpan.FromMinutes(3), Duration = TimeSpan.FromMinutes(11), LastWatched = DateTime.Now });

        [Fact]
        public async Task An_episode_cached_without_its_show_name_is_an_episode_of_that_show_once_named()
        {
            var (data, cache, home) = World();
            cache.Put(Episode("netflix:ep:81950083", "Puggie Loses His Snortle"));
            Watched(data, "netflix:ep:81950083");
            Assert.Equal(CardKind.Film, (await home.UpNextAsync()).Single().Kind); // how it was: a film with no page

            Assert.Equal(1, data.NameSeries(new Dictionary<string, string> { [ShowId] = "Doozies Doghouse" }));

            var card = (await home.UpNextAsync()).Single();
            Assert.Equal(CardKind.Episode, card.Kind);
            Assert.Equal("Doozies Doghouse", card.Label);
            Assert.Equal("Doozies Doghouse", card.Series!.Name);
            Assert.Equal(ShowId, card.Series.Sources.Single().ItemId);
            Assert.Equal(0, data.NameSeries(new Dictionary<string, string> { [ShowId] = "Doozies Doghouse" })); // nothing left to name
        }

        [Fact]
        public async Task A_listing_names_the_show_of_episodes_that_name_it_only_by_id()
        {
            var named = Episode("netflix:ep:2", "Spaceship", seriesName: "Kept Name");
            var (data, cache, home) = World(Episode("netflix:ep:1", "Puggie Loses His Snortle"), named);
            cache.Put(Episode("netflix:ep:1", "Puggie Loses His Snortle"));
            Watched(data, "netflix:ep:1");
            Assert.Equal(CardKind.Film, (await home.UpNextAsync()).Single().Kind); // on Home from the cache, unnamed
            var listed = (await data.GetChildrenAsync(ShowId + "|s1:9", "Doozies Doghouse")).ToList();
            Assert.Equal("Doozies Doghouse", listed.Single(m => m.Id == "netflix:ep:1").Data.SeriesName);
            Assert.Equal("Kept Name", listed.Single(m => m.Id == "netflix:ep:2").Data.SeriesName); // a name it has is left alone
            Assert.True(cache.TryGet("netflix:ep:1", out var kept));
            Assert.Equal("Doozies Doghouse", kept!.SeriesName); // and the cache has it, for Continue watching after a restart
            Assert.Equal("Doozies Doghouse", (await home.UpNextAsync()).Single().Label); // and the card on Home is its show's episode now
        }

        [Fact]
        public void The_show_store_gives_the_names_of_shows_found_by_a_search_by_their_provider_id()
        {
            var timers = new PlaybackControllerTests.FakeTimers();
            var store = new DiskShowStore(_dir, timers, runInBackground: a => a());
            StoredShow Show(string key, string name, TitleKind kind = TitleKind.Series) => new(
                new CatalogueTitle(key, kind, name, null, null, null, Array.Empty<TitleSource>()), Array.Empty<StoredSeason>(), DateTimeOffset.UnixEpoch);
            store.Put($"Series:doozies doghouse::{ShowId}", Show($"Series:doozies doghouse::{ShowId}", "Doozies Doghouse"));
            store.Put("Series:loki:2021:jf1", Show("Series:loki:2021:jf1", "Loki"));
            store.Put("tmdb:tv:7", Show("tmdb:tv:7", "Andor"));                                   // keyed by TMDB: no provider id
            store.Put("Film:casper:1995:film1", Show("Film:casper:1995:film1", "Casper", TitleKind.Film));
            timers.Advance(DiskShowStore.SaveDelay);

            var names = new DiskShowStore(_dir, timers).SeriesNamesById();
            Assert.Equal(new Dictionary<string, string> { [ShowId] = "Doozies Doghouse", ["jf1"] = "Loki" }, names);
        }
    }
}
