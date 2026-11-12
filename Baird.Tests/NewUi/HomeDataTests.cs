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
        internal sealed class Seerr : IJellyseerrService
        {
            public List<JellyseerrRequest> Requests = new();
            public Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string q, int p, CancellationToken ct = default) => Task.FromResult(Enumerable.Empty<JellyseerrSearchResult>());
            public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int p, CancellationToken ct = default) => Task.FromResult<IEnumerable<JellyseerrSearchResult>>(new[]
            {
                new JellyseerrSearchResult { Id = 7, MediaType = "tv", Title = "Andor", PosterPath = "/a.jpg", ReleaseDate = "2022-09-21", MediaInfoStatus = 1 },
                new JellyseerrSearchResult { Id = 8, MediaType = "movie", Title = "Alien", ReleaseDate = "1979-05-25", MediaInfoStatus = 5 },
            });
            public Task<JellyseerrRequestResponse> CreateRequestAsync(int id, string t, CancellationToken ct = default) => Task.FromResult(new JellyseerrRequestResponse());
            public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<JellyseerrRequest>>(Requests);
        }

        internal static (HomeData Home, History History, MockEpgService Epg, IDataService Data) Build(bool withFailing = false, Seerr? seerr = null)
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
            history.Items.Add(new HistoryItem { Id = "ch1", LastPosition = TimeSpan.FromMinutes(5), Duration = TimeSpan.Zero, LastWatched = DateTime.Now.AddMinutes(-30) });
            var data = new DataService(providers, history, new Watchlist(), new MediaItemCache(), new MockMediaDataCache());
            var epg = new MockEpgService();
            var now = DateTimeOffset.UtcNow;
            epg.MockSchedules["ch1"] = new List<EpgProgram> { new() { Id = "p1", ChannelId = "ch1", Title = "News at Six", StartUnix = now.AddMinutes(-15).ToUnixTimeSeconds(), StopUnix = now.AddMinutes(15).ToUnixTimeSeconds() } };
            return (new HomeData(data, epg, seerr ?? new Seerr()), history, epg, data);
        }

        [Fact]
        public async Task Continue_watching_is_channels_and_unfinished_videos_most_recent_first_with_progress_for_videos()
        {
            var (home, _, _, _) = Build();
            var cards = await home.UpNextAsync();
            Assert.Equal(new[] { "film1", "ch1", "ep1" }, cards.Select(c => c.Id));
            Assert.Equal(CardKind.Channel, cards[1].Kind);
            Assert.Null(cards[1].Progress);
            Assert.Equal("101", cards[1].Channel!.Number);
            Assert.Equal("News at Six", cards[1].Sublabel);
            Assert.Equal(CardKind.Film, cards[0].Kind);
            Assert.Equal(0.4, cards[0].Progress!.Value, 3);
            Assert.Equal(CardKind.Episode, cards[2].Kind);
            Assert.Equal(TimeSpan.FromMinutes(10), cards[2].Resume);
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
            Assert.Equal(3, (await home.UpNextAsync()).Count);
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

        private static JellyseerrRequest Req(int tmdb, string type, string title, DateTime created, int mediaStatus, double? progress = null, DateTime? updated = null) => new()
        {
            Id = tmdb * 10,
            Status = 2,
            TmdbId = tmdb,
            MediaType = type,
            Title = title,
            PosterPath = $"/{tmdb}.jpg",
            CreatedAt = created.ToString("o"),
            UpdatedAt = (updated ?? created).ToString("o"),
            MediaInfoStatus = mediaStatus,
            DownloadProgress = progress,
        };

        [Fact]
        public async Task Downloads_lists_requests_newest_first_one_card_per_title_with_where_each_has_got_to()
        {
            var now = DateTime.UtcNow;
            var seerr = new Seerr();
            var dune = Req(1, "movie", "Dune", now.AddDays(-3), mediaStatus: 3, progress: 42.4);
            var rivals = Req(2, "tv", "Rivals", now.AddDays(-1), mediaStatus: 4);
            rivals.Seasons = new[] { new JellyseerrSeason(1, 8, 5), new JellyseerrSeason(2, 8, 4), new JellyseerrSeason(3, 8, 1) };
            var future = Req(5, "movie", "Dune: Part Three", now.AddDays(-6), mediaStatus: 3);
            future.ReleaseDate = "2099-12-18";
            var stuck = Req(6, "movie", "Clayface", now.AddDays(-7), mediaStatus: 3);
            stuck.ReleaseDate = "2020-01-01";
            seerr.Requests.AddRange(new[]
            {
                dune, rivals,
                Req(2, "tv", "Rivals", now.AddDays(-5), mediaStatus: 4), // an older request for another season
                Req(4, "movie", "Arrival", now.AddDays(-4), mediaStatus: 5, updated: now.AddDays(-2)),
                future, stuck,
            });
            var (home, _, _, _) = Build(seerr: seerr);
            var cards = await home.DownloadsAsync();
            Assert.Equal(new[] { "Rivals", "Dune", "Arrival", "Dune: Part Three", "Clayface" }, cards.Select(c => c.Label));
            Assert.Equal(new[] { "partial", "dl:42", "ready", "out:18 Dec 2099", "searching" }, cards.Select(c => c.Badge));
            Assert.Equal("S1 ready · S2 partly", cards[0].Sublabel); // which seasons are here
            Assert.Equal(0.424, cards[1].Progress!.Value, 3); // a bar along the card
            Assert.Equal("tmdb:tv:2", cards[0].Id); // OK opens the show's page, like Discover
            Assert.Equal(CardKind.Series, cards[0].Kind);
            Assert.Equal(CardKind.Film, cards[1].Kind);
            Assert.Equal(4, cards[0].Title!.Seerr!.Status);
            Assert.Equal("https://image.tmdb.org/t/p/w500/2.jpg", cards[0].ImageUrl);
            Assert.StartsWith("Requested ", cards[1].Sublabel);
        }

        [Theory]
        [InlineData("1:5,2:5,3:5,4:3", "S1–3 ready · S4 coming")]
        [InlineData("1:4", "S1 partly")]
        [InlineData("1:1,2:2", "S2 coming")]
        [InlineData("1:5,2:1,3:5", "S1 ready · S3 ready")]
        public void Season_lines_say_which_seasons_are_here(string seasons, string expected)
        {
            var list = seasons.Split(',').Select(x => x.Split(':')).Select(x => new JellyseerrSeason(int.Parse(x[0]), 8, int.Parse(x[1]))).ToList();
            Assert.Equal(expected, HomeData.SeasonLine(list));
        }

        [Fact]
        public async Task Downloads_drops_what_arrived_more_than_two_weeks_ago()
        {
            var now = DateTime.UtcNow;
            var seerr = new Seerr();
            seerr.Requests.Add(Req(4, "movie", "Arrival", now.AddDays(-30), mediaStatus: 5, updated: now.AddDays(-20)));
            seerr.Requests.Add(Req(5, "movie", "Heat", now.AddDays(-30), mediaStatus: 3));
            var (home, _, _, _) = Build(seerr: seerr);
            Assert.Equal(new[] { "Heat" }, (await home.DownloadsAsync()).Select(c => c.Label));
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
