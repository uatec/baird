using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>
    /// A service that drives the browser lists a show's season in view (the one last opened on, else the first) at once and the others
    /// one at a time later, so a page doesn't wait on 37 listings of a browser; a season not listed yet reads "–".
    /// </summary>
    public class LazySeasonsTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        private sealed record Rig(CatalogueTests.World W, CatalogueTests.Library Browser, ShowData Shows, ShowDataStoreTests.MemStore Store, CatalogueTitle Title,
            List<(string Key, Func<CancellationToken, Task> Work)> Background);

        /// <summary>A show on ITVX (played in the browser) with five seasons of two episodes each.</summary>
        private static Rig Build(int? opening = null)
        {
            var browser = new CatalogueTests.Library(ItvxProvider.SourceName);
            browser.Items.Add(CatalogueTests.Item("show", "Lazy Show", source: ItvxProvider.SourceName, type: MediaType.Brand));
            browser.Children["show"] = Enumerable.Range(1, 5).Select(n => CatalogueTests.Item($"show-s{n}", $"Season {n}", source: ItvxProvider.SourceName, type: MediaType.Brand)).ToList();
            for (var n = 1; n <= 5; n++)
                browser.Children[$"show-s{n}"] = new() { CatalogueTests.Item($"s{n}e1", $"Episode {n}.1", source: ItvxProvider.SourceName), CatalogueTests.Item($"s{n}e2", $"Episode {n}.2", source: ItvxProvider.SourceName) };
            var w = CatalogueTests.Build(browser);
            var store = new ShowDataStoreTests.MemStore();
            var background = new List<(string, Func<CancellationToken, Task>)>();
            var shows = new ShowData(w.Data, w.Seerr, w.Search, store: store) { RefreshSink = (_, _) => { } };
            shows.BackgroundSink = batch => { lock (background) background.AddRange(batch); };
            var title = new CatalogueTitle("Series:show", TitleKind.Series, "Lazy Show", null, null, null, new[] { new TitleSource(ItvxProvider.SourceName, "show", PlayVia.Browser) });
            if (opening is { } n3) store.Put(title.Key, new StoredShow(title with { Sources = Array.Empty<TitleSource>() }, Array.Empty<StoredSeason>(), DateTimeOffset.UtcNow, n3));
            store.Puts.Clear();
            return new Rig(w, browser, shows, store, title, background);
        }

        [Fact]
        public async Task Only_the_season_in_view_is_listed_at_once_and_the_others_read_dash_until_they_are()
        {
            var rig = Build();
            var d = await rig.Shows.DetailsAsync(rig.Title);
            Assert.Equal(2, rig.Browser.ChildrenCalls);                         // the show's seasons, and season 1
            Assert.Equal(5, d.Seasons.Count);
            Assert.False(d.Seasons[0].Pending);
            Assert.Equal(2, d.Seasons[0].Episodes.Count);
            Assert.All(d.Seasons.Skip(1), s => { Assert.True(s.Pending); Assert.Equal("–", s.StateLabel); Assert.Equal(SeasonState.Available, s.State); });
        }

        [Fact]
        public async Task The_other_seasons_go_to_the_background_nearest_the_opening_season_first_and_arrive_one_at_a_time()
        {
            var rig = Build();
            var changed = new List<string>();
            rig.Shows.Changed += (k, c) => { if (c == ShowData.ShowChange.Availability) lock (changed) changed.Add(k); };
            await rig.Shows.DetailsAsync(rig.Title);
            Assert.Equal(4, rig.Background.Count);
            Assert.Equal(new[] { "show-s2", "show-s3", "show-s4", "show-s5" }, rig.Background.Select(b => b.Key.Split('|')[^1]));

            await rig.Background[0].Work(CancellationToken.None);               // season 2 arrives
            Assert.Equal(new[] { rig.Title.Key }, changed);                     // the page is told
            var d = await rig.Shows.DetailsAsync(rig.Title);
            Assert.False(d.Seasons[1].Pending);
            Assert.Equal(2, d.Seasons[1].Episodes.Count);
            Assert.True(d.Seasons[2].Pending);                                  // the rest still wait their turn
        }

        [Fact]
        public async Task The_season_the_page_last_opened_on_is_listed_first()
        {
            var rig = Build(opening: 3);
            var d = await rig.Shows.DetailsAsync(rig.Title);
            Assert.False(d.Seasons[2].Pending);                                 // season 3
            Assert.True(d.Seasons[0].Pending);
            Assert.Equal(new[] { "show-s4", "show-s2", "show-s5", "show-s1" }, rig.Background.Select(b => b.Key.Split('|')[^1])); // nearest first, later before earlier
        }

        [Fact]
        public async Task A_season_tab_lit_while_dash_is_listed_on_its_own_and_not_again_while_it_is_fresh()
        {
            var rig = Build();
            await rig.Shows.DetailsAsync(rig.Title);
            var before = rig.Browser.ChildrenCalls;
            Assert.True(await rig.Shows.EnsureSeasonListedAsync(rig.Title, 4));
            Assert.Equal(before + 1, rig.Browser.ChildrenCalls);
            Assert.False(await rig.Shows.EnsureSeasonListedAsync(rig.Title, 4)); // listed already
            Assert.Equal(before + 1, rig.Browser.ChildrenCalls);
            var d = await rig.Shows.DetailsAsync(rig.Title);
            Assert.False(d.Seasons[3].Pending);
        }

        [Fact]
        public async Task When_every_episode_of_the_season_is_watched_the_next_season_is_listed_too()
        {
            var rig = Build(opening: 1);
            rig.W.History.Add(new HistoryItem { Id = "s1e1", IsFinished = true, LastWatched = DateTime.Now.AddDays(-2), Duration = TimeSpan.FromMinutes(30), LastPosition = TimeSpan.FromMinutes(30) });
            rig.W.History.Add(new HistoryItem { Id = "s1e2", IsFinished = true, LastWatched = DateTime.Now.AddDays(-1), Duration = TimeSpan.FromMinutes(30), LastPosition = TimeSpan.FromMinutes(30) });
            var d = await rig.Shows.DetailsAsync(rig.Title);
            Assert.False(d.Seasons[0].Pending);
            Assert.False(d.Seasons[1].Pending);                                 // where the next episode is
            Assert.True(d.Seasons[2].Pending);
            Assert.Equal("Next S2 E1", d.Next?.Label);
        }

        [Fact]
        public async Task What_the_page_opened_on_is_kept_for_next_time()
        {
            var rig = Build();
            await rig.Shows.DetailsAsync(rig.Title);
            Assert.Equal(1, rig.Store.Shows[rig.Title.Key].Opening);
        }

        [Fact]
        public void Episodes_carry_the_number_the_service_gave_them_not_their_position_in_its_listing()
        {
            // Jellyfin lists two of a season's three episodes (the first isn't there): by position they were 1 and 2.
            var e2 = new Baird.ViewModels.MediaItemViewModel(CatalogueTests.Item("e2", "We Bare Bears", details: "S1 E2", subtitle: "Food Truck/Chloe"));
            var e3 = new Baird.ViewModels.MediaItemViewModel(CatalogueTests.Item("e3", "We Bare Bears", details: "S1 E3", subtitle: "Pandas Date/Everyday Bears"));
            var library = new[] { new ShowData.LibrarySeason(1, "Season 1", new[] { e2, e3 }, "Jellyfin") };
            var status = new JellyseerrMediaStatus(5, new[] { new JellyseerrSeason(1, 3, 5) }, Array.Empty<JellyseerrDownload>());

            var season = ShowData.Merge(library, status, "Jellyfin").Single();

            Assert.Equal(new[] { 1, 2, 3 }, season.Episodes.Select(x => x.Number));
            Assert.Equal(EpisodeState.Missing, season.Episodes[0].State);        // the one that isn't there is the first, not the last
            Assert.Equal("2. Food Truck/Chloe", season.Episodes[1].Label);
            Assert.Equal("3. Pandas Date/Everyday Bears", season.Episodes[2].Label);
            Assert.Equal("S1 E3", season.Episodes[2].Code);                    // what Continue watching calls it
        }

        [Fact]
        public void A_playing_episode_is_headed_by_its_show_with_the_code_and_title_under_it_as_in_continue_watching()
        {
            var card = new HomeCard(CardKind.Episode, "e3", "3. Pandas Date/Everyday Bears", "23 min", null, null, null, "Jellyfin", PlayVia.Mpv, Show: "We Bare Bears", Code: "S1 E3");
            var now = card.AsEpisodeOfItsShow();
            Assert.Equal("We Bare Bears", now.Label);
            Assert.Equal("S1 E3 \u00B7 Pandas Date/Everyday Bears", now.Sublabel);
            Assert.Same(card.Title, now.Title);
            var film = new HomeCard(CardKind.Film, "f", "Alien", "1979", null, null, null, "Jellyfin", PlayVia.Mpv);
            Assert.Same(film, film.AsEpisodeOfItsShow());                         // anything else is left as it is
        }
    }
}
