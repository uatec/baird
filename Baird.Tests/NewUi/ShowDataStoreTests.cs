using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>A show page opens from the metadata kept between runs, then refreshes it from the live answers.</summary>
    public class ShowDataStoreTests
    {
        private sealed class Clock { public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); }

        /// <summary>An in-memory store that remembers everything it was handed.</summary>
        internal sealed class MemStore : IShowStore
        {
            public readonly Dictionary<string, StoredShow> Shows = new();
            public readonly List<(string Key, StoredShow Show)> Puts = new();
            public bool TryGet(string key, out StoredShow? show)
            {
                lock (Shows) { var found = Shows.TryGetValue(key, out var s); show = s; return found; }
            }
            public void Put(string key, StoredShow show) { lock (Shows) { Puts.Add((key, show)); Shows[key] = show; } }
            public int PutCount { get { lock (Shows) return Puts.Count; } }
        }

        private sealed class Counter { public int Value; }

        private sealed record Rig(CatalogueTests.World W, ShowData Shows, MemStore Store, Clock Clock, Counter SeasonCalls);

        private static Rig Setup(TimeSpan? budget = null, TimeSpan? piece = null)
        {
            var w = CatalogueTests.Build();
            var clock = new Clock();
            var store = new MemStore();
            var shows = new ShowData(w.Data, w.Seerr, w.Search, piece ?? TimeSpan.FromMilliseconds(200), budget ?? TimeSpan.FromSeconds(3), () => clock.Now, store);
            shows.RefreshSink = (_, _) => { }; // stale availability isn't refreshed here
            var seasons = new Counter();
            w.Seerr.OnSeasonEntered = () => Interlocked.Increment(ref seasons.Value);
            return new Rig(w, shows, store, clock, seasons);
        }

        private static CatalogueTitle Severance() =>
            ShowData.FromCard(new HomeCard(CardKind.Series, "tmdb:tv:95396", "Severance", "2022", null, null, "get", "Seerr", PlayVia.None));

        private static async Task<CatalogueTitle> Find(CatalogueTests.World w, string q) =>
            (await w.Search.SearchAsync(q, CatalogueTests.Channels, DateTimeOffset.UtcNow)).Top!.Title!;

        /// <summary>A kept record: every episode named "n. Stored SxEn", art and synopsis, availability unknown.</summary>
        internal static StoredShow Record(CatalogueTitle t, DateTimeOffset stamp, params int[] episodesPerSeason) =>
            new(t with { Sources = Array.Empty<TitleSource>(), Seerr = t.Seerr is { } r ? r with { Status = 0 } : null, ImageUrl = "http://img/stored", Synopsis = "Stored synopsis" },
                episodesPerSeason.Select((count, i) => new StoredSeason(i + 1, $"Season {i + 1}",
                    Enumerable.Range(1, count).Select(n => new StoredEpisode(n, $"{n}. Stored S{i + 1}E{n}", null, $"http://img/s{i + 1}e{n}", $"Synopsis S{i + 1}E{n}")).ToList(), count)).ToList(),
                stamp);

        private static void Keep(Rig rig, CatalogueTitle t, StoredShow s) { lock (rig.Store.Shows) rig.Store.Shows[t.Key] = s; }

        [Fact]
        public async Task A_stored_title_is_reported_before_any_network_answers()
        {
            var rig = Setup(budget: TimeSpan.FromMilliseconds(300), piece: TimeSpan.FromSeconds(10));
            var title = Severance();
            Keep(rig, title, Record(title, rig.Clock.Now, 9, 10));
            rig.W.Seerr.HangStatus = true; rig.W.Seerr.HangSeason = true;
            rig.W.Jellyfin.HangSearch = true; rig.W.IPlayer.HangSearch = true;

            var reports = new List<ShowDetails>();
            var task = rig.Shows.DetailsAsync(title, d => { lock (reports) reports.Add(d); });
            ShowDetails first;
            lock (reports) { Assert.Single(reports); first = reports[0]; } // reported synchronously, before anything answered

            Assert.True(first.CheckingLibrary);
            Assert.Equal(new[] { 9, 10 }, first.Seasons.Select(s => s.Episodes.Count));
            Assert.Equal("4. Stored S2E4", first.Seasons[1].Episodes[3].Label);
            Assert.Equal("Synopsis S1E2", first.Seasons[0].Episodes[1].Synopsis);
            Assert.Equal("http://img/s1e3", first.Seasons[0].Episodes[2].ImageUrl);
            Assert.Equal(ShowDetails.Checking, first.Summary);
            Assert.Equal(0, rig.SeasonCalls.Value);
            await task;
        }

        [Fact]
        public async Task A_stored_title_older_than_7_days_is_still_served_and_refreshed()
        {
            var rig = Setup();
            var title = Severance();
            var old = Record(title, rig.Clock.Now - TimeSpan.FromDays(8), 9, 10);
            Keep(rig, title, old);
            rig.Clock.Now += TimeSpan.FromMinutes(1);

            var reports = new List<ShowDetails>();
            var d = await rig.Shows.DetailsAsync(title, x => { lock (reports) reports.Add(x); });
            Assert.Equal("1. Stored S1E1", reports[0].Seasons[0].Episodes[0].Label); // served first, though old

            Assert.Equal("1. Good News About Hell", d.Seasons[0].Episodes[0].Label);
            var put = Assert.Single(rig.Store.Puts);
            Assert.Equal(title.Key, put.Key);
            Assert.Equal(rig.Clock.Now, put.Show.Stamp);
            Assert.Equal("1. Good News About Hell", put.Show.Seasons[0].Episodes[0].Label);
            // Seerr names only season 1's first episodes: the rest keep the names learnt before, rather than "Episode n".
            Assert.Equal("3. Stored S1E3", put.Show.Seasons[0].Episodes[2].Label);
            Assert.Equal("5. Stored S2E5", d.Seasons[1].Episodes[4].Label);
        }

        [Fact]
        public async Task A_failed_or_timed_out_refresh_keeps_the_old_record_and_raises_no_error()
        {
            var rig = Setup(budget: TimeSpan.FromMilliseconds(100), piece: TimeSpan.FromMilliseconds(250));
            var title = Severance();
            var old = Record(title, rig.Clock.Now - TimeSpan.FromDays(8), 9, 10);
            Keep(rig, title, old);
            rig.W.Seerr.HangStatus = true; rig.W.Seerr.HangSeason = true;

            var d = await rig.Shows.DetailsAsync(title); // past the budget with nothing live: the record, not an error
            Assert.True(d.CheckingLibrary);              // as it was: availability still unknown, so still "–"
            Assert.Equal("4. Stored S2E4", d.Seasons[1].Episodes[3].Label);
            Assert.True((await rig.Shows.DetailsAsync(title, fresh: true)).CheckingLibrary); // the same when asked afresh
            Assert.Null(await rig.Shows.FollowingAsync(title)); // an unverified record offers nothing to get

            Assert.False(await rig.Shows.EnsureFreshAsync(title)); // waits for that compute: it timed out, so nothing is kept
            Assert.Empty(rig.Store.Puts);
            Assert.True(rig.Store.TryGet(title.Key, out var kept));
            Assert.Same(old, kept);
        }

        [Fact]
        public async Task Live_results_rewrite_the_store()
        {
            var rig = Setup();
            var found = await Find(rig.W, "simpsons");
            var title = found with { Seerr = null }; // like a Jellyfin card: Seerr's entry comes from the record, its status is asked for
            Keep(rig, title, Record(found, rig.Clock.Now - TimeSpan.FromDays(1), 2, 3)); // kept with Seerr's status zeroed
            var putsWhenSeasonAsked = -1; var reportsWhenSeasonAsked = -1;
            var reports = new List<ShowDetails>();
            rig.W.Seerr.OnSeasonEntered = () => { putsWhenSeasonAsked = rig.Store.PutCount; lock (reports) reportsWhenSeasonAsked = reports.Count; };

            var d = await rig.Shows.DetailsAsync(title, x => { lock (reports) reports.Add(x); });

            Assert.True(reportsWhenSeasonAsked >= 2); // the stored one and the early (partial) live one
            Assert.Equal(0, putsWhenSeasonAsked);       // neither was stored
            Assert.Equal(0, reports[0].Title.Seerr!.Status);
            Assert.Equal(4, d.Title.Seerr!.Status);    // the zeroed status was asked for afresh
            var put = Assert.Single(rig.Store.Puts);
            Assert.Equal(new[] { 1, 2, 3, 4 }, put.Show.Seasons.Select(s => s.Number));
            Assert.Equal("1. Simpsons Roasting on an Open Fire", put.Show.Seasons[0].Episodes[0].Label);
            Assert.Equal(rig.Clock.Now, put.Show.Stamp);
        }

        [Fact]
        public async Task A_hung_seerr_lookup_never_drops_the_records_seerr_entry()
        {
            var w = CatalogueTests.Build();
            var clock = new Clock();
            var store = new MemStore();
            var found = await Find(w, "simpsons");
            var search = new CatalogueSearch(w.Data, w.Epg, w.Seerr, timeout: TimeSpan.FromMilliseconds(200));
            var shows = new ShowData(w.Data, w.Seerr, search, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), () => clock.Now, store) { RefreshSink = (_, _) => { } };
            var card = found with { Seerr = null }; // a Jellyfin card knows nothing of Seerr
            var kept = Record(found, clock.Now - TimeSpan.FromDays(8), 2, 3, 2, 2); // seasons 3 and 4 known only from Seerr
            lock (store.Shows) store.Shows[card.Key] = kept;
            w.Seerr.HangSearch = true;
            var lookups = w.Seerr.SearchCalls;

            var d = await shows.DetailsAsync(card);

            Assert.Equal(lookups, w.Seerr.SearchCalls); // the record's Seerr entry is used: no lookup
            Assert.Equal(456, d.Title.Seerr!.TmdbId);
            Assert.True(store.TryGet(card.Key, out var after));
            Assert.Equal(456, after!.Title.Seerr!.TmdbId);
            Assert.Equal(new[] { 1, 2, 3, 4 }, after.Seasons.Select(s => s.Number));
        }

        [Fact]
        public async Task Play_waits_for_availability_except_for_cards_carrying_a_source()
        {
            var rig = Setup(budget: TimeSpan.FromMilliseconds(300), piece: TimeSpan.FromSeconds(10));
            var series = await Find(rig.W, "simpsons");
            var film = await Find(rig.W, "alien");
            rig.W.Seerr.HangStatus = true; rig.W.Seerr.HangSeason = true;
            rig.W.Jellyfin.HangSearch = true; rig.W.IPlayer.HangSearch = true; rig.W.Jellyfin.HangChildren = true; rig.W.IPlayer.HangChildren = true;

            var seerrOnly = Severance();
            Keep(rig, seerrOnly, Record(seerrOnly, rig.Clock.Now, 9, 10));
            ShowDetails? first = null;
            var task = rig.Shows.DetailsAsync(seerrOnly, d => first ??= d);
            Assert.NotNull(first);
            Assert.Empty(first!.Title.Sources);
            Assert.Null(first.Next);
            Assert.Equal(ShowDetails.Checking, first.Summary);
            Assert.All(first.Seasons, s => Assert.Equal(SeasonState.Missing, s.State)); // shown as "–" while checking
            await task;

            Assert.NotEmpty(series.Sources);
            Keep(rig, series, Record(series, rig.Clock.Now, 2, 3));
            ShowDetails? fromCard = null;
            var t2 = rig.Shows.DetailsAsync(series, d => fromCard ??= d);
            Assert.NotNull(fromCard);
            Assert.Equal(series.Sources, fromCard!.Title.Sources);
            await t2;

            Keep(rig, film, new StoredShow(film with { Sources = Array.Empty<TitleSource>() }, Array.Empty<StoredSeason>(), rig.Clock.Now));
            ShowDetails? filmFirst = null;
            var t3 = rig.Shows.DetailsAsync(film, d => filmFirst ??= d);
            Assert.NotNull(filmFirst);
            Assert.Equal("alien", filmFirst!.Title.Best!.ItemId); // Play at once
            await t3;
        }

        [Fact]
        public async Task EnsureFreshAsync_skips_the_network_for_a_fresh_record_and_refreshes_a_missing_or_old_one()
        {
            var rig = Setup();
            var title = Severance();

            Assert.True(await rig.Shows.EnsureFreshAsync(title)); // nothing kept: asks
            Assert.True(rig.Store.TryGet(title.Key, out var first));
            Assert.Equal(rig.Clock.Now, first!.Stamp);
            Assert.Equal("1. Good News About Hell", first.Seasons[0].Episodes[0].Label);

            var status = rig.W.Seerr.StatusCalls; var seasons = rig.SeasonCalls.Value; var puts = rig.Store.PutCount;
            rig.Clock.Now += TimeSpan.FromDays(6);
            Assert.False(await rig.Shows.EnsureFreshAsync(title)); // under 7 days: no network at all
            Assert.Equal(status, rig.W.Seerr.StatusCalls);
            Assert.Equal(seasons, rig.SeasonCalls.Value);
            Assert.Equal(puts, rig.Store.PutCount);

            rig.Clock.Now += TimeSpan.FromDays(2);
            Assert.True(await rig.Shows.EnsureFreshAsync(title)); // over 7 days: asks again
            Assert.True(rig.SeasonCalls.Value > seasons);
            Assert.True(rig.Store.TryGet(title.Key, out var second));
            Assert.Equal(rig.Clock.Now, second!.Stamp);
        }

        [Fact]
        public async Task EnsureFreshAsync_never_reads_the_availability_cache_nor_queues_refreshes()
        {
            var rig = Setup();
            var refreshes = new List<string>();
            rig.Shows.RefreshSink = (k, _) => { lock (refreshes) refreshes.Add(k); };
            var title = Severance();
            Assert.True(await rig.Shows.EnsureFreshAsync(title)); // fills the availability cache
            var status = rig.W.Seerr.StatusCalls;

            rig.Clock.Now += TimeSpan.FromDays(8); // the record and every cached availability answer are stale now
            Assert.True(await rig.Shows.EnsureFreshAsync(title));

            Assert.True(rig.W.Seerr.StatusCalls > status); // asked itself: no stale answer served
            lock (refreshes) Assert.Empty(refreshes);       // and nothing handed to the sink
        }

        /// <summary>A kept record whose episodes are only numbered ("n. Episode n", no still, no synopsis), as Merge leaves a Seerr-only season.</summary>
        internal static StoredShow Placeholders(CatalogueTitle t, DateTimeOffset stamp, params int[] episodesPerSeason) =>
            new(t with { Sources = Array.Empty<TitleSource>(), Seerr = t.Seerr is { } r ? r with { Status = 0 } : null },
                episodesPerSeason.Select((count, i) => new StoredSeason(i + 1, $"Season {i + 1}",
                    Enumerable.Range(1, count).Select(n => new StoredEpisode(n, $"{n}. Episode {n}", null, null, null)).ToList(), count)).ToList(),
                stamp);

        [Fact]
        public void Stored_reads_the_kept_record_with_no_network()
        {
            var rig = Setup();
            var title = Severance();
            Assert.Null(rig.Shows.Stored(title));
            Keep(rig, title, Record(title, rig.Clock.Now - TimeSpan.FromDays(30), 9, 10));
            var status = rig.W.Seerr.StatusCalls; var searches = rig.W.Seerr.SearchCalls;

            var d = rig.Shows.Stored(title)!;

            Assert.True(d.CheckingLibrary);
            Assert.Equal("http://img/stored", d.Title.ImageUrl);
            Assert.Equal(new[] { 9, 10 }, d.Seasons.Select(s => s.Episodes.Count));
            Assert.Equal("4. Stored S2E4", d.Seasons[1].Episodes[3].Label);
            Assert.Equal("http://img/s1e3", d.Seasons[0].Episodes[2].ImageUrl);
            Assert.Equal((status, searches, 0), (rig.W.Seerr.StatusCalls, rig.W.Seerr.SearchCalls, rig.SeasonCalls.Value));
        }

        [Fact]
        public async Task EnsureSeasonAsync_fills_a_seerr_only_seasons_names_once()
        {
            var rig = Setup();
            var title = Severance();
            var stamp = rig.Clock.Now - TimeSpan.FromDays(2);
            Keep(rig, title, Placeholders(title, stamp, 9, 10));

            var stills = await rig.Shows.EnsureSeasonAsync(title, 1);

            Assert.Equal(new[] { "https://image.tmdb.org/t/p/w500/sev1.jpg" }, stills);
            Assert.Equal(1, rig.SeasonCalls.Value);
            var put = Assert.Single(rig.Store.Puts).Show;
            Assert.Equal(stamp, put.Stamp); // only one season was learnt: the record is no fresher than it was
            var s1 = put.Seasons[0].Episodes;
            Assert.Equal("1. Good News About Hell", s1[0].Label);
            Assert.Equal("Mark starts a new job.", s1[0].Synopsis);
            Assert.Equal("2. Half Loop", s1[1].Label);
            Assert.Equal("3. Episode 3", s1[2].Label);                 // Seerr didn't name it: still the placeholder
            Assert.Equal("1. Episode 1", put.Seasons[1].Episodes[0].Label); // other seasons untouched

            Assert.Equal(stills, await rig.Shows.EnsureSeasonAsync(title, 1)); // named now: no second ask, nothing kept again
            Assert.Equal(1, rig.SeasonCalls.Value);
            Assert.Single(rig.Store.Puts);
        }

        [Fact]
        public async Task EnsureSeasonAsync_never_overwrites_real_names_and_asks_nothing_for_a_named_or_library_season()
        {
            var rig = Setup();
            var title = Severance();
            Keep(rig, title, Record(title, rig.Clock.Now, 9, 10)); // every episode already named
            Assert.Equal(Enumerable.Range(1, 9).Select(n => $"http://img/s1e{n}"), await rig.Shows.EnsureSeasonAsync(title, 1));
            Assert.Equal(0, rig.SeasonCalls.Value);
            Assert.Empty(rig.Store.Puts);
            Assert.Empty(await rig.Shows.EnsureSeasonAsync(title, 7)); // a season the record doesn't have

            var library = new CatalogueTitle("Series:lib", TitleKind.Series, "Library Show", null, null, null, new[] { new TitleSource("Jellyfin", "lib", PlayVia.Mpv) });
            Keep(rig, library, Placeholders(library, rig.Clock.Now, 3)); // Seerr doesn't know it: nothing to ask
            Assert.Empty(await rig.Shows.EnsureSeasonAsync(library, 1));
            Assert.Empty(await rig.Shows.EnsureSeasonAsync(Severance() with { Key = "Series:nothing-kept" }, 1));
            Assert.Equal(0, rig.SeasonCalls.Value);
            Assert.Empty(rig.Store.Puts);

            // Seerr answering with a blank name never turns a placeholder into nothing.
            rig.W.Seerr.Seasons[(95396, 2)] = new JellyseerrSeasonDetails(2, new[] { new JellyseerrEpisode(1, " ", null, null), new JellyseerrEpisode(2, "Goodbye, Mrs. Selvig", null, "http://still/s2e2") });
            var s2 = Placeholders(title, rig.Clock.Now, 9, 10);
            Keep(rig, title, s2);
            Assert.Equal(new[] { "http://still/s2e2" }, await rig.Shows.EnsureSeasonAsync(title, 2));
            var kept = Assert.Single(rig.Store.Puts).Show.Seasons[1].Episodes;
            Assert.Equal("1. Episode 1", kept[0].Label);
            Assert.Equal("2. Goodbye, Mrs. Selvig", kept[1].Label);
        }

        [Fact]
        public async Task EnsureSeasonAsync_keeps_nothing_when_seerr_times_out()
        {
            var rig = Setup(piece: TimeSpan.FromMilliseconds(100));
            var title = Severance();
            var old = Placeholders(title, rig.Clock.Now, 9, 10);
            Keep(rig, title, old);
            rig.W.Seerr.HangSeason = true;

            Assert.Empty(await rig.Shows.EnsureSeasonAsync(title, 1));
            Assert.Empty(rig.Store.Puts);
            Assert.True(rig.Store.TryGet(title.Key, out var kept));
            Assert.Same(old, kept);

            rig.W.Seerr.HangSeason = false; // Seerr is back: the next warm fills it in
            Assert.Single(await rig.Shows.EnsureSeasonAsync(title, 1));
            Assert.Single(rig.Store.Puts);
        }

        [Fact]
        public async Task Secrets_and_sources_never_reach_the_store()
        {
            var rig = Setup();
            var title = await Find(rig.W, "simpsons") with { FocusItemId = "s1e1" };
            Assert.Contains(title.Sources, s => s.StreamUrl != null || s.ItemId.Length > 0);

            await rig.Shows.DetailsAsync(title);

            var put = Assert.Single(rig.Store.Puts).Show;
            Assert.Empty(put.Title.Sources);
            Assert.Null(put.Title.FocusItemId);
            Assert.Equal(0, put.Title.Seerr!.Status);
            var text = string.Join("\n", new[] { put.Title.ImageUrl, put.Title.Synopsis, put.Title.Subtitle }
                .Concat(put.Seasons.SelectMany(s => s.Episodes.SelectMany(e => new[] { e.Label, e.Sublabel, e.ImageUrl, e.Synopsis }))));
            Assert.DoesNotContain("http://stream/", text, StringComparison.Ordinal);
            Assert.True(put.Seasons.Count > 0);
        }
    }
}
