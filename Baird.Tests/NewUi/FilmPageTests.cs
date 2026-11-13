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

        private static async Task<(ShowPage Page, HomeScreenTests.Rig Rig, CatalogueTests.World World)> Open(HomeCard card, bool withDeleter = true)
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows;
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
    }
}
