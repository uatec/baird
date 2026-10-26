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
    /// <summary>Search grouping and the show page's data (library + Seerr), with fakes for the providers and Seerr.</summary>
    public class CatalogueTests
    {
        internal static MediaItemData Item(string id, string name, string source = "Jellyfin: nas", MediaType type = MediaType.Video, string details = "", string subtitle = "",
            bool live = false, string? number = null, string? stream = null) => new()
            {
                Id = id,
                Name = name,
                Details = details,
                ImageUrl = $"http://img/{id}",
                IsLive = live,
                StreamUrl = type == MediaType.Brand ? "" : stream ?? $"http://stream/{id}",
                Source = source,
                ChannelNumber = number,
                Type = type,
                Synopsis = $"About {name}",
                Subtitle = subtitle,
                Duration = TimeSpan.FromMinutes(45),
            };

        /// <summary>A provider with children (seasons, episodes) and a name filter for search.</summary>
        internal sealed class Library : IMediaProvider
        {
            public string Name { get; }
            public readonly List<MediaItemData> Items = new();
            public readonly Dictionary<string, List<MediaItemData>> Children = new();
            public Library(string name) => Name = name;
            public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult<IEnumerable<MediaItemData>>(Items.Where(i => i.IsLive).ToList());
            public Task<IEnumerable<MediaItemData>> SearchAsync(string q, CancellationToken ct = default) =>
                Task.FromResult<IEnumerable<MediaItemData>>(Items.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (i.ChannelNumber?.StartsWith(q) ?? false)).ToList());
            public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => Task.FromResult<IEnumerable<MediaItemData>>(Children.TryGetValue(id, out var c) ? c : new List<MediaItemData>());
            public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult(Items.Concat(Children.Values.SelectMany(c => c)).FirstOrDefault(i => i.Id == id));
        }

        internal sealed class FakeSeerr : IJellyseerrService
        {
            public readonly List<JellyseerrSearchResult> Results = new();
            public readonly Dictionary<int, JellyseerrMediaStatus> Status = new();
            public readonly List<(int Id, string Type, IReadOnlyList<int>? Seasons)> Requests = new();
            public Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string q, int p, CancellationToken ct = default) =>
                Task.FromResult<IEnumerable<JellyseerrSearchResult>>(Results.Where(r => r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList());
            public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int p, CancellationToken ct = default) => Task.FromResult<IEnumerable<JellyseerrSearchResult>>(Results);
            public Task<JellyseerrRequestResponse> CreateRequestAsync(int id, string t, CancellationToken ct = default) => RequestSeasonsAsync(id, t, null, ct);
            public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken ct = default) => Task.FromResult(Enumerable.Empty<JellyseerrRequest>());
            public Task<JellyseerrMediaStatus?> GetMediaStatusAsync(int tmdbId, string mediaType, CancellationToken ct = default) =>
                Task.FromResult(Status.TryGetValue(tmdbId, out var s) ? s : null);
            public Task<JellyseerrRequestResponse> RequestSeasonsAsync(int id, string type, IReadOnlyList<int>? seasons, CancellationToken ct = default)
            {
                Requests.Add((id, type, seasons));
                return Task.FromResult(new JellyseerrRequestResponse { Success = true, RequestId = 1 });
            }
        }

        private sealed class NoHistory : IHistoryService
        {
            public readonly List<HistoryItem> Items = new();
            public Task UpsertAsync(MediaItemViewModel media, TimeSpan position, TimeSpan duration) => Task.CompletedTask;
            public Task<List<HistoryItem>> GetHistoryAsync() => Task.FromResult(Items);
            public HistoryItem? GetProgress(string id) => Items.FirstOrDefault(i => i.Id == id);
        }
        private sealed class NoWatchlist : IWatchlistService
        {
            public event EventHandler? WatchlistUpdated { add { } remove { } }
            public Task AddAsync(string id) => Task.CompletedTask;
            public Task RemoveAsync(string id) => Task.CompletedTask;
            public Task<HashSet<string>> GetWatchlistIdsAsync() => Task.FromResult(new HashSet<string>());
            public bool IsOnWatchlist(string id) => false;
        }

        internal sealed record World(CatalogueSearch Search, ShowData Shows, Library Jellyfin, Library IPlayer, FakeSeerr Seerr, MockEpgService Epg, IDataService Data, List<HistoryItem> History);

        /// <summary>
        /// The Simpsons: seasons 1–2 on Jellyfin (season 2 has 1 of its 3 episodes), also on iPlayer; Seerr knows 4 seasons,
        /// season 3 downloading (40%). Severance: only in Seerr. Alien: a film on Jellyfin and in Seerr. Channel 4 airs The Simpsons.
        /// </summary>
        internal static World Build()
        {
            var jf = new Library("Jellyfin");
            jf.Items.Add(Item("simp", "The Simpsons", type: MediaType.Brand, details: "1989"));
            jf.Items.Add(Item("alien", "Alien", details: "1979"));
            jf.Children["simp"] = new() { Item("simp-s1", "Season 1", type: MediaType.Brand), Item("simp-s2", "Season 2", type: MediaType.Brand) };
            jf.Children["simp-s1"] = new() { Item("s1e1", "Simpsons Roasting on an Open Fire"), Item("s1e2", "Bart the Genius") };
            jf.Children["simp-s2"] = new() { Item("s2e1", "Bart Gets an F") };
            var ip = new Library("BBC iPlayer");
            ip.Items.Add(Item("ip-simp", "The Simpsons", source: "BBC iPlayer", type: MediaType.Brand));
            var tv = new Library("TvHeadend");
            tv.Items.Add(Item("ch4", "Channel 4", source: "Live TV", live: true, number: "104", stream: "http://tvh/4"));
            tv.Items.Add(Item("ch1", "BBC One", source: "Live TV", live: true, number: "101", stream: "http://tvh/1"));

            var seerr = new FakeSeerr();
            seerr.Results.Add(new JellyseerrSearchResult { Id = 456, MediaType = "tv", Title = "The Simpsons", ReleaseDate = "1989-12-17", MediaInfoStatus = 4 });
            seerr.Results.Add(new JellyseerrSearchResult { Id = 95396, MediaType = "tv", Title = "Severance", ReleaseDate = "2022-02-17", MediaInfoStatus = 1, PosterPath = "/sev.jpg", Overview = "Work" });
            seerr.Results.Add(new JellyseerrSearchResult { Id = 348, MediaType = "movie", Title = "Alien", ReleaseDate = "1979-05-25", MediaInfoStatus = 5 });
            seerr.Results.Add(new JellyseerrSearchResult { Id = 999, MediaType = "movie", Title = "The Simpsons Movie", ReleaseDate = "2007-07-25", MediaInfoStatus = 3 });
            seerr.Status[456] = new JellyseerrMediaStatus(4,
                new[] { new JellyseerrSeason(1, 2, 5), new JellyseerrSeason(2, 3, 4), new JellyseerrSeason(3, 2, 3), new JellyseerrSeason(4, 2, 1) },
                new[] { new JellyseerrDownload(3, 1, 100, 60), new JellyseerrDownload(3, 2, 100, 60) });
            seerr.Status[95396] = new JellyseerrMediaStatus(1, new[] { new JellyseerrSeason(1, 9, 1), new JellyseerrSeason(2, 10, 1) }, Array.Empty<JellyseerrDownload>());

            var history = new NoHistory();
            var data = new DataService(new List<IMediaProvider> { tv, jf, ip }, history, new NoWatchlist(), new MediaItemCache(), new MockMediaDataCache());
            var epg = new MockEpgService();
            var now = DateTimeOffset.UtcNow;
            epg.MockSchedules["ch4"] = new List<EpgProgram>
            {
                new() { Id = "e1", ChannelId = "ch4", Title = "The Simpsons", StartUnix = now.AddMinutes(-10).ToUnixTimeSeconds(), StopUnix = now.AddMinutes(20).ToUnixTimeSeconds() },
                new() { Id = "e2", ChannelId = "ch4", Title = "Hollyoaks", StartUnix = now.AddMinutes(20).ToUnixTimeSeconds(), StopUnix = now.AddMinutes(50).ToUnixTimeSeconds() },
            };
            epg.MockSchedules["ch1"] = new List<EpgProgram>
            {
                new() { Id = "e3", ChannelId = "ch1", Title = "News", StartUnix = now.AddMinutes(-10).ToUnixTimeSeconds(), StopUnix = now.AddMinutes(20).ToUnixTimeSeconds() },
            };
            var search = new CatalogueSearch(data, epg, seerr);
            return new World(search, new ShowData(data, seerr, search), jf, ip, seerr, epg, data, history.Items);
        }

        internal static readonly IReadOnlyList<Channel> Channels = new[] { new Channel("ch1", "101", "BBC One", null, "http://tvh/1"), new Channel("ch4", "104", "Channel 4", null, "http://tvh/4") };

        [Fact]
        public async Task The_same_show_on_two_services_and_in_seerr_is_one_result_that_says_where()
        {
            var w = Build();
            var r = await w.Search.SearchAsync("simpsons", Channels, DateTimeOffset.UtcNow);
            var all = new[] { r.Top! }.Concat(r.Titles).Where(h => h.Kind == HitKind.Title).ToList();
            var simpsons = all.Where(h => h.Title!.Name == "The Simpsons").ToList();
            var t = Assert.Single(simpsons).Title!;
            Assert.Equal(new[] { "Jellyfin", "BBC iPlayer" }, t.Sources.Select(s => s.Service));
            Assert.Equal(456, t.Seerr!.TmdbId);
            Assert.Equal("On Jellyfin · also on BBC iPlayer", t.Where);
            Assert.Equal(Availability.Partial, t.Availability);
            Assert.Same(simpsons[0], r.Top); // the best match is the top result
        }

        [Fact]
        public async Task A_title_only_seerr_knows_is_a_result_you_can_request()
        {
            var w = Build();
            var r = await w.Search.SearchAsync("severance", Channels, DateTimeOffset.UtcNow);
            var t = r.Top!.Title!;
            Assert.Equal("Severance", t.Name);
            Assert.Empty(t.Sources);
            Assert.Equal(Availability.Get, t.Availability);
            Assert.Equal("get", r.Top.Card.Badge);
            Assert.Equal("https://image.tmdb.org/t/p/w500/sev.jpg", r.Top.Card.ImageUrl);
        }

        [Fact]
        public async Task A_film_in_the_library_and_seerr_is_one_playable_result()
        {
            var w = Build();
            var r = await w.Search.SearchAsync("alien", Channels, DateTimeOffset.UtcNow);
            var t = r.Top!.Title!;
            Assert.Equal(TitleKind.Film, t.Kind);
            Assert.Equal("alien", t.Best!.ItemId);
            Assert.Equal(348, t.Seerr!.TmdbId);
            Assert.Equal("http://stream/alien", r.Top.Card.StreamUrl);
            Assert.Empty(r.Titles);
        }

        [Fact]
        public async Task Search_finds_channels_by_name_and_number_and_what_is_on_the_guide()
        {
            var w = Build();
            var byNumber = await w.Search.SearchAsync("104", Channels, DateTimeOffset.UtcNow);
            Assert.Equal("ch4", byNumber.Top!.Card.Channel!.Id);
            Assert.Equal(HitKind.Channel, byNumber.Top.Kind);
            Assert.Equal("The Simpsons", byNumber.Top.Card.Programme!.Title); // channel results say what's on

            var simpsons = await w.Search.SearchAsync("simps", Channels, DateTimeOffset.UtcNow);
            var listing = Assert.Single(simpsons.OnTv);
            Assert.Equal("ch4", listing.Card.Channel!.Id);
            Assert.Equal("live", listing.Card.Badge);
            Assert.Contains("on now", listing.Card.Sublabel);
        }

        [Fact]
        public void Words_match_from_the_start_of_each_word()
        {
            Assert.True(CatalogueSearch.WordsMatch("doc wh", "Doctor Who"));
            Assert.True(CatalogueSearch.WordsMatch("simpsons", "The Simpsons"));
            Assert.False(CatalogueSearch.WordsMatch("octor", "Doctor Who"));
        }

        [Fact]
        public void Remakes_years_apart_stay_separate_titles()
        {
            var items = new[] { new MediaItemViewModel(Item("a", "Dune", details: "1984")), new MediaItemViewModel(Item("b", "Dune", details: "2021")) };
            var (titles, _) = CatalogueSearch.Group(items, Array.Empty<JellyseerrSearchResult>());
            Assert.Equal(2, titles.Count);
        }

        [Fact]
        public async Task A_failing_or_slow_source_does_not_hold_the_others_back()
        {
            var w = Build();
            var slow = new CatalogueSearch(w.Data, w.Epg, new SlowSeerr(), timeout: TimeSpan.FromMilliseconds(200));
            var r = await slow.SearchAsync("alien", Channels, DateTimeOffset.UtcNow);
            Assert.Equal("Alien", r.Top!.Title!.Name);
        }

        private sealed class SlowSeerr : IJellyseerrService
        {
            public async Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string q, int p, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); return Array.Empty<JellyseerrSearchResult>(); }
            public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int p, CancellationToken ct = default) => throw new InvalidOperationException();
            public Task<JellyseerrRequestResponse> CreateRequestAsync(int id, string t, CancellationToken ct = default) => throw new InvalidOperationException();
            public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        }

        // ------------------------------------------------------------------ the show page's data
        private static async Task<ShowDetails> Simpsons(World w)
        {
            var r = await w.Search.SearchAsync("simpsons", Channels, DateTimeOffset.UtcNow);
            return await w.Shows.DetailsAsync(r.Top!.Title!, fresh: true);
        }

        [Fact]
        public async Task Seasons_merge_the_library_with_seerr()
        {
            var d = await Simpsons(Build());
            Assert.Equal(new[] { 1, 2, 3, 4 }, d.Seasons.Select(s => s.Number));
            Assert.Equal(new[] { SeasonState.Available, SeasonState.Partial, SeasonState.Requested, SeasonState.Missing }, d.Seasons.Select(s => s.State));
            Assert.Equal(new[] { "Jellyfin", "1 of 3 on Jellyfin", "Downloading 40%", "Not streaming" }, d.Seasons.Select(s => s.StateLabel));
            Assert.Equal("Season 1 on Jellyfin · 1 of 3 episodes of season 2 on Jellyfin · Season 3 downloading · Season 4 can be requested", d.Summary);
        }


        [Fact]
        public async Task Episodes_seerr_counts_beyond_the_library_are_missing_or_downloading_with_a_percentage()
        {
            var d = await Simpsons(Build());
            var s2 = d.Seasons[1].Episodes;
            Assert.Equal(new[] { EpisodeState.Available, EpisodeState.Missing, EpisodeState.Missing }, s2.Select(e => e.State));
            Assert.Equal("1. Bart Gets an F", s2[0].Label);
            Assert.Equal("s2e1", s2[0].Source!.ItemId);
            var s3 = d.Seasons[2].Episodes;
            Assert.All(s3, e => Assert.Equal(EpisodeState.Requested, e.State));
            Assert.Equal(0.4, s3[0].Download!.Value, 3);
        }

        [Fact]
        public async Task The_primary_button_plays_the_first_episode_then_resumes_then_moves_on()
        {
            var w = Build();
            var d = await Simpsons(w);
            Assert.Equal("Play S1 E1", d.Next!.Value.Label);

            w.History.Add(new HistoryItem { Id = "s1e1", LastPosition = TimeSpan.FromMinutes(5), Duration = TimeSpan.FromMinutes(22), LastWatched = DateTime.Now });
            d = await Simpsons(w);
            Assert.Equal("Resume S1 E1", d.Next!.Value.Label);
            Assert.Equal(TimeSpan.FromMinutes(5), d.Next.Value.Episode.Source!.Resume);

            w.History[0] = new HistoryItem { Id = "s1e1", LastPosition = TimeSpan.FromMinutes(22), Duration = TimeSpan.FromMinutes(22), LastWatched = DateTime.Now, IsFinished = true };
            d = await Simpsons(w);
            Assert.Equal("Next S1 E2", d.Next!.Value.Label);
            Assert.True(d.Seasons[0].Episodes[0].Watched);
        }

        [Fact]
        public async Task A_seerr_card_finds_where_it_streams_and_a_library_card_finds_its_seerr_entry()
        {
            var w = Build();
            var fromDiscover = ShowData.FromCard(new HomeCard(CardKind.Film, "tmdb:movie:348", "Alien", "1979", null, null, null, "Seerr", PlayVia.None));
            var alien = await w.Shows.DetailsAsync(fromDiscover);
            Assert.Equal("alien", alien.Title.Best!.ItemId);
            Assert.Equal(SeasonState.Available, alien.FilmState);

            var fromWatchlist = ShowData.FromCard(new HomeCard(CardKind.Series, "simp", "The Simpsons", "1989", null, null, null, "Jellyfin: nas", PlayVia.Mpv));
            var simpsons = await w.Shows.DetailsAsync(fromWatchlist);
            Assert.Equal(456, simpsons.Title.Seerr!.TmdbId);
            Assert.Equal(4, simpsons.Seasons.Count);
        }

        [Fact]
        public async Task A_show_only_in_seerr_lists_its_seasons_to_request()
        {
            var w = Build();
            var r = await w.Search.SearchAsync("severance", Channels, DateTimeOffset.UtcNow);
            var d = await w.Shows.DetailsAsync(r.Top!.Title!);
            Assert.Equal(new[] { SeasonState.Missing, SeasonState.Missing }, d.Seasons.Select(s => s.State));
            Assert.Equal(9, d.Seasons[0].Episodes.Count);
            Assert.Null(d.Next);
            Assert.Equal("Not available to stream", d.Summary);
        }

        [Fact]
        public async Task Requests_ask_seerr_for_the_missing_seasons_a_season_or_the_film()
        {
            var w = Build();
            var d = await Simpsons(w);
            var (ok, message) = await w.Shows.RequestAsync(d, null);
            Assert.True(ok);
            Assert.Equal((456, "tv"), (w.Seerr.Requests[0].Id, w.Seerr.Requests[0].Type));
            Assert.Equal(new[] { 4 }, w.Seerr.Requests[0].Seasons);
            Assert.Contains("season 4", message);

            await w.Shows.RequestAsync(d, new[] { 4 });
            Assert.Equal(new[] { 4 }, w.Seerr.Requests[1].Seasons);

            var film = await w.Shows.DetailsAsync(ShowData.FromCard(new HomeCard(CardKind.Film, "tmdb:movie:999", "The Simpsons Movie", "2007", null, null, "get", "Seerr", PlayVia.None)));
            await w.Shows.RequestAsync(film, null);
            Assert.Equal((999, "movie", (IReadOnlyList<int>?)null), (w.Seerr.Requests[2].Id, w.Seerr.Requests[2].Type, w.Seerr.Requests[2].Seasons));
        }

        [Fact]
        public void Season_numbers_come_from_folder_names()
        {
            Assert.Equal(3, ShowData.SeasonNumberOf("Season 3", 1));
            Assert.Equal(2, ShowData.SeasonNumberOf("Series 2", 1));
            Assert.Equal(0, ShowData.SeasonNumberOf("Specials", 1));
            Assert.Equal(4, ShowData.SeasonNumberOf("Bonus", 4));
        }

        [Fact]
        public void Seerr_status_parses_seasons_their_states_and_downloads()
        {
            const string json = """
            { "name": "The Simpsons", "seasons": [ { "seasonNumber": 0, "episodeCount": 50 }, { "seasonNumber": 1, "episodeCount": 13 }, { "seasonNumber": 2, "episodeCount": 22 } ],
              "mediaInfo": { "status": 4, "seasons": [ { "seasonNumber": 1, "status": 5 }, { "seasonNumber": 2, "status": 3 } ],
                "downloadStatus": [ { "size": 1000, "sizeLeft": 250, "episode": { "seasonNumber": 2, "episodeNumber": 1 } }, { "size": 1000, "sizeLeft": 750, "episode": { "seasonNumber": 2, "episodeNumber": 2 } } ] } }
            """;
            var s = JellyseerrService.ParseMediaStatus(json);
            Assert.Equal(4, s.Status);
            Assert.Equal(new[] { new JellyseerrSeason(1, 13, 5), new JellyseerrSeason(2, 22, 3) }, s.Seasons);
            Assert.Equal(0.5, s.Progress(2)!.Value, 3);
            Assert.Equal(0.75, s.Progress(2, 1)!.Value, 3);
            Assert.Null(s.Progress(1));
        }

        // ------------------------------------------------------------------ progressive results
        /// <summary>A provider that answers only when released (Jellyfin 12's slow search), or never.</summary>
        internal sealed class Gated : IMediaProvider
        {
            public string Name { get; }
            private readonly Library _inner;
            public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Searches;
            public Gated(string name, Library inner) { Name = name; _inner = inner; }
            public Task<IEnumerable<MediaItemData>> GetListingAsync() => _inner.GetListingAsync();
            public async Task<IEnumerable<MediaItemData>> SearchAsync(string q, CancellationToken ct = default)
            {
                Interlocked.Increment(ref Searches);
                await Gate.Task.WaitAsync(ct);
                return await _inner.SearchAsync(q, ct);
            }
            public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => _inner.GetChildrenAsync(id);
            public Task<MediaItemData?> GetItemAsync(string id) => _inner.GetItemAsync(id);
        }

        /// <summary>Loki is on a slow Jellyfin (S1 with 2 episodes) and in Seerr; iPlayer answers at once.</summary>
        internal static (CatalogueSearch Search, ShowData Shows, Gated Jellyfin, FakeSeerr Seerr) Slow(TimeSpan? timeout = null)
        {
            var jf = new Library("Jellyfin");
            jf.Items.Add(Item("loki", "Loki", type: MediaType.Brand, details: "2021"));
            jf.Children["loki"] = new() { Item("loki-1", "Glorious Purpose"), Item("loki-2", "The Variant") };
            var ip = new Library("BBC iPlayer");
            ip.Items.Add(Item("ip-lok", "Loki Odyssey", source: "BBC iPlayer"));
            var gated = new Gated("Jellyfin", jf);
            var seerr = new FakeSeerr();
            seerr.Results.Add(new JellyseerrSearchResult { Id = 84958, MediaType = "tv", Title = "Loki", ReleaseDate = "2021-06-09", MediaInfoStatus = 5 });
            seerr.Status[84958] = new JellyseerrMediaStatus(5, new[] { new JellyseerrSeason(1, 2, 5) }, Array.Empty<JellyseerrDownload>());
            var data = new DataService(new List<IMediaProvider> { gated, ip }, new NoHistory(), new NoWatchlist(), new MediaItemCache(), new MockMediaDataCache());
            var search = new CatalogueSearch(data, new MockEpgService(), seerr, timeout: timeout);
            return (search, new ShowData(data, seerr, search), gated, seerr);
        }

        private static IEnumerable<SearchHit> All(SearchResults r) => (r.Top == null ? Array.Empty<SearchHit>() : new[] { r.Top }).Concat(r.Titles);

        [Fact]
        public async Task Results_arrive_source_by_source_and_say_checking_your_library_until_it_has_answered()
        {
            var (search, _, jf, _) = Slow();
            var updates = new List<SearchResults>();
            var run = search.SearchAsync("loki", Channels, DateTimeOffset.UtcNow, r => { lock (updates) updates.Add(r); });
            await Task.Delay(150);
            SearchResults early;
            lock (updates) early = updates.Last();
            Assert.Contains("Jellyfin", early.Pending);
            var loki = All(early).Single(h => h.Title?.Name == "Loki");
            Assert.EndsWith("Checking your library…", loki.Card.Sublabel);
            Assert.Null(loki.Card.Badge); // never "not in your library" while the library is still answering
            Assert.Contains(All(early), h => h.Title?.Name == "Loki Odyssey"); // the fast source is already in

            jf.Gate.SetResult();
            var final = await run;
            Assert.Empty(final.Pending);
            var t = All(final).Single(h => h.Title?.Name == "Loki").Title!;
            Assert.Equal("loki", t.Best!.ItemId);
            Assert.Equal(84958, t.Seerr!.TmdbId);
            lock (updates) Assert.Same(final, updates.Last());
        }

        [Fact]
        public async Task A_source_that_never_answers_is_given_up_on_after_its_timeout()
        {
            var (search, _, _, _) = Slow(TimeSpan.FromMilliseconds(300));
            var final = await search.SearchAsync("loki", Channels, DateTimeOffset.UtcNow);
            Assert.Empty(final.Pending);
            var loki = All(final).Single(h => h.Title?.Name == "Loki");
            Assert.Equal(Availability.Library, loki.Title!.Availability);
        }

        [Fact]
        public async Task The_show_page_shows_seerr_at_once_and_its_library_episodes_when_the_library_answers()
        {
            var (_, shows, jf, _) = Slow();
            var title = ShowData.FromCard(new HomeCard(CardKind.Series, "tmdb:tv:84958", "Loki", "2021", null, null, null, "Seerr", PlayVia.None));
            var seen = new List<ShowDetails>();
            var run = shows.DetailsAsync(title, d => { lock (seen) seen.Add(d); });
            await Task.Delay(150);
            ShowDetails first;
            lock (seen) first = seen.Last();
            Assert.True(first.CheckingLibrary);
            Assert.Equal("Checking your library…", first.Summary);
            jf.Gate.SetResult();
            var d = await run;
            Assert.False(d.CheckingLibrary);
            Assert.Equal("loki-1", d.Seasons[0].Episodes[0].Source!.ItemId);
            Assert.Equal("Play S1 E1", d.Next!.Value.Label);
        }

        [Fact]
        public async Task The_show_page_reuses_a_search_still_running_for_the_same_name()
        {
            var (search, shows, jf, _) = Slow();
            var running = search.SearchAsync("loki", Channels, DateTimeOffset.UtcNow);
            var details = shows.DetailsAsync(ShowData.FromCard(new HomeCard(CardKind.Series, "tmdb:tv:84958", "Loki", "2021", null, null, null, "Seerr", PlayVia.None)));
            await Task.Delay(100);
            jf.Gate.SetResult();
            await running; await details;
            Assert.Equal(1, jf.Searches);
        }

        [Fact]
        public async Task Browser_services_titles_carry_how_they_play()
        {
            var nf = new Library("Netflix");
            nf.Items.Add(Item("nf-1", "Wednesday", source: "Netflix", stream: "https://www.netflix.com/watch/1"));
            var data = new DataService(new List<IMediaProvider> { nf }, new NoHistory(), new NoWatchlist(), new MediaItemCache(), new MockMediaDataCache());
            var r = await new CatalogueSearch(data, new MockEpgService(), null).SearchAsync("wednesday", Channels, DateTimeOffset.UtcNow);
            Assert.Equal(PlayVia.Browser, r.Top!.Card.Via);
            Assert.Equal("https://www.netflix.com/watch/1", r.Top.Card.StreamUrl);
        }
    }
}
