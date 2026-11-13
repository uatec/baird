using System;
using System.Linq;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Avalonia.Headless.XUnit;
using Baird.NewUi.Core;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>Deleting from the server: a series or film goes from Radarr/Sonarr (with its files), Seerr and Jellyfin; an episode from Jellyfin.</summary>
    public class DeletionTests
    {
        private static (Deletion Deletion, CatalogueTests.World World) Build()
        {
            var w = CatalogueTests.Build();
            return (new Deletion(w.Data, w.Seerr, w.Search), w);
        }

        private static async Task<HomeCard> Found(CatalogueTests.World w, string name) =>
            (await w.Search.DownloadsAsync(name)).First(h => h.Card.Label == name).Card;

        [Fact]
        public async Task Deleting_a_series_in_jellyfin_and_seerr_removes_it_from_both()
        {
            var (d, w) = Build();
            w.Seerr.Asked.Add(CatalogueTests.Asked(456, "tv", "The Simpsons", 30, mediaStatus: 4));
            var card = await Found(w, "The Simpsons");
            Assert.True(d.CanDelete(card));
            var (ok, message) = await d.DeleteAsync(card);
            Assert.True(ok, message);
            Assert.Equal(new[] { (456, "tv") }, w.Seerr.Removed);
            Assert.Equal(new[] { "simp" }, w.Jellyfin.Deleted);
            Assert.Contains("The Simpsons", message);
        }

        [Fact]
        public async Task A_jellyfin_title_seerr_doesnt_list_as_requested_is_found_in_seerr_by_name()
        {
            var (d, w) = Build();
            var card = await Found(w, "Alien"); // Seerr has it (348), but it isn't in the requests
            var (ok, _) = await d.DeleteAsync(card);
            Assert.True(ok);
            Assert.Equal(new[] { (348, "movie") }, w.Seerr.Removed);
            Assert.Equal(new[] { "alien" }, w.Jellyfin.Deleted);
        }

        [Fact]
        public async Task A_request_that_hasnt_arrived_is_removed_from_seerr_only()
        {
            var (d, w) = Build();
            w.Seerr.Asked.Add(CatalogueTests.Asked(95396, "tv", "Severance", 1, mediaStatus: 3));
            var card = await Found(w, "Severance");
            Assert.True(d.CanDelete(card));
            Assert.True((await d.DeleteAsync(card)).Ok);
            Assert.Equal(new[] { (95396, "tv") }, w.Seerr.Removed);
            Assert.Empty(w.Jellyfin.Deleted);
        }

        [Fact]
        public async Task An_episode_is_deleted_from_jellyfin_only()
        {
            var (d, w) = Build();
            var ep = new HomeCard(CardKind.Episode, "s1e2", "Bart the Genius", "S1 E2", null, null, null, "Jellyfin: nas", PlayVia.Mpv, "http://stream/s1e2");
            Assert.True(d.CanDelete(ep));
            Assert.True((await d.DeleteAsync(ep)).Ok);
            Assert.Equal(new[] { "s1e2" }, w.Jellyfin.Deleted);
            Assert.Empty(w.Seerr.Removed);
        }

        [Fact]
        public void Things_not_on_the_server_cant_be_deleted()
        {
            var (d, _) = Build();
            Assert.False(d.CanDelete(new HomeCard(CardKind.Series, "ip-simp", "The Simpsons", null, null, null, null, "BBC iPlayer", PlayVia.Browser)));
            Assert.False(d.CanDelete(new HomeCard(CardKind.Film, "tmdb:movie:7", "Andor", null, null, null, "get", "Seerr", PlayVia.None))); // not requested
            Assert.False(d.CanDelete(new HomeCard(CardKind.Channel, "ch1", "BBC One", null, null, null, "live", "TV", PlayVia.Mpv)));
        }

        [Fact]
        public async Task A_failure_says_so()
        {
            var (d, w) = Build();
            w.Jellyfin.Fail = true;
            var ep = new HomeCard(CardKind.Episode, "s1e2", "Bart the Genius", null, null, null, null, "Jellyfin: nas", PlayVia.Mpv, "http://stream/s1e2");
            var (ok, message) = await d.DeleteAsync(ep);
            Assert.False(ok);
            Assert.StartsWith("Couldn't delete Bart the Genius", message);
        }

        [AvaloniaFact]
        public async Task Hold_ok_offers_delete_which_asks_first_then_deletes_and_says_so()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows;
            rig.Ctx.Deleter = new Deletion(w.Data, w.Seerr, w.Search);
            var changed = 0;
            rig.Ctx.LibraryChanged += () => changed++;
            var ep = new HomeCard(CardKind.Episode, "s1e2", "Bart the Genius", null, null, null, null, "Jellyfin: nas", PlayVia.Mpv, "http://stream/s1e2");

            var menu = new ItemActions(rig.Ctx, ep);
            Assert.Contains("Delete from the server…", menu.Items);
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("itemactions", ep));
            for (var i = 0; i < menu.Items.Count; i++) menu.OnAction(UiAction.Down, false);
            menu.OnAction(UiAction.Ok, false);

            var ask = Assert.IsType<ChoiceList>(rig.Ctx.Nav.State.Overlay!.Params);
            Assert.Equal("Delete Bart the Genius for good?", ask.Title);
            Assert.Equal("Keep it", ask.Choices[0].Label); // the safe choice is lit first
            Assert.Empty(w.Jellyfin.Deleted);
            ask.Choices[1].Run();
            await Task.Delay(50);
            Assert.Equal(new[] { "s1e2" }, w.Jellyfin.Deleted);
            Assert.Contains("Deleted Bart the Genius from the server.", rig.Toasts);
            Assert.Equal(1, changed);
        }

        [AvaloniaFact]
        public void Channels_and_things_to_request_offer_no_delete()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows;
            rig.Ctx.Deleter = new Deletion(w.Data, w.Seerr, w.Search);
            var get = new HomeCard(CardKind.Film, "tmdb:movie:7", "Andor", null, null, null, "get", "Seerr", PlayVia.None);
            Assert.DoesNotContain("Delete from the server…", new ItemActions(rig.Ctx, get).Items);
        }
    }
}

namespace Baird.Tests.NewUi
{
    public class EpisodeActionsTests
    {
        [Avalonia.Headless.XUnit.AvaloniaFact]
        public async System.Threading.Tasks.Task An_episode_opens_its_series_page_not_a_film_page()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Shows = w.Shows;
            var series = new CatalogueTitle("Series:simp", TitleKind.Series, "The Simpsons", null, null, null, new[] { new TitleSource("Jellyfin", "simp", PlayVia.Mpv) });
            rig.Ctx.SeriesOf = c => System.Threading.Tasks.Task.FromResult<CatalogueTitle?>(c.Id == "s1e2" ? series : null);
            var ep = new HomeCard(CardKind.Episode, "s1e2", "The Simpsons", "S1 E2 · Bart the Genius", null, 0.3, null, "Jellyfin: nas", PlayVia.Mpv, "http://stream/s1e2");
            var menu = new Baird.NewUi.Screens.ItemActions(rig.Ctx, ep);
            Xunit.Assert.DoesNotContain("Open the page", menu.Items);
            var i = menu.Items.ToList().IndexOf("Open the series");
            Xunit.Assert.True(i >= 0);
            for (var k = 0; k < i; k++) menu.OnAction(Baird.NewUi.Core.UiAction.Down, false);
            menu.OnAction(Baird.NewUi.Core.UiAction.Ok, false);
            await System.Threading.Tasks.Task.Delay(20);
            var page = Xunit.Assert.Single(rig.Ctx.Nav.State.Pages);
            Xunit.Assert.Equal(series with { FocusItemId = "s1e2" }, page.Params); // opened on that episode
        }
    }
}
