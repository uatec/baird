using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Baird.Models;
using Baird.NewUi;
using Baird.NewUi.Adapters;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Screens;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>
    /// What a page links to is warmed before the viewer gets there: one batch per page (its own data, its children, its siblings),
    /// newest batch first, one hop only, never stream resolution. Run through the real queue (gate open, no timers needed),
    /// a kept-record store in memory, and a recording image warmer.
    /// </summary>
    public class WarmerTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        private sealed class Gate : IWarmGate { public volatile WarmMode Value = WarmMode.Run; public WarmMode Mode => Value; }

        /// <summary>The queue never asks for a timer while its gate is open.</summary>
        private sealed class NoTimers : ITimers
        {
            public IDisposable After(TimeSpan delay, Action action) => new Noop();
            private sealed class Noop : IDisposable { public void Dispose() { } }
        }

        private sealed class Clock { public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); }

        private sealed class Rig : IDisposable
        {
            public readonly CatalogueTests.World W = CatalogueTests.Build();
            public readonly ShowDataStoreTests.MemStore Store = new();
            public readonly Clock Clock = new();
            public readonly PlaybackControllerTests.FakeTimers Timers;
            public readonly Gate Gate = new();
            public readonly WorkQueue Queue;
            public readonly ShowData Shows;
            public readonly Warmer Warmer;
            private readonly List<string> _warmed = new();
            private readonly List<string> _disk = new();
            private readonly List<(string Key, Rank Rank, bool Urgent)> _queued = new();
            public int SeasonCalls;
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Rig(PlaybackControllerTests.FakeTimers? timers = null)
            {
                Timers = timers ?? new();
                Queue = new(new NoTimers(), Gate);
                Shows = new ShowData(W.Data, W.Seerr, W.Search, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3), () => Clock.Now, Store) { RefreshSink = (_, _) => { } };
                W.Seerr.OnSeasonEntered = () => Interlocked.Increment(ref SeasonCalls);
                Warmer = new Warmer(Queue, Shows, Timers, url => { lock (_warmed) _warmed.Add(url); return Task.CompletedTask; },
                    url => { lock (_disk) _disk.Add(url); return Task.CompletedTask; });
                Warmer.Queued += (k, r, u) => { lock (_queued) _queued.Add((k, r, u)); };
            }

            /// <summary>Images fetched and decoded (ready to draw).</summary>
            public List<string> Warmed { get { lock (_warmed) return _warmed.ToList(); } }
            /// <summary>Images fetched to the disk cache only, never decoded.</summary>
            public List<string> DiskWarmed { get { lock (_disk) return _disk.ToList(); } }
            /// <summary>Provider searches (YouTube's is two yt-dlp processes) so far.</summary>
            public int Searches => W.Jellyfin.SearchCalls + W.IPlayer.SearchCalls;
            public List<(string Key, Rank Rank, bool Urgent)> Queued { get { lock (_queued) return _queued.ToList(); } }
            public List<string> Keys => Queued.Select(q => q.Key).ToList();

            /// <summary>Holds the queue's one worker, so what's submitted next queues up in the order it will run.</summary>
            public async Task Block()
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Queue.Submit(new[] { new WorkItem("test:blocker", Rank.Page, async ct => { started.TrySetResult(); await _release.Task.WaitAsync(ct); }) });
                await started.Task.WaitAsync(Wait);
            }

            public async Task Drain() { _release.TrySetResult(); await Queue.Idle.WaitAsync(Wait); }

            /// <summary>Network calls so far: Seerr status, Seerr lookups, Seerr seasons, library listings, library item lookups.</summary>
            public (int, int, int, int, int) Network() => (W.Seerr.StatusCalls, W.Seerr.SearchCalls, SeasonCalls,
                W.Jellyfin.ChildrenCalls + W.IPlayer.ChildrenCalls, W.Jellyfin.ItemCalls + W.IPlayer.ItemCalls);

            /// <summary>Keeps a fresh record for the title: art "http://art/{name}", episodes named, stills "http://img/{name}/s{n}e{m}".</summary>
            public void KeepFresh(CatalogueTitle t, params int[] episodesPerSeason)
            {
                var id = t.Name;
                Store.Put(t.Key, new StoredShow(t with { Sources = Array.Empty<TitleSource>(), ImageUrl = $"http://art/{id}" },
                    episodesPerSeason.Select((count, i) => new StoredSeason(i + 1, $"Season {i + 1}",
                        Enumerable.Range(1, count).Select(n => new StoredEpisode(n, $"{n}. {id} {i + 1}x{n}", null, $"http://img/{id}/s{i + 1}e{n}", "About it")).ToList(), count)).ToList(),
                    Clock.Now));
                Store.Puts.Clear();
            }

            public void Dispose() { _release.TrySetResult(); Queue.Dispose(); }
        }

        private static HomeCard Series(string id) => new(CardKind.Series, id, id, null, null, null, null, "Jellyfin: nas", PlayVia.Mpv);
        private static HomeCard FromSeerr(string id, CardKind kind = CardKind.Series) => new(kind, id, id, null, null, null, "get", "Seerr", PlayVia.None);
        private static HomeCard Episode(string id) => new(CardKind.Episode, id, id, "S1 E1", null, 0.4, null, "Jellyfin: nas", PlayVia.Mpv, $"http://stream/{id}", TimeSpan.FromMinutes(5));
        private static HomeCard Channel(string id) => new(CardKind.Channel, id, id, null, null, null, null, "TV", PlayVia.Mpv, $"http://tvh/{id}");

        private static async Task<CatalogueTitle> Find(CatalogueTests.World w, string q) =>
            (await w.Search.SearchAsync(q, CatalogueTests.Channels, DateTimeOffset.UtcNow)).Top!.Title!;

        private static readonly Regex SeasonOfUrl = new(@"s(\d+)e\d+$", RegexOptions.Compiled);

        /// <summary>The seasons the warmed stills belong to, in the order each was first warmed.</summary>
        private static List<int> SeasonsWarmed(IEnumerable<string> urls) =>
            urls.Select(u => SeasonOfUrl.Match(u)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();

        // ------------------------------------------------------------------ Home

        [Fact]
        public async Task Home_warms_continue_up_next_downloads_then_trending_in_that_order_and_skips_channels()
        {
            using var rig = new Rig();
            // Home's rows as it shows them, in order.
            var rows = new (string Row, HomeCard[] Cards)[]
            {
                ("Continue watching", new[] { Episode("resume-ep"), Series("cw-show"), FromSeerr("tmdb:tv:1") }), // a resume (plays), a show, then Up next's "get"
                ("On now", new[] { Channel("ch1"), Channel("ch4") }),
                ("Your watchlist", new[] { Series("wl-show") }),
                ("Downloads", new[] { FromSeerr("tmdb:tv:2"), Series("cw-show") }),          // the show again: warmed once, where it first appears
                ("Discover", new[] { FromSeerr("tmdb:movie:3", CardKind.Film) }),
                ("Popular on Netflix", new[] { FromSeerr("tmdb:tv:4") }),
            };
            var children = Warmer.TitlesOf(rows.SelectMany(r => r.Cards));
            Assert.Equal(new[] { "Series:cw-show", "tmdb:tv:1", "Series:wl-show", "tmdb:tv:2", "tmdb:movie:3", "tmdb:tv:4" }, children.Select(t => t.Key));
            foreach (var t in children) rig.KeepFresh(t, 2);

            await rig.Block();
            rig.Warmer.PageOpened("home", children, Array.Empty<CatalogueTitle>());
            await rig.Drain();

            Assert.Equal(children.Select(t => Warmer.KeyOf(t)), rig.Keys);
            Assert.All(rig.Queued, q => Assert.Equal((Rank.Child, false), (q.Rank, q.Urgent)));
            var arts = rig.DiskWarmed.Where(u => u.StartsWith("http://art/", StringComparison.Ordinal)).ToList();
            Assert.Equal(new[] { "cw-show", "tmdb:tv:1", "wl-show", "tmdb:tv:2", "tmdb:movie:3", "tmdb:tv:4" }.Select(n => $"http://art/{n}"), arts);
            Assert.DoesNotContain(rig.Warmed.Concat(rig.DiskWarmed), u => u.StartsWith("http://stream/", StringComparison.Ordinal) || u.StartsWith("http://tvh/", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ show page

        /// <summary>The Simpsons, seasons 1 and 2 watched up to the start of season 2; Seerr names seasons 3 and 4 (stills "http://still/s{n}e{m}").</summary>
        private static async Task<(Rig Rig, ShowDetails D)> PartlyWatchedSimpsons()
        {
            var rig = new Rig();
            foreach (var id in new[] { "s1e1", "s1e2" })
                rig.W.History.Add(new HistoryItem { Id = id, LastPosition = TimeSpan.FromMinutes(22), Duration = TimeSpan.FromMinutes(22), LastWatched = DateTime.Now, IsFinished = true });
            foreach (var n in new[] { 3, 4 })
                rig.W.Seerr.Seasons[(456, n)] = new JellyseerrSeasonDetails(n, new[] { new JellyseerrEpisode(1, $"Season {n} opener", "Opens it", $"http://still/s{n}e1") });
            var d = await rig.Shows.DetailsAsync(await Find(rig.W, "simpsons")); // the page loads it (and keeps it)
            Assert.Equal("Next S2 E1", d.Next!.Value.Label);
            return (rig, d);
        }

        [Fact]
        public async Task A_show_page_warms_the_next_episode_season_first_then_nearest_outward()
        {
            var (rig, d) = await PartlyWatchedSimpsons();
            using var cleanup = rig;
            Assert.Equal(new[] { 1, 2, 0, 3 }, Warmer.SeasonOrder(d)); // indexes: season 2, then 3 and 1 (later first on a tie), then 4

            await rig.Block();
            rig.Warmer.ShowPageOpened(d);
            await rig.Drain();

            Assert.Equal(new[] { Warmer.KeyOf(d.Title) }.Concat(new[] { 2, 3, 1, 4 }.Select(n => Warmer.KeyOf(d.Title, n))), rig.Keys);
            Assert.Equal(new[] { Rank.Page, Rank.Child, Rank.Child, Rank.Child, Rank.Child }, rig.Queued.Select(q => q.Rank));
            // The opening season and those either side are decoded, ready to draw; season 4 only reaches the disk.
            Assert.Equal(new[] { 2, 3, 1 }, SeasonsWarmed(rig.Warmed));
            Assert.Equal(new[] { 4 }, SeasonsWarmed(rig.DiskWarmed));
            Assert.Equal("http://img/simp", rig.Warmed[0]); // the page's backdrop leads
            // Seasons 3 and 4 were only numbered: they are named now, for next time.
            Assert.True(rig.Store.TryGet(d.Title.Key, out var kept));
            Assert.Equal("1. Season 3 opener", kept!.Seasons.Single(s => s.Number == 3).Episodes[0].Label);
        }

        [Fact]
        public async Task Only_one_hop_is_warmed()
        {
            using var rig = new Rig();
            var simpsons = await Find(rig.W, "simpsons");
            var severance = ShowData.FromCard(FromSeerr("tmdb:tv:95396"));
            await rig.Block();
            rig.Warmer.PageOpened("home", new[] { simpsons, severance }, Array.Empty<CatalogueTitle>());
            await rig.Drain();

            Assert.Equal(new[] { Warmer.KeyOf(simpsons), Warmer.KeyOf(severance) }, rig.Keys); // never their seasons
            Assert.Equal(2, rig.SeasonCalls); // only each page's own opening season (as the page itself asks): seasons 3 and 4 of The Simpsons stay unasked
            Assert.True(rig.Store.TryGet(simpsons.Key, out _));
            Assert.True(rig.Store.TryGet(severance.Key, out _));

            // A show page warms its seasons and its siblings, but not the siblings' seasons.
            var d = await rig.Shows.DetailsAsync(simpsons);
            rig.Warmer.From(new[] { severance, simpsons });
            var before = rig.Queued.Count;
            rig.Warmer.ShowPageOpened(d);
            await rig.Queue.Idle.WaitAsync(Wait);
            var keys = rig.Keys.Skip(before).ToList();
            Assert.Equal(Warmer.KeyOf(severance), keys[^1]);
            Assert.Equal(Rank.Sibling, rig.Queued[^1].Rank);
            Assert.DoesNotContain(keys, k => k.StartsWith(Warmer.KeyOf(severance) + "#", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Going_back_warms_forward_pages_before_pages_behind()
        {
            using var rig = new Rig();
            var behind = ShowData.FromCard(Series("behind"));
            var forward1 = ShowData.FromCard(Series("forward1"));
            var forward2 = ShowData.FromCard(Series("forward2"));
            foreach (var t in new[] { behind, forward1, forward2 }) rig.KeepFresh(t, 1);

            await rig.Block();
            rig.Warmer.PageOpened("search", new[] { behind }, Array.Empty<CatalogueTitle>()); // the page you were on
            rig.Warmer.PageOpened("home", new[] { forward1, forward2 }, Array.Empty<CatalogueTitle>()); // back to Home: a new batch
            await rig.Drain();

            Assert.Equal(new[] { "forward1", "forward2", "behind" }.Select(n => $"http://art/{n}"), rig.DiskWarmed.Where(u => u.StartsWith("http://art/", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task Lighting_an_unloaded_season_tab_is_urgent()
        {
            using var rig = new Rig();
            var severance = ShowData.FromCard(FromSeerr("tmdb:tv:95396"));
            rig.Store.Put(severance.Key, ShowDataStoreTests.Placeholders(severance, rig.Clock.Now, 9, 10)); // only numbered so far
            var others = new[] { ShowData.FromCard(Series("a")), ShowData.FromCard(Series("b")) };
            foreach (var t in others) rig.KeepFresh(t, 1);
            var d = rig.Shows.Stored(severance)!;
            Assert.True(ShowData.Unnamed(d.Seasons[0]));

            await rig.Block();
            rig.Warmer.PageOpened("home", others, Array.Empty<CatalogueTitle>());
            rig.Warmer.Season(d, 0);
            rig.Warmer.Season(d, 0); // drawn again: nothing more
            await rig.Drain();

            Assert.Contains((Warmer.KeyOf(severance, 1), Rank.Page, true), rig.Queued);
            Assert.Contains((Warmer.KeyOf(severance, 2), Rank.Child, false), rig.Queued);
            Assert.Equal(2, rig.Queued.Count(q => q.Key.StartsWith(Warmer.KeyOf(severance), StringComparison.Ordinal)));
            Assert.Equal("https://image.tmdb.org/t/p/w500/sev1.jpg", rig.Warmed[0]); // ahead of everything queued before it
            Assert.True(rig.Store.TryGet(severance.Key, out var kept));
            Assert.Equal("1. Good News About Hell", kept!.Seasons[0].Episodes[0].Label);

            // A season already named isn't urgent: only its neighbours are warmed.
            var (rig2, simpsons) = await PartlyWatchedSimpsons();
            using var cleanup2 = rig2;
            rig2.Warmer.Season(simpsons, 0);
            await rig2.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { (Warmer.KeyOf(simpsons.Title, 2), Rank.Child, false) }, rig2.Queued);
        }

        [Fact]
        public async Task No_stream_resolution_is_ever_queued()
        {
            var (rig, d) = await PartlyWatchedSimpsons();
            using var cleanup = rig;
            var cards = new[] { Episode("s2e1"), Channel("ch1"), CatalogueSearch.CardOf(d.Title), FromSeerr("tmdb:tv:95396"), CatalogueSearch.CardOf(await Find(rig.W, "alien")) };
            var network = rig.Network();

            rig.Warmer.Lit(cards[0]);
            rig.Warmer.Lit(cards[2]);
            rig.Timers.Advance(Warmer.Delay);
            rig.Warmer.PageOpened("home", Warmer.TitlesOf(cards), Array.Empty<CatalogueTitle>());
            rig.Warmer.From(Warmer.TitlesOf(cards));
            rig.Warmer.ShowPageOpened(d);
            rig.Warmer.Season(d, 3);
            await rig.Queue.Idle.WaitAsync(Wait);

            Assert.NotEmpty(rig.Queued);
            Assert.All(rig.Keys, k => Assert.True(k.StartsWith("show:", StringComparison.Ordinal) || k.StartsWith("img:", StringComparison.Ordinal), k));
            Assert.DoesNotContain(rig.Keys, k => k.Contains("s2e1", StringComparison.Ordinal) || k.Contains("ch1", StringComparison.Ordinal) || k.Contains("alien", StringComparison.Ordinal));
            Assert.DoesNotContain(rig.Warmed, u => u.Contains("stream", StringComparison.Ordinal) || u.StartsWith("http://tvh/", StringComparison.Ordinal));
            Assert.Equal(network.Item5, rig.Network().Item5); // no item lookups (what starts yt-dlp for a YouTube id)
        }

        [Fact]
        public async Task A_fresh_record_costs_no_network()
        {
            using var rig = new Rig();
            var severance = ShowData.FromCard(FromSeerr("tmdb:tv:95396"));
            var library = ShowData.FromCard(Series("lib"));
            rig.KeepFresh(severance, 2, 3);
            rig.KeepFresh(library, 4);
            var network = rig.Network();

            rig.Warmer.PageOpened("home", new[] { severance, library }, Array.Empty<CatalogueTitle>());
            rig.Warmer.ShowPageOpened(rig.Shows.Stored(severance)!);
            rig.Warmer.Season(rig.Shows.Stored(severance)!, 1);
            await rig.Queue.Idle.WaitAsync(Wait); // (the lit card asks for live availability: see A_lit_card_primes_the_page_it_opens)

            Assert.Equal(network, rig.Network());
            Assert.Equal(0, rig.Searches);
            Assert.Empty(rig.Store.Puts);
            Assert.Contains("http://art/lib", rig.DiskWarmed);
            Assert.Contains("http://img/tmdb:tv:95396/s2e3", rig.Warmed); // a season's stills, from the record
        }

        [Fact]
        public async Task No_more_than_a_dozen_images_per_title()
        {
            using var rig = new Rig();
            var big = ShowData.FromCard(Series("big"));
            rig.KeepFresh(big, 30);
            rig.Warmer.Lit(CatalogueSearch.CardOf(big));
            rig.Timers.Advance(Warmer.Delay);
            await rig.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(Warmer.MaxImages, rig.Warmed.Count);
        }

        [Fact]
        public async Task A_home_batch_warms_only_each_titles_backdrop()
        {
            using var rig = new Rig();
            var titles = new[] { "a", "b", "c", "d" }.Select(n => ShowData.FromCard(Series(n))).ToList();
            foreach (var t in titles) rig.KeepFresh(t, 3, 3); // each has stills
            await rig.Block();
            rig.Warmer.PageOpened("home", titles, Array.Empty<CatalogueTitle>());
            await rig.Drain();
            Assert.Equal(titles.Select(t => $"http://art/{t.Name}"), rig.DiskWarmed); // exactly one image each, no stills
            Assert.Empty(rig.Warmed);                                                 // and only to disk: nothing decoded
        }

        [Fact]
        public async Task A_home_batch_of_seerr_cards_never_searches_the_providers_nor_lists_a_youtube_channel()
        {
            using var rig = new Rig();
            // Trending on Home: Seerr cards with no source and nothing kept (a cold store), and a YouTube channel.
            var seerrCards = new[] { "tmdb:tv:95396", "tmdb:tv:101", "tmdb:movie:102", "tmdb:tv:103" }
                .Select(id => ShowData.FromCard(FromSeerr(id, id.Contains("movie") ? CardKind.Film : CardKind.Series))).ToList();
            var channel = new CatalogueTitle("Series:yt:channel/UC1", TitleKind.Series, "A channel", null, "http://yt/art", null,
                new[] { new TitleSource(YouTubeService.SourceName, "yt:channel/UC1", PlayVia.Mpv) });
            var children = rig.W.Jellyfin.ChildrenCalls + rig.W.IPlayer.ChildrenCalls;

            await rig.Block();
            rig.Warmer.PageOpened("home", seerrCards.Append(channel).ToList(), Array.Empty<CatalogueTitle>());
            await rig.Drain();

            Assert.Equal(0, rig.Searches);                                                   // no provider search (YouTube's is yt-dlp)
            Assert.Equal(children, rig.W.Jellyfin.ChildrenCalls + rig.W.IPlayer.ChildrenCalls); // no listing at all, the channel's included
            Assert.True(rig.Store.TryGet(seerrCards[0].Key, out var severance));             // Seerr's view of it is kept for next time
            Assert.Equal(new[] { 1, 2 }, severance!.Seasons.Select(s => s.Number));
            Assert.Equal("1. Good News About Hell", severance.Seasons[0].Episodes[0].Label);
            Assert.False(rig.Store.TryGet(channel.Key, out _));                              // the channel's page works it out when opened
            Assert.Contains("http://yt/art", rig.DiskWarmed);
            Assert.All(seerrCards, t => Assert.False(rig.Shows.IsKnown(t)));                 // never shared with a page: it does its own full compute
        }

        [Fact]
        public async Task A_new_search_supersedes_the_last_searchs_jobs()
        {
            using var rig = new Rig();
            var old = new[] { "old1", "old2" }.Select(n => ShowData.FromCard(Series(n))).ToList();
            var now = new[] { "new1" }.Select(n => ShowData.FromCard(Series(n))).ToList();
            foreach (var t in old.Concat(now)) rig.KeepFresh(t, 1);

            await rig.Block();
            rig.Warmer.PageOpened("search", old, Array.Empty<CatalogueTitle>());
            rig.Warmer.PageOpened("search", now, Array.Empty<CatalogueTitle>()); // typed on: new results
            await rig.Drain();

            Assert.Equal(new[] { "http://art/new1" }, rig.DiskWarmed); // the old results' jobs came up and did nothing
        }

        [Fact]
        public async Task A_lit_card_primes_the_page_it_opens()
        {
            using var rig = new Rig();
            var title = await Find(rig.W, "simpsons");
            rig.KeepFresh(title, 2, 1); // a fresh record: before, lighting it asked nobody, and the page opened on "–"
            var before = rig.Network();

            rig.Warmer.Lit(CatalogueSearch.CardOf(title));
            rig.Timers.Advance(Warmer.Delay);
            await rig.Queue.Idle.WaitAsync(Wait);
            var primed = rig.Network();
            Assert.True(primed.Item1 > before.Item1 && primed.Item4 > before.Item4); // Seerr's status and the library were asked

            var reports = new List<ShowDetails>();
            var d = await rig.Shows.DetailsAsync(title, x => { lock (reports) reports.Add(x); }); // the page opens
            Assert.Equal(primed, rig.Network());                       // joined: no second round of asking
            Assert.False(d.CheckingLibrary);
            Assert.Equal("Play S1 E1", d.Next!.Value.Label);           // Play at once
            Assert.All(reports, r => Assert.False(r.CheckingLibrary)); // never "–" first
        }

        [Fact]
        public async Task A_lit_card_waits_its_turn_while_frames_are_dropping()
        {
            using var rig = new Rig();
            var title = await Find(rig.W, "simpsons");
            rig.Gate.Value = WarmMode.Paused;
            rig.Warmer.Lit(CatalogueSearch.CardOf(title));
            rig.Timers.Advance(Warmer.Delay);
            Assert.Equal(new[] { (Warmer.KeyOf(title), Rank.Page, false) }, rig.Queued); // a batch item, held by the gate
            await Task.Delay(100);
            Assert.Empty(rig.Warmed);
            Assert.False(rig.Shows.IsKnown(title));

            var (rig2, d) = await PartlyWatchedSimpsons();             // the season tab stays urgent: the viewer is on the page
            using var cleanup2 = rig2;
            var severance = ShowData.FromCard(FromSeerr("tmdb:tv:95396"));
            rig2.Store.Put(severance.Key, ShowDataStoreTests.Placeholders(severance, rig2.Clock.Now, 9, 10));
            rig2.Gate.Value = WarmMode.Paused;
            rig2.Warmer.Season(rig2.Shows.Stored(severance)!, 0);
            Assert.Contains((Warmer.KeyOf(severance, 1), Rank.Page, true), rig2.Queued);
        }

        [Fact]
        public async Task A_show_pages_siblings_warm_only_their_backdrop_but_its_own_title_and_seasons_warm_stills()
        {
            var (rig, d) = await PartlyWatchedSimpsons();
            using var cleanup = rig;
            var other = ShowData.FromCard(Series("other"));
            rig.KeepFresh(other, 3);
            rig.Warmer.From(new[] { d.Title, other });
            rig.Warmer.ShowPageOpened(d);
            await rig.Queue.Idle.WaitAsync(Wait);
            Assert.Contains(rig.Warmed, u => u.StartsWith("http://still/s3", StringComparison.Ordinal)); // a season job: its stills
            Assert.Contains("http://art/other", rig.DiskWarmed);
            Assert.DoesNotContain(rig.Warmed.Concat(rig.DiskWarmed), u => u.StartsWith("http://img/other/", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ the lit card (from the old prefetcher)

        [Fact]
        public async Task A_lit_series_is_warmed_ahead_of_everything_after_a_moment()
        {
            using var rig = new Rig();
            var title = await Find(rig.W, "simpsons");
            rig.Warmer.Lit(CatalogueSearch.CardOf(title));
            Assert.Empty(rig.Queued); // not yet: it waits until the card has been lit a moment
            rig.Timers.Advance(Warmer.Delay);
            await rig.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { (Warmer.KeyOf(title), Rank.Page, true) }, rig.Queued);
            Assert.True(rig.Store.TryGet(title.Key, out _));
            Assert.Equal(new[] { title.ImageUrl!, "http://img/s1e1", "http://img/s1e2" }, rig.Warmed);
        }

        [Fact]
        public async Task Moving_on_quickly_warms_only_where_you_stop_and_films_that_play_need_nothing()
        {
            using var rig = new Rig();
            var title = await Find(rig.W, "simpsons");
            var severance = await Find(rig.W, "severance");
            var alien = (await rig.W.Search.SearchAsync("alien", CatalogueTests.Channels, DateTimeOffset.UtcNow)).Top!;
            rig.Warmer.Lit(CatalogueSearch.CardOf(title));
            rig.Timers.Advance(TimeSpan.FromMilliseconds(100));
            rig.Warmer.Lit(CatalogueSearch.CardOf(severance));
            rig.Timers.Advance(Warmer.Delay);
            await rig.Queue.Idle.WaitAsync(Wait);
            Assert.False(rig.Store.TryGet(title.Key, out _));
            Assert.True(rig.Store.TryGet(severance.Key, out _));
            Assert.Equal(new[] { Warmer.KeyOf(severance) }, rig.Keys);

            rig.Warmer.Lit(alien.Card);
            rig.Timers.Advance(Warmer.Delay);
            Assert.Single(rig.Queued);
            Assert.False(Warmer.OpensAPage(alien.Card)); // OK plays it: no page to prepare
            Assert.True(Warmer.OpensAPage(new HomeCard(CardKind.Film, "tmdb:movie:1", "X", null, null, null, "get", "Seerr", PlayVia.None)));
        }

        [Fact]
        public async Task Lighting_a_season_tab_warms_the_seasons_either_side()
        {
            using var rig = new Rig();
            var d = await rig.Shows.DetailsAsync(await Find(rig.W, "simpsons"));
            rig.Warmer.Season(d, 0);
            await rig.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "http://img/s2e1" }, rig.DiskWarmed); // the neighbour reaches the disk, not the image cache
            Assert.Empty(rig.Warmed);
        }

        // ------------------------------------------------------------------ the screens hand their pages to the warmer

        /// <summary>A screen context whose show data, search and warmer are the rig's (the warmer's dwell on the context's fake timers).</summary>
        private static (HomeScreenTests.Rig Screen, Rig Rig) Screens()
        {
            var screen = HomeScreenTests.Context();
            var rig = new Rig(screen.Timers);
            screen.Ctx.Search = rig.W.Search; screen.Ctx.Shows = rig.Shows; screen.Ctx.Channels = CatalogueTests.Channels; screen.Ctx.Timers = screen.Timers;
            screen.Ctx.Warm = rig.Warmer;
            return (screen, rig);
        }

        [AvaloniaFact]
        public async Task Home_warms_its_pages_once_it_settles_and_again_when_shown_again()
        {
            var (screen, rig) = Screens();
            using var cleanup = rig;
            var home = new HomeScreen(screen.Ctx);
            await home.LoadAsync();
            Dispatcher.UIThread.RunJobs();
            home.Build(new (string, string, CardShape, IReadOnlyList<HomeCard>)[]
            {
                ("Continue watching", "Continue watching", CardShape.Wide, new[] { Episode("resume-ep"), Series("cw-show") }),
                ("On now", "On now", CardShape.Wide, new[] { Channel("ch1") }),
                ("Downloads", "Downloads", CardShape.Poster, new[] { FromSeerr("tmdb:tv:2"), FromSeerr("tmdb:tv:3") }),
            });
            var expected = Warmer.TitlesOf(home.CurrentRows().SelectMany(r => r.Cards)).Select(t => Warmer.KeyOf(t)).ToList();
            Assert.Equal(new[] { "show:Series:cw-show", "show:tmdb:tv:2", "show:tmdb:tv:3" }, expected);

            screen.Timers.Advance(TimeSpan.FromMilliseconds(900));
            Assert.Empty(rig.Queued);
            home.OnAction(UiAction.Right, false); // a key: not settled yet
            screen.Timers.Advance(TimeSpan.FromMilliseconds(900));
            Assert.DoesNotContain(rig.Queued, q => !q.Urgent);
            screen.Timers.Advance(TimeSpan.FromMilliseconds(200));
            Assert.Equal(expected, rig.Queued.Where(q => !q.Urgent).Select(q => q.Key));

            home.OnAction(UiAction.Left, false);
            screen.Timers.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(3, rig.Queued.Count(q => !q.Urgent)); // the same Home, settled again: nothing new to ask

            home.OnShow(); // back on Home: a new batch, so what's forward of the viewer runs first
            screen.Timers.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(expected.Concat(expected), rig.Queued.Where(q => !q.Urgent).Select(q => q.Key));

            // OK on a card: the page it opens has the rest of its row as siblings.
            home.OnAction(UiAction.Down, false); home.OnAction(UiAction.Down, false); // Downloads, first card
            home.OnAction(UiAction.Ok, false);
            var opened = ShowData.FromCard(FromSeerr("tmdb:tv:2"));
            rig.KeepFresh(opened, 1);
            rig.Warmer.ShowPageOpened(rig.Shows.Stored(opened)!);
            Assert.Equal(("show:tmdb:tv:3", Rank.Sibling, false), rig.Queued[^1]);
            await rig.Queue.Idle.WaitAsync(Wait);
        }

        [AvaloniaFact]
        public async Task A_show_page_warms_its_seasons_once_it_has_loaded_and_its_season_tabs()
        {
            var (screen, rig) = Screens();
            using var cleanup = rig;
            var title = await Find(rig.W, "simpsons");
            var page = new ShowPage(screen.Ctx, title);
            await page.Loaded;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { Warmer.KeyOf(title) }.Concat(new[] { 1, 2, 3, 4 }.Select(n => Warmer.KeyOf(title, n))), rig.Keys);

            page.OnAction(UiAction.Down, false); // onto the season tabs: season 1's neighbours follow
            Assert.Equal(Warmer.KeyOf(title, 2), rig.Keys[^1]);
            await rig.Queue.Idle.WaitAsync(Wait);
        }

        [AvaloniaFact]
        public async Task Search_warms_its_result_pages_and_hands_the_lit_card_to_the_warmer()
        {
            var (screen, rig) = Screens();
            using var cleanup = rig;
            var s = new SearchScreen(screen.Ctx);
            s.SetQuery("simpsons");
            await s.RunAsync();
            Dispatcher.UIThread.RunJobs();
            var pages = Warmer.TitlesOf(s.ResultRows.SelectMany(r => r).Select(h => h.Card)).Select(t => Warmer.KeyOf(t)).ToList();
            Assert.NotEmpty(pages);
            Assert.Equal(pages, rig.Queued.Where(q => !q.Urgent).Select(q => q.Key));

            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Right, false);
            screen.Timers.Advance(Warmer.Delay);
            await rig.Queue.Idle.WaitAsync(Wait);
            Assert.Contains(rig.Queued, q => q.Urgent && q.Key == Warmer.KeyOf(s.ResultRows[0][0].Title!));
            Assert.True(rig.Store.TryGet(s.ResultRows[0][0].Title!.Key, out _));
        }

        // ------------------------------------------------------------------ what's learnt later reaches the page on screen

        /// <summary>Lets the dispatcher run what was posted to it (and the awaits behind it) until the page has settled.</summary>
        private static async Task Settle(ShowPage page)
        {
            for (var i = 0; i < 25; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await page.Loaded;
                await Task.Delay(10);
            }
            Dispatcher.UIThread.RunJobs();
        }

        [AvaloniaFact]
        public async Task A_page_that_settled_still_checking_asks_again_and_catches_up()
        {
            var (screen, rig) = Screens();
            using var cleanup = rig;
            var shows = new ShowData(rig.W.Data, rig.W.Seerr, rig.W.Search, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200), () => rig.Clock.Now, rig.Store) { RefreshSink = (_, _) => { } };
            screen.Ctx.Shows = shows;
            var severance = ShowData.FromCard(FromSeerr("tmdb:tv:95396"));
            rig.Store.Put(severance.Key, ShowDataStoreTests.Record(severance, rig.Clock.Now, 9, 10));
            rig.W.Seerr.StatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // Seerr slow, past the budget

            var page = new ShowPage(screen.Ctx, severance);
            await page.Loaded;
            await Settle(page);
            Assert.True(page.Details!.CheckingLibrary); // the budget ran out on the record: "–"

            rig.W.Seerr.StatusGate.SetResult();         // Seerr answers after all: the work, still running, completes
            for (var i = 0; i < 100 && rig.Store.PutCount == 0; i++) await Task.Delay(20);
            Assert.True(page.Details.CheckingLibrary);  // nothing on screen changes by itself...

            screen.Timers.Advance(ShowPage.RejoinAfter); // ...until the page asks again, and joins the finished work
            await Settle(page);
            Assert.False(page.Details!.CheckingLibrary);
            Assert.Contains("Request…", page.ButtonLabels);
            Destroy(page);
        }

        [AvaloniaFact]
        public async Task A_refreshed_answer_reaches_the_page_on_screen_once_without_looping()
        {
            var (screen, rig) = Screens();
            using var cleanup = rig;
            var refreshes = new List<Func<CancellationToken, Task>>();
            var shows = new ShowData(rig.W.Data, rig.W.Seerr, rig.W.Search, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3), () => rig.Clock.Now, rig.Store)
            { RefreshSink = (_, work) => { lock (refreshes) refreshes.Add(work); } };
            screen.Ctx.Shows = shows;
            var severance = ShowData.FromCard(FromSeerr("tmdb:tv:95396"));
            Assert.True(await shows.EnsureFreshFromSeerrAsync(severance)); // Seerr's status learnt a minute ago (by warming)
            rig.Clock.Now += TimeSpan.FromSeconds(61);

            var page = new ShowPage(screen.Ctx, severance);
            await Settle(page);
            Assert.Equal(SeasonState.Missing, page.Details!.Seasons[0].State); // the stale answer, served at once
            Assert.Equal(0, page.Reloads);                                     // its own load never makes it ask again
            Func<CancellationToken, Task> refresh;
            lock (refreshes) refresh = Assert.Single(refreshes);

            // Meanwhile season 1 was requested. The refresh runs (three times over: N pieces of news) and the page catches up once.
            rig.W.Seerr.Status[95396] = new JellyseerrMediaStatus(3, new[] { new JellyseerrSeason(1, 9, 3), new JellyseerrSeason(2, 10, 1) }, Array.Empty<JellyseerrDownload>());
            for (var i = 0; i < 3; i++) await refresh(CancellationToken.None);
            await Settle(page);

            Assert.Equal(SeasonState.Requested, page.Details!.Seasons[0].State);
            Assert.InRange(page.Reloads, 1, 2); // one at a time: the news that came in during it makes one more, not three
            var settled = page.Reloads;
            await Settle(page);
            Assert.Equal(settled, page.Reloads); // and then it stops
            Destroy(page);
        }

        [AvaloniaFact]
        public async Task A_season_tab_warmed_by_name_fills_in_on_the_page_on_screen()
        {
            var (screen, rig) = Screens();
            using var cleanup = rig;
            var severance = ShowData.FromCard(FromSeerr("tmdb:tv:95396"));
            var page = new ShowPage(screen.Ctx, severance);
            await Settle(page);
            Assert.Equal("1. Episode 1", page.Details!.Seasons[1].Episodes[0].Label); // season 2: only numbered so far
            var reloads = page.Reloads;

            rig.W.Seerr.Seasons[(95396, 2)] = new JellyseerrSeasonDetails(2, new[] { new JellyseerrEpisode(1, "Hello, Ms. Cobel", "Back again.", "http://still/sev2e1") });
            page.OnAction(UiAction.Down, false); page.OnAction(UiAction.Right, false); // onto season 2's tab: warmed at once
            await rig.Queue.Idle.WaitAsync(Wait);
            await Settle(page);

            Assert.Equal("1. Hello, Ms. Cobel", page.Details!.Seasons[1].Episodes[0].Label);
            Assert.Equal("http://still/sev2e1", page.Details.Seasons[1].Episodes[0].ImageUrl);
            Assert.Equal(reloads, page.Reloads); // filled from the store: nothing asked again
            Destroy(page);
        }

        private static void Destroy(ShowPage page) { page.Destroy(); Dispatcher.UIThread.RunJobs(); }

        // ------------------------------------------------------------------ what the page shares with the warming (from the old prefetcher)

        [Fact]
        public async Task The_page_uses_what_was_learnt_and_asks_again_only_when_told()
        {
            var w = CatalogueTests.Build();
            var title = await Find(w, "simpsons");
            var first = await w.Shows.DetailsAsync(title);
            Assert.Same(first, await w.Shows.DetailsAsync(title));
            Assert.NotSame(first, await w.Shows.DetailsAsync(title, fresh: true)); // after a request, or polling a download
        }

        [Fact]
        public async Task A_page_opened_while_a_warm_runs_joins_it_and_sees_what_is_known_so_far()
        {
            var (search, shows, jf, _) = CatalogueTests.Slow();
            var loki = ShowData.FromCard(new HomeCard(CardKind.Series, "tmdb:tv:84958", "Loki", "2021", null, null, null, "Seerr", PlayVia.None));
            using var warmCts = new CancellationTokenSource();
            var warm = shows.DetailsAsync(loki, null, warmCts.Token);
            await Task.Delay(150);
            var seen = new List<ShowDetails>();
            var page = shows.DetailsAsync(loki, d => { lock (seen) seen.Add(d); });
            lock (seen) Assert.True(seen.Single().CheckingLibrary); // at once: Seerr's view, still checking the library
            warmCts.Cancel(); // the warm moves on; the page still wants it, so it carries on
            jf.Gate.SetResult();
            var d = await page;
            Assert.Equal("Play S1 E1", d.Next!.Value.Label);
            Assert.Equal(1, jf.Searches);
        }

        [AvaloniaFact]
        public void Warmed_artwork_is_drawn_at_once_with_no_fade()
        {
            ImageCache.Clear();
            var bmp = new WriteableBitmap(new PixelSize(16, 9), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            ImageCache.Put("http://img/ready", bmp);
            var art = new ArtImage("http://img/ready");
            Assert.Same(bmp, art.Main.Source);
            Assert.Equal(1, art.Opacity);
        }
    }
}
