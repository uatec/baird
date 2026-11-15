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
        public void Choosing_an_action_from_the_up_next_menu_closes_it()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("itemactions-upnext"));
            var a = new ItemActions(rig.Ctx, Film(TimeSpan.FromMinutes(40)), fromUpNext: true);
            for (var i = 0; i < a.Items.ToList().IndexOf("Remove from Up next"); i++) a.OnAction(UiAction.Down, false);
            a.OnAction(UiAction.Ok, false);
            Assert.Null(rig.Ctx.Nav.State.Overlay);
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
        public void Up_next_items_can_be_forgotten()
        {
            var rig = HomeScreenTests.Context();
            Assert.Contains("Forget this", new ItemActions(rig.Ctx, Film(TimeSpan.FromMinutes(40)), fromUpNext: true).Items);
            Assert.DoesNotContain("Forget this", new ItemActions(rig.Ctx, Film(TimeSpan.FromMinutes(40))).Items);
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

        // ---- a live channel: the guide, or straight to what is on ----
        private static HomeCard LiveChannel(string? title, string? subtitle = null, bool noInfo = false)
        {
            var ch = new Channel("ch4", "104", "Channel 4", null, "http://tvh/4");
            var now = DateTimeOffset.UtcNow;
            var p = title == null ? null : new Programme("p1", "ch4", title, subtitle, "synopsis", now.AddMinutes(-20), now.AddMinutes(40), noInfo);
            return new HomeCard(CardKind.Channel, "ch4", "104  Channel 4", p?.Title, null, null, "live", "TV", PlayVia.Mpv, "http://tvh/4", Channel: ch, Programme: p);
        }

        [AvaloniaFact]
        public void A_channel_with_a_programme_on_offers_the_guide_or_that_programmes_page()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows;
            var menu = new ItemActions(rig.Ctx, LiveChannel("The Simpsons", "Bart the Genius"), _ => { });
            Assert.Equal(new[] { "Watch now", "Open “The Simpsons”", "This channel's schedule" }, menu.Items);
        }

        [AvaloniaFact]
        public void A_stretch_the_guide_has_nothing_for_offers_no_page()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows;
            Assert.DoesNotContain(menu(rig, LiveChannel("Channel 4", noInfo: true)), i => i.StartsWith("Open"));
            Assert.DoesNotContain(menu(rig, LiveChannel(null)), i => i.StartsWith("Open"));
            static System.Collections.Generic.IReadOnlyList<string> menu(HomeScreenTests.Rig r, HomeCard c) => new ItemActions(r.Ctx, c, _ => { }).Items;
        }

        [AvaloniaFact]
        public async Task Open_the_programme_finds_its_series_or_film_page_by_name()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows; rig.Ctx.Channels = CatalogueTests.Channels;
            var menu = new ItemActions(rig.Ctx, LiveChannel("The Simpsons", "Bart the Genius"), _ => { });
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("itemactions", LiveChannel("The Simpsons")));
            for (var i = 0; i < menu.Items.ToList().IndexOf("Open “The Simpsons”"); i++) menu.OnAction(UiAction.Down, false);
            menu.OnAction(UiAction.Ok, false);
            await Task.Delay(300);
            var page = rig.Ctx.Nav.State.Pages.Single();
            Assert.Equal("show", page.Name);
            var title = (CatalogueTitle)page.Params!;
            Assert.Equal("The Simpsons", title.Name);
            Assert.Equal(TitleKind.Series, title.Kind);
        }

        [AvaloniaFact]
        public async Task A_programme_nothing_knows_says_so_rather_than_opening_nothing()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows; rig.Ctx.Channels = CatalogueTests.Channels;
            var menu = new ItemActions(rig.Ctx, LiveChannel("Zzyzx Road Live"), _ => { });
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("itemactions", LiveChannel("Zzyzx Road Live")));
            for (var i = 0; i < menu.Items.ToList().IndexOf("Open “Zzyzx Road Live”"); i++) menu.OnAction(UiAction.Down, false);
            menu.OnAction(UiAction.Ok, false);
            await Task.Delay(300);
            Assert.Empty(rig.Ctx.Nav.State.Pages);
            Assert.Contains(rig.Toasts, t => t.Contains("Zzyzx Road Live") && t.Contains("can't find"));
        }
    }
}
