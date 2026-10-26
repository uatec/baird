using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ItemActionsTests
    {
        private static HomeCard Film(TimeSpan resume) => new(CardKind.Film, "film1", "Casper", "1995", null, 0.4, null, "Jellyfin: x", PlayVia.Mpv, "http://jf/film1", resume);
        private static HomeCard Episode() => new(CardKind.Episode, "ep1", "Lamentis", "S1:E3", null, 0.2, null, "Jellyfin: x", PlayVia.Mpv, "http://jf/ep1", TimeSpan.FromMinutes(5));

        [AvaloniaFact]
        public async Task Holding_ok_on_a_home_card_opens_its_actions()
        {
            var rig = HomeScreenTests.Context();
            var home = new HomeScreen(rig.Ctx);
            await home.LoadAsync();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            home.OnAction(UiAction.Down, false); // On now (not Up next)
            home.OnAction(UiAction.HoldOk, false);
            Assert.Equal("itemactions", rig.Ctx.Nav.State.Overlay?.Name);
            Assert.IsType<HomeCard>(rig.Ctx.Nav.State.Overlay!.Params);
            Assert.Empty(rig.Toasts);
        }

        [AvaloniaFact]
        public void Something_part_watched_offers_resume_and_from_the_start()
        {
            var rig = HomeScreenTests.Context();
            var data = HomeDataTests.Build().Data;
            rig.Ctx.Shows = new Baird.NewUi.Adapters.ShowData(data, null, new Baird.NewUi.Adapters.CatalogueSearch(data, new Baird.Tests.Mocks.MockEpgService(), null, null));
            var a = new ItemActions(rig.Ctx, Film(TimeSpan.FromMinutes(40)));
            Assert.Equal("Resume", a.Items[0]);
            Assert.Contains("Play from the start", a.Items);
            Assert.Contains(a.Items, i => i.Contains("watchlist"));
        }

        [AvaloniaFact]
        public void Play_from_the_start_ignores_the_resume_point()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("itemactions"));
            var a = new ItemActions(rig.Ctx, Film(TimeSpan.FromMinutes(40)));
            for (var i = 0; i < a.Items.ToList().IndexOf("Play from the start"); i++) a.OnAction(UiAction.Down, false);
            a.OnAction(UiAction.Ok, false);
            Assert.Equal(("http://jf/film1", (double?)null), rig.Engine.Plays.Single());
            Assert.True(rig.Ctx.Nav.State.AtVideo);
        }

        [AvaloniaFact]
        public void A_film_or_show_offers_its_page_but_an_episode_from_history_cannot()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Shows = new Baird.NewUi.Adapters.ShowData(HomeDataTests.Build().Data, null, new Baird.NewUi.Adapters.CatalogueSearch(HomeDataTests.Build().Data, new Baird.Tests.Mocks.MockEpgService(), null, null));
            Assert.Contains("Open the page", new ItemActions(rig.Ctx, Film(TimeSpan.Zero)).Items);
            Assert.DoesNotContain("Open the page", new ItemActions(rig.Ctx, Episode()).Items);
        }

        [AvaloniaFact]
        public void Up_next_items_can_be_removed_from_up_next()
        {
            var rig = HomeScreenTests.Context();
            Assert.Contains("Remove from Up next", new ItemActions(rig.Ctx, Film(TimeSpan.FromMinutes(40)), fromUpNext: true).Items);
            Assert.DoesNotContain("Remove from Up next", new ItemActions(rig.Ctx, Film(TimeSpan.FromMinutes(40))).Items);
        }

        [AvaloniaFact]
        public async Task Holding_ok_on_an_up_next_card_offers_removal()
        {
            var rig = HomeScreenTests.Context();
            var home = new HomeScreen(rig.Ctx);
            await home.LoadAsync();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            home.OnAction(UiAction.HoldOk, false);
            Assert.Equal("itemactions-upnext", rig.Ctx.Nav.State.Overlay?.Name);
        }
    }
}
