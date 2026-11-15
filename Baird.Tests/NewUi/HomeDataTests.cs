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
            public List<JellyseerrSearchResult> Movies = new(), Tv = new();
            public bool MoviesThrow;
            public int Calls;
            public Task<IEnumerable<JellyseerrSearchResult>> GetProviderTrendingAsync(int providerId, string mediaType, CancellationToken ct = default)
            {
                Interlocked.Increment(ref Calls);
                if (mediaType == "movie" && MoviesThrow) throw new InvalidOperationException("down");
                return Task.FromResult<IEnumerable<JellyseerrSearchResult>>(mediaType == "movie" ? Movies : Tv);
            }
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
        public async Task A_finished_episode_is_replaced_in_continue_watching_by_the_one_that_follows()
        {
            var (home, history, _, _) = Build();
            history.Items.Add(new HistoryItem { Id = "ep1", LastPosition = TimeSpan.FromMinutes(50), Duration = TimeSpan.FromMinutes(50), LastWatched = DateTime.Now, IsFinished = true });
            history.Items.RemoveAll(h => h.Id == "ep1" && !h.IsFinished);
            var asked = new List<string>();
            home.FindNextAfter = (m, ct) =>
            {
                asked.Add(m.Id);
                return Task.FromResult<HomeCard?>(new HomeCard(CardKind.Episode, "ep2", "Loki 5", "S1 E5", null, null, null, "P", PlayVia.Mpv, "http://stream/ep2"));
            };
            var cards = await home.UpNextAsync();
            Assert.Equal("ep2", cards[0].Id);
            Assert.Contains("ep1", asked);
            Assert.DoesNotContain(cards, c => c.Id == "ep1");
        }

        [Fact]
        public async Task A_lookup_that_fails_leaves_continue_watching_as_it_was()
        {
            var (home, history, _, _) = Build();
            history.Items.Add(new HistoryItem { Id = "ep1", LastPosition = TimeSpan.FromMinutes(50), Duration = TimeSpan.FromMinutes(50), LastWatched = DateTime.Now, IsFinished = true });
            home.FindNextAfter = (m, ct) => throw new InvalidOperationException("down");
            Assert.Contains(await home.UpNextAsync(), c => c.Id == "film1");
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
            Assert.Equal(new[] { "part", "dl:42", "ready", "out:18 Dec 2099", "notfound" }, cards.Select(c => c.Badge)); // Clayface: out for years, asked a week ago, still nothing
            Assert.Equal("S1 ready · S2 partly", cards[0].Sublabel); // which seasons are here
            Assert.Equal(0.424, cards[1].Progress!.Value, 3); // a bar along the card
            Assert.Equal("tmdb:tv:2", cards[0].Id); // OK opens the show's page, like Discover
            Assert.Equal(CardKind.Series, cards[0].Kind);
            Assert.Equal(CardKind.Film, cards[1].Kind);
            Assert.Equal(4, cards[0].Title!.Seerr!.Status);
            Assert.Equal("https://image.tmdb.org/t/p/w500/2.jpg", cards[0].ImageUrl);
            Assert.Equal("Downloading", cards[1].Sublabel);
        }

        // ---- what a Downloads tile says: a pill for the state and a line for which part ----
        private static JellyseerrSeason[] Seasons(string statuses) =>
            statuses.Split(',').Select((st, i) => new JellyseerrSeason(i + 1, 8, int.Parse(st))).ToArray();

        private static (string Badge, string Scope) Say(string type, string statuses, int[]? requested, int requestStatus = 2, int mediaStatus = 3,
            double? progress = null, int downloadingEpisodes = 0, int downloadingSeason = 2, int askedDaysAgo = 1, string? release = null)
        {
            var r = Req(1, type, "X", DateTime.UtcNow.AddDays(-askedDaysAgo), mediaStatus, progress);
            r.Status = requestStatus;
            r.ReleaseDate = release;
            if (statuses.Length > 0) r.Seasons = Seasons(statuses);
            r.Downloads = Enumerable.Range(1, downloadingEpisodes).Select(e => new JellyseerrDownload(downloadingSeason, e, 100, 50)).ToList();
            var v = HomeData.Describe(r, requested ?? Array.Empty<int>(), DateTime.UtcNow);
            return (v.Badge, v.Scope.Replace(DateTime.UtcNow.AddDays(-askedDaysAgo).ToLocalTime().ToString("d MMM"), "<date>"));
        }

        [Fact]
        public void A_request_waiting_for_approval_says_requested_and_when()
        {
            var (badge, scope) = Say("tv", "1,1,1,1,1", new[] { 1, 2 }, requestStatus: 1, mediaStatus: 2);
            Assert.Equal("requested", badge);
            Assert.Equal("S1–2 · asked <date>", scope);
        }

        [Fact]
        public void An_approved_request_with_nothing_downloading_says_searching()
        {
            var (badge, scope) = Say("tv", "2,2,1,1", new[] { 1, 2 });
            Assert.Equal(("searching", "S1–2 · looking for it"), (badge, scope));
        }

        [Fact]
        public void An_approved_request_nobody_could_find_for_days_says_not_found()
        {
            var (badge, scope) = Say("tv", "3,1", new[] { 1 }, askedDaysAgo: 5, release: "2020-01-01");
            Assert.Equal(("notfound", "S1 · not found yet"), (badge, scope));
        }

        [Fact]
        public void A_series_not_out_yet_says_when()
        {
            var (badge, scope) = Say("tv", "3", new[] { 1 }, release: "2099-12-18");
            Assert.Equal(("out:18 Dec 2099", "S1 · not out yet"), (badge, scope));
        }

        [Fact]
        public void Downloading_shows_the_percentage_and_the_episodes_still_to_come()
        {
            var (badge, scope) = Say("tv", "5,3,1", new[] { 1, 2 }, mediaStatus: 4, progress: 42, downloadingEpisodes: 6);
            Assert.Equal("part", badge); // S1 is here already
            Assert.Equal("S1 ready · S2 6 to go", scope);
            var (b2, s2) = Say("tv", "3,1", new[] { 1 }, progress: 42, downloadingEpisodes: 6, downloadingSeason: 1);
            Assert.Equal(("dl:42", "S1 · 6 episodes to go"), (b2, s2));
            Assert.Equal("S1 · 1 episode to go", Say("tv", "3", new[] { 1 }, progress: 90, downloadingEpisodes: 1, downloadingSeason: 1).Scope);
        }

        [Fact]
        public void Some_of_what_you_asked_for_here_and_the_rest_coming_says_part_ready()
        {
            var (badge, scope) = Say("tv", "5,3,3,1", new[] { 1, 2, 3 }, mediaStatus: 4);
            Assert.Equal(("part", "S1 ready · S2–3 coming"), (badge, scope));
        }

        [Fact]
        public void Everything_you_asked_for_is_here_but_not_the_whole_show_says_ready_and_how_much()
        {
            var (badge, scope) = Say("tv", "5,5,5,1,1", new[] { 1, 2, 3 }, mediaStatus: 4);
            Assert.Equal(("ready", "S1–3 of 5 seasons"), (badge, scope));
        }

        [Fact]
        public void The_whole_show_here_says_complete()
        {
            Assert.Equal(("complete", "All 5 seasons"), Say("tv", "5,5,5,5,5", new[] { 1, 2, 3, 4, 5 }, mediaStatus: 5));
            Assert.Equal(("complete", "The whole series"), Say("tv", "5", new[] { 1 }, mediaStatus: 5));
        }

        [Fact]
        public void A_season_with_only_some_episodes_and_nothing_downloading_says_partly_here()
        {
            Assert.Equal(("part", "S1 ready · S2 partly"), Say("tv", "5,4,1", new[] { 1, 2 }, mediaStatus: 4));
            Assert.Equal(("part", "S2 partly"), Say("tv", "1,4", new[] { 2 }, mediaStatus: 4));
        }

        [Fact]
        public void A_season_deleted_from_the_library_is_no_longer_asked_for()
        {
            // Seerr marks deleted (7) and blocklisted (6) seasons; they are neither here nor on the way.
            Assert.Equal(("ready", "S1 of 3 seasons"), Say("tv", "5,7,1", new[] { 1, 2 }, mediaStatus: 4));
        }

        [Fact]
        public async Task A_title_deleted_from_the_server_is_not_listed_in_downloads()
        {
            var seerr = new Seerr();
            var gone = Req(5, "movie", "Fallout", DateTime.UtcNow.AddDays(-2), mediaStatus: 7);
            seerr.Requests.AddRange(new[] { gone, Req(6, "movie", "Heat", DateTime.UtcNow.AddDays(-1), mediaStatus: 3) });
            var (home, _, _, _) = Build(seerr: seerr);
            Assert.Equal(new[] { "Heat" }, (await home.DownloadsAsync()).Select(c => c.Label));
        }

        [Fact]
        public void A_declined_request_says_so()
        {
            Assert.Equal(("declined", "S1 · declined"), Say("tv", "1", new[] { 1 }, requestStatus: 3, mediaStatus: 1));
        }

        [Fact]
        public void When_Seerr_does_not_say_which_seasons_were_asked_for_the_ones_it_knows_of_stand_in()
        {
            // seasons with a status above "unknown" are the ones that were requested (or arrived)
            Assert.Equal(("ready", "S1–2 of 3 seasons"), Say("tv", "5,5,1", null, mediaStatus: 4));
        }

        [Fact]
        public void Films_say_requested_searching_a_percentage_or_ready()
        {
            Assert.Equal(("requested", "Asked <date>"), Say("movie", "", null, requestStatus: 1, mediaStatus: 2));
            Assert.Equal(("searching", "Looking for it"), Say("movie", "", null));
            Assert.Equal(("dl:42", "Downloading"), Say("movie", "", null, progress: 42));
            Assert.Equal(("ready", "Film"), Say("movie", "", null, mediaStatus: 5));
            Assert.Equal(("out:18 Dec 2099", "Not released yet"), Say("movie", "", null, release: "2099-12-18"));
        }

        [Fact]
        public async Task A_title_requested_in_pieces_shows_every_season_asked_for_across_its_requests()
        {
            var now = DateTime.UtcNow;
            var seerr = new Seerr();
            var newer = Req(7, "tv", "Silo", now.AddDays(-1), mediaStatus: 3); newer.RequestedSeasons = new[] { 2 };
            var older = Req(7, "tv", "Silo", now.AddDays(-9), mediaStatus: 3); older.RequestedSeasons = new[] { 1 };
            newer.Seasons = older.Seasons = Seasons("3,3,1");
            seerr.Requests.AddRange(new[] { newer, older });
            var (home, _, _, _) = Build(seerr: seerr);
            var card = Assert.Single(await home.DownloadsAsync());
            Assert.Contains("S1–2", card.Sublabel);
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
