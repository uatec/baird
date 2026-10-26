using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Baird.Services;
using Baird.Tests.Mocks;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class HomeDataTests
    {
        private static MediaItemData Item(string id, string name, bool live = false, string? number = null, string source = "Jellyfin: x", string subtitle = "", MediaType type = MediaType.Video, string? stream = null) => new()
        {
            Id = id,
            Name = name,
            Details = "2021",
            ImageUrl = $"http://img/{id}",
            IsLive = live,
            StreamUrl = stream ?? $"http://stream/{id}",
            Source = source,
            ChannelNumber = number,
            Type = type,
            Synopsis = "s",
            Subtitle = subtitle,
            Duration = TimeSpan.FromMinutes(50),
        };

        internal sealed class History : IHistoryService
        {
            public readonly List<HistoryItem> Items = new();
            public readonly List<(string Id, TimeSpan Pos)> Upserts = new();
            public Task UpsertAsync(MediaItemViewModel media, TimeSpan position, TimeSpan duration) { Upserts.Add((media.Id, position)); return Task.CompletedTask; }
            public Task<List<HistoryItem>> GetHistoryAsync() => Task.FromResult(Items);
            public HistoryItem? GetProgress(string id) => Items.FirstOrDefault(i => i.Id == id);
        }
        private sealed class Watchlist : IWatchlistService
        {
            public event EventHandler? WatchlistUpdated;
            public Task AddAsync(string id) => Task.CompletedTask;
            public Task RemoveAsync(string id) => Task.CompletedTask;
            public Task<HashSet<string>> GetWatchlistIdsAsync() => Task.FromResult(new HashSet<string> { "film1" });
            public bool IsOnWatchlist(string id) => id == "film1";
            void Unused() => WatchlistUpdated?.Invoke(this, EventArgs.Empty);
        }
        private sealed class Failing : IMediaProvider
        {
            public string Name => "Broken";
            public Task<IEnumerable<MediaItemData>> GetListingAsync() => throw new InvalidOperationException("down");
            public Task<IEnumerable<MediaItemData>> SearchAsync(string q, CancellationToken ct = default) => throw new InvalidOperationException("down");
            public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => throw new InvalidOperationException("down");
            public Task<MediaItemData?> GetItemAsync(string id) => throw new InvalidOperationException("down");
        }
        private sealed class Seerr : IJellyseerrService
        {
            public Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string q, int p, CancellationToken ct = default) => Task.FromResult(Enumerable.Empty<JellyseerrSearchResult>());
            public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int p, CancellationToken ct = default) => Task.FromResult<IEnumerable<JellyseerrSearchResult>>(new[]
            {
                new JellyseerrSearchResult { Id = 7, MediaType = "tv", Title = "Andor", PosterPath = "/a.jpg", ReleaseDate = "2022-09-21", MediaInfoStatus = 1 },
                new JellyseerrSearchResult { Id = 8, MediaType = "movie", Title = "Alien", ReleaseDate = "1979-05-25", MediaInfoStatus = 5 },
            });
            public Task<JellyseerrRequestResponse> CreateRequestAsync(int id, string t, CancellationToken ct = default) => Task.FromResult(new JellyseerrRequestResponse());
            public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken ct = default) => Task.FromResult(Enumerable.Empty<JellyseerrRequest>());
        }

        internal static (HomeData Home, History History, MockEpgService Epg, IDataService Data) Build(bool withFailing = false)
        {
            var items = new[]
            {
                Item("ch1", "BBC One", live: true, number: "101", source: "TV"), Item("ch4", "Channel 4", live: true, number: "104", source: "TV"),
                Item("ch0", "Radio", live: true, number: "0", source: "TV"),
                Item("ep1", "Loki", subtitle: "S1E4 The Nexus Event"), Item("film1", "Casper"), Item("nf1", "Stranger Things", source: "Netflix"),
            };
            var providers = new List<IMediaProvider> { new Baird.Tests.Mocks.MockMediaProvider("P", items) };
            if (withFailing) providers.Insert(0, new Failing());
            var history = new History();
            history.Items.Add(new HistoryItem { Id = "ep1", LastPosition = TimeSpan.FromMinutes(10), Duration = TimeSpan.FromMinutes(50), LastWatched = DateTime.Now.AddHours(-1) });
            history.Items.Add(new HistoryItem { Id = "film1", LastPosition = TimeSpan.FromMinutes(40), Duration = TimeSpan.FromMinutes(100), LastWatched = DateTime.Now });
            history.Items.Add(new HistoryItem { Id = "ch1", LastPosition = TimeSpan.FromMinutes(5), Duration = TimeSpan.Zero, LastWatched = DateTime.Now });
            var data = new DataService(providers, history, new Watchlist(), new MediaItemCache(), new MockMediaDataCache());
            var epg = new MockEpgService();
            var now = DateTimeOffset.UtcNow;
            epg.MockSchedules["ch1"] = new List<EpgProgram> { new() { Id = "p1", ChannelId = "ch1", Title = "News at Six", StartUnix = now.AddMinutes(-15).ToUnixTimeSeconds(), StopUnix = now.AddMinutes(15).ToUnixTimeSeconds() } };
            return (new HomeData(data, epg, new Seerr()), history, epg, data);
        }

        [Fact]
        public async Task Up_next_is_unfinished_on_demand_items_most_recent_first_with_progress()
        {
            var (home, _, _, _) = Build();
            var cards = await home.UpNextAsync();
            Assert.Equal(new[] { "film1", "ep1" }, cards.Select(c => c.Id));
            Assert.Equal(CardKind.Film, cards[0].Kind);
            Assert.Equal(0.4, cards[0].Progress!.Value, 3);
            Assert.Equal(CardKind.Episode, cards[1].Kind);
            Assert.Equal(TimeSpan.FromMinutes(10), cards[1].Resume);
        }

        [Fact]
        public async Task On_now_lists_numbered_channels_in_order_with_the_current_programme_or_none()
        {
            var (home, _, _, _) = Build();
            var cards = await home.OnNowAsync(DateTimeOffset.UtcNow);
            Assert.Equal(new[] { "ch1", "ch4" }, cards.Select(c => c.Id));
            Assert.Equal("News at Six", cards[0].Sublabel);
            Assert.InRange(cards[0].Progress!.Value, 0.4, 0.6);
            Assert.Null(cards[1].Sublabel);
            Assert.Null(cards[1].Programme);
            Assert.Equal("101", cards[0].Channel!.Number);
        }

        [Fact]
        public async Task Home_loads_with_a_failing_provider()
        {
            var (home, _, _, _) = Build(withFailing: true);
            Assert.Equal(2, (await home.OnNowAsync(DateTimeOffset.UtcNow)).Count);
            Assert.Equal(2, (await home.UpNextAsync()).Count);
        }

        [Fact]
        public async Task Discover_maps_seerr_trending_with_a_get_badge_when_not_available()
        {
            var (home, _, _, _) = Build();
            var cards = await home.DiscoverAsync();
            Assert.Equal("Andor", cards[0].Label);
            Assert.Equal("get", cards[0].Badge);
            Assert.Equal("https://image.tmdb.org/t/p/w500/a.jpg", cards[0].ImageUrl);
            Assert.Null(cards[1].Badge);
        }

        [Fact]
        public void Netflix_and_disney_play_in_the_browser()
        {
            Assert.Equal(PlayVia.Browser, HomeData.ViaOf("Netflix"));
            Assert.Equal(PlayVia.Browser, HomeData.ViaOf("Disney+"));
            Assert.Equal(PlayVia.Browser, HomeData.ViaOf("ITVX"));
            Assert.Equal(PlayVia.Browser, HomeData.ViaOf("Channel 4"));
            Assert.Equal(PlayVia.Mpv, HomeData.ViaOf("Jellyfin: x"));
        }

        [Fact]
        public async Task History_writer_saves_through_the_legacy_service()
        {
            var (_, history, _, data) = Build();
            await new HistoryWriter(data).SaveAsync("ep1", TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(50));
            Assert.Contains(("ep1", TimeSpan.FromMinutes(12)), history.Upserts);
        }
    
        [Fact]
        public async Task Hiding_an_item_takes_it_off_up_next_until_it_is_watched_again()
        {
            var (_, history, epg, data) = Build();
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "baird-hidden-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var home = new HomeData(data, epg, null) { HiddenPath = path };
                Assert.Contains(await home.UpNextAsync(), c => c.Id == "film1");
                await home.HideFromUpNextAsync("film1");
                Assert.DoesNotContain(await home.UpNextAsync(), c => c.Id == "film1");
                Assert.DoesNotContain(await new HomeData(data, epg, null) { HiddenPath = path }.UpNextAsync(), c => c.Id == "film1"); // remembered
                history.Items.Single(h => h.Id == "film1").LastWatched = DateTime.Now.AddMinutes(5);           // watched again
                Assert.Contains(await home.UpNextAsync(), c => c.Id == "film1");
            }
            finally { System.IO.File.Delete(path); }
        }
}
}
