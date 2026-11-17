using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Baird.NewUi.Adapters;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Screens;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class FilmPageTests
    {
        private static HomeCard SeerrFilm(int id, string name, string? synopsis = null, string year = "1995") =>
            new(CardKind.Film, $"tmdb:movie:{id}", name, year, null, null, "get", "Seerr", PlayVia.None, Synopsis: synopsis);

        [Fact]
        public void Seerr_status_carries_the_overview_of_the_title()
        {
            var s = JellyseerrService.ParseMediaStatus("{\"overview\":\"A heist.\",\"mediaInfo\":{\"status\":1}}");
            Assert.Equal("A heist.", s.Overview);
            Assert.Null(JellyseerrService.ParseMediaStatus("{\"mediaInfo\":{\"status\":1}}").Overview);
        }

        [Fact]
        public async Task A_film_card_with_no_synopsis_gets_seerrs_overview()
        {
            var w = CatalogueTests.Build();
            w.Seerr.Status[7777] = new JellyseerrMediaStatus(1, Array.Empty<JellyseerrSeason>(), Array.Empty<JellyseerrDownload>(), "A crew plans one last job.");
            var d = await w.Shows.DetailsAsync(ShowData.FromCard(SeerrFilm(7777, "Heat")));
            Assert.Equal("A crew plans one last job.", d.Title.Synopsis);
        }

        [Fact]
        public async Task A_synopsis_the_card_already_has_is_kept()
        {
            var w = CatalogueTests.Build();
            w.Seerr.Status[7777] = new JellyseerrMediaStatus(1, Array.Empty<JellyseerrSeason>(), Array.Empty<JellyseerrDownload>(), "From Seerr.");
            var d = await w.Shows.DetailsAsync(ShowData.FromCard(SeerrFilm(7777, "Heat", "From the card.")));
            Assert.Equal("From the card.", d.Title.Synopsis);
        }

        private static async Task<(ShowPage Page, HomeScreenTests.Rig Rig, CatalogueTests.World World)> Open(HomeCard card, bool withDeleter = true, Action<CatalogueTests.World>? setup = null)
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows;
            setup?.Invoke(w);
            if (withDeleter) rig.Ctx.Deleter = new Deletion(w.Data, w.Seerr, w.Search);
            var page = new ShowPage(rig.Ctx, ShowData.FromCard(card));
            await page.Loaded;
            return (page, rig, w);
        }

        [AvaloniaFact]
        public async Task A_film_in_jellyfin_offers_play_and_delete()
        {
            var (page, _, _) = await Open(SeerrFilm(348, "Alien", year: "1979"));
            Assert.Equal(new[] { "Play", "Watchlist", "Delete…" }, page.ButtonLabels);
        }

        [AvaloniaFact]
        public async Task Delete_asks_first_and_then_deletes_the_film_from_jellyfin()
        {
            var (page, rig, w) = await Open(SeerrFilm(348, "Alien", year: "1979"));
            var at = page.ButtonLabels.ToList().IndexOf("Delete…");
            for (var i = 0; i < at; i++) page.OnAction(UiAction.Right, false);
            page.OnAction(UiAction.Ok, false);
            var ask = Assert.IsType<ChoiceList>(rig.Ctx.Nav.State.Overlay!.Params);
            Assert.Equal("Delete Alien for good?", ask.Title);
            Assert.Equal("Keep it", ask.Choices[0].Label);
            Assert.Empty(w.Jellyfin.Deleted);
            ask.Choices[1].Run();
            await Task.Delay(100);
            Assert.Equal(new[] { "alien" }, w.Jellyfin.Deleted);
        }

        [AvaloniaFact]
        public async Task A_film_that_is_not_in_jellyfin_offers_no_delete()
        {
            var (page, _, _) = await Open(SeerrFilm(999, "The Simpsons Movie", year: "2007"));
            Assert.DoesNotContain("Delete…", page.ButtonLabels);
        }

        [AvaloniaFact]
        public async Task Without_a_deleter_there_is_no_delete_button()
        {
            var (page, _, _) = await Open(SeerrFilm(348, "Alien", year: "1979"), withDeleter: false);
            Assert.DoesNotContain("Delete…", page.ButtonLabels);
        }

        // ---- a request shows up in Downloads at once ----
        [AvaloniaFact]
        public async Task A_request_tells_home_and_search_the_library_changed_so_downloads_refresh()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows;
            w.Seerr.Results.Add(new JellyseerrSearchResult { Id = 1234, MediaType = "movie", Title = "Heat", ReleaseDate = "1995-12-15", MediaInfoStatus = 1 });
            var changed = 0;
            rig.Ctx.LibraryChanged += () => changed++;
            var before = (await w.Search.DownloadsAsync("")).Count; // reads and keeps Downloads
            var page = new ShowPage(rig.Ctx, ShowData.FromCard(SeerrFilm(1234, "Heat")));
            await page.Loaded;
            Assert.Contains("Request…", page.ButtonLabels);
            page.OnAction(UiAction.Ok, false);
            var ask = Assert.IsType<ChoiceList>(rig.Ctx.Nav.State.Overlay!.Params);
            w.Seerr.Asked.Add(new JellyseerrRequest { Id = 9, Status = 1, TmdbId = 1234, MediaType = "movie", Title = "Heat", CreatedAt = DateTime.UtcNow.ToString("o"), UpdatedAt = DateTime.UtcNow.ToString("o"), MediaInfoStatus = 2 });
            ask.Choices[0].Run();
            await Task.Delay(150);
            Assert.True(changed >= 1, "nobody was told the library changed");
            Assert.Equal(before + 1, (await w.Search.DownloadsAsync("")).Count); // not the copy kept from before the request
        }

        // ---- discarding a download in progress, from its page ----
        [AvaloniaFact]
        public async Task A_film_being_downloaded_offers_discard_and_asks_first()
        {
            var (page, rig, w) = await Open(SeerrFilm(999, "The Simpsons Movie", year: "2007"),
                setup: w => w.Seerr.Status[999] = new JellyseerrMediaStatus(3, Array.Empty<JellyseerrSeason>(), new[] { new JellyseerrDownload(null, null, 100, 25) }));
            Assert.Contains("Discard download…", page.ButtonLabels);
            var at = page.ButtonLabels.ToList().IndexOf("Discard download…");
            for (var i = 0; i < at; i++) page.OnAction(UiAction.Right, false);
            page.OnAction(UiAction.Ok, false);
            var ask = Assert.IsType<ChoiceList>(rig.Ctx.Nav.State.Overlay!.Params);
            Assert.Equal("Discard The Simpsons Movie?", ask.Title);
            ask.Choices[1].Run();
            await Task.Delay(100);
            Assert.Equal(new[] { (999, "movie") }, w.Seerr.Discarded);
        }

        [AvaloniaFact]
        public async Task Nothing_to_discard_when_it_is_here_or_was_never_requested()
        {
            var (here, _, _) = await Open(SeerrFilm(348, "Alien", year: "1979"));
            Assert.DoesNotContain("Discard download…", here.ButtonLabels);
            var (never, _, _) = await Open(SeerrFilm(1234, "Heat"));
            Assert.DoesNotContain("Discard download…", never.ButtonLabels);
        }

        [AvaloniaFact]
        public async Task A_series_being_downloaded_can_be_discarded_but_one_that_is_partly_here_cannot()
        {
            var coming = new HomeCard(CardKind.Series, "tmdb:tv:7001", "Silo", "2023", null, null, "get", "Seerr", PlayVia.None);
            var (page, _, _) = await Open(coming, setup: w => w.Seerr.Status[7001] = new JellyseerrMediaStatus(3,
                new[] { new JellyseerrSeason(1, 10, 3), new JellyseerrSeason(2, 10, 1) }, new[] { new JellyseerrDownload(1, 1, 100, 60) }));
            Assert.Contains("Discard download…", page.ButtonLabels);

            // The Simpsons: seasons 1-2 are in Jellyfin: whatever is still coming is not separable from what is here.
            var here = new HomeCard(CardKind.Series, "tmdb:tv:456", "The Simpsons", "1989", null, null, null, "Seerr", PlayVia.None);
            var (partly, _, _) = await Open(here);
            Assert.DoesNotContain("Discard download…", partly.ButtonLabels);
        }
    }
}
