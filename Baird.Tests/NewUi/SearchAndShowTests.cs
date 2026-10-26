using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Baird.NewUi;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class SearchAndShowTests
    {
        private sealed record Rig(HomeScreenTests.Rig Home, CatalogueTests.World World)
        {
            public NewUiContext Ctx => Home.Ctx;
        }

        private static Rig Build()
        {
            var home = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            home.Ctx.Search = w.Search;
            home.Ctx.Shows = w.Shows;
            home.Ctx.Channels = CatalogueTests.Channels;
            home.Ctx.Timers = home.Timers;
            return new Rig(home, w);
        }

        private static Window Show(Control view)
        {
            var w = new Window { Width = 1920, Height = 1080, Content = view };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            return w;
        }

        private static async Task Wait(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); } }

        private static async Task Type(SearchScreen s, Rig rig, string text)
        {
            foreach (var ch in text)
                if (char.IsDigit(ch)) s.OnAction(UiAction.Digit0 + (ch - '0'), false);
                else TypeLetter(s, ch);
            rig.Home.Timers.Advance(SearchScreen.Debounce);
            await s.RunAsync();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Walk the on-screen keyboard to a letter and press OK (as the remote would).</summary>
        private static void TypeLetter(SearchScreen s, char ch)
        {
            var rows = OnScreenKeyboard.Rows;
            var (r, c) = Enumerable.Range(0, rows.Length).SelectMany(i => Enumerable.Range(0, rows[i].Length).Select(j => (i, j))).First(p => rows[p.i][p.j] == ch.ToString());
            // From wherever the keyboard is: go to the top-left first (row 1 col 0 is reachable from anywhere by Left/Up).
            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Left, false);
            for (var i = 0; i < 5; i++) s.OnAction(UiAction.Down, false);
            for (var i = 0; i < 3; i++) s.OnAction(UiAction.Up, false); // row 1 (Up from row 0 would leave the keyboard)
            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Left, false);
            var row = 1;
            while (row < r) { s.OnAction(UiAction.Down, false); row++; }
            if (r == 0) s.OnAction(UiAction.Up, false);
            for (var i = 0; i < c; i++) s.OnAction(UiAction.Right, false);
            s.OnAction(UiAction.Ok, false);
        }

        [AvaloniaFact]
        public async Task The_keyboard_and_the_remote_digits_type_and_del_deletes_and_the_search_waits_for_a_pause()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            TypeLetter(s, 'a'); TypeLetter(s, 'l');
            s.OnAction(UiAction.Digit1, false);
            Assert.Equal("al1", s.Query);
            s.OnAction(UiAction.Del, false);
            Assert.Equal("al", s.Query);
            Assert.Empty(s.ResultRows); // not yet: the search waits for a pause in typing
            rig.Home.Timers.Advance(SearchScreen.Debounce);
            await s.RunAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Alien", s.ResultRows[0][0].Title!.Name);
        }

        [AvaloniaFact]
        public async Task Results_are_grouped_top_result_then_shows_and_films_then_on_tv()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            await Type(s, rig, "simpsons");
            var rows = s.ResultRows;
            Assert.Equal("The Simpsons", rows[0].Single().Title!.Name);
            Assert.Equal(new[] { "Jellyfin", "BBC iPlayer" }, rows[0][0].Title!.Sources.Select(x => x.Service));
            Assert.Contains(rows.SelectMany(r => r), h => h.Title?.Name == "The Simpsons Movie" && h.Card.Badge == "requested");
            Assert.Equal(HitKind.Listing, rows[^1][0].Kind);
        }

        [AvaloniaFact]
        public async Task Right_off_the_keyboard_reaches_the_results_and_ok_on_a_series_opens_its_page()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            await Type(s, rig, "simpsons");
            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Right, false);
            var lit = Assert.IsType<Card>(s.LitElement);
            Assert.Equal("The Simpsons", lit.Data.Label);
            s.OnAction(UiAction.Ok, false);
            var page = rig.Ctx.Nav.State.Pages.Single();
            Assert.Equal("show", page.Name);
            Assert.Equal("The Simpsons", ((CatalogueTitle)page.Params!).Name);
        }

        [AvaloniaFact]
        public async Task A_channel_number_tunes_and_a_film_plays()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            await Type(s, rig, "104");
            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Right, false);
            s.OnAction(UiAction.Ok, false);
            Assert.Equal("ch4", rig.Home.Tuned.Single().Id);

            var s2 = new SearchScreen(rig.Ctx);
            Show(s2.View);
            await Type(s2, rig, "alien");
            for (var i = 0; i < 12; i++) s2.OnAction(UiAction.Right, false);
            s2.OnAction(UiAction.Ok, false);
            Assert.Equal("vod", rig.Ctx.Nav.State.Video!.Kind);
            Assert.Equal("alien", rig.Ctx.Playback.Current!.ItemId);
        }

        [AvaloniaFact]
        public async Task A_title_you_cannot_watch_opens_its_page_to_request_it()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            await Type(s, rig, "severance");
            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Right, false);
            s.OnAction(UiAction.Ok, false);
            Assert.Equal("Severance", ((CatalogueTitle)rig.Ctx.Nav.State.Pages.Single().Params!).Name);
        }

        [AvaloniaFact]
        public async Task Results_stay_in_their_own_column_and_the_lit_one_is_always_wholly_on_screen()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            await Type(s, rig, "simpsons");
            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Right, false);
            var keyboardRight = 96 + 700;
            for (var step = 0; step < 4; step++)
            {
                await Wait(600);
                var lit = s.LitElement!;
                var m = lit.TransformToVisual(s.View)!.Value;
                var tl = new Point(0, 0).Transform(m);
                var br = new Point(lit.Bounds.Width, lit.Bounds.Height).Transform(m);
                Assert.True(tl.X > keyboardRight, $"step {step}: left {tl.X}");
                Assert.InRange(tl.Y, 64, 1030);
                Assert.InRange(br.Y, 64, 1030);
                s.OnAction(UiAction.Down, false);
            }
        }

        [AvaloniaFact]
        public void Up_off_the_keyboard_and_left_off_the_results_are_handled_as_designed()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            Assert.True(s.OnAction(UiAction.Up, false));  // letters → digits row
            Assert.False(s.OnAction(UiAction.Up, false)); // off the top: the shell lights the tab bar
        }

        [AvaloniaFact]
        public void A_tap_of_voice_says_how_to_use_it()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Assert.True(s.OnAction(UiAction.Voice, false));
            Assert.Contains("Hold the remote's voice button", rig.Home.Toasts.Single());
        }

        // ------------------------------------------------------------------ the show page
        private static async Task<(ShowPage Page, Rig Rig)> Simpsons()
        {
            var rig = Build();
            var r = await rig.World.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow);
            var page = new ShowPage(rig.Ctx, r.Top!.Title!);
            await page.Loaded; await page.OnTvLoaded;
            Dispatcher.UIThread.RunJobs();
            Show(page.View);
            return (page, rig);
        }

        [AvaloniaFact]
        public async Task The_show_page_offers_play_request_and_watchlist_and_its_seasons()
        {
            var (page, _) = await Simpsons();
            Assert.Equal(new[] { "Play S1 E1", "Request…", "On TV now · Channel 4", "Watchlist" }, page.ButtonLabels);
            Assert.Equal(4, page.Details!.Seasons.Count);
            Assert.Equal("Play S1 E1", ((PillButton)page.LitElement!).Label);
        }

        [AvaloniaFact]
        public async Task Play_plays_the_episode_and_back_returns_to_the_page()
        {
            var (page, rig) = await Simpsons();
            rig.Ctx.OpenShow(page.Details!.Title);
            page.OnAction(UiAction.Ok, false);
            Assert.Equal("s1e1", rig.Ctx.Playback.Current!.ItemId);
            Assert.True(rig.Ctx.Nav.State.AtVideo);
            rig.Ctx.Nav.Back();
            Assert.Equal("show", rig.Ctx.Nav.State.Pages.Single().Name);
        }

        [AvaloniaFact]
        public async Task Season_tabs_switch_the_episodes_as_you_move_across_them()
        {
            var (page, _) = await Simpsons();
            page.OnAction(UiAction.Down, false); // the season tabs
            Assert.IsType<SeasonTab>(page.LitElement);
            page.OnAction(UiAction.Right, false);
            Assert.Equal(1, page.SeasonIndex);
            page.OnAction(UiAction.Down, false); // season 2's episodes
            var ep = (Card)page.LitElement!;
            Assert.Equal("1. Bart Gets an F", ep.Data.Label);
            page.OnAction(UiAction.Up, false);
            page.OnAction(UiAction.Right, false); // season 3: downloading
            page.OnAction(UiAction.Down, false);
            Assert.Equal("dl:40", ((Card)page.LitElement!).Data.Badge);
        }

        [AvaloniaFact]
        public async Task A_lit_episode_is_wholly_on_screen()
        {
            var (page, _) = await Simpsons();
            page.OnAction(UiAction.Down, false);
            page.OnAction(UiAction.Down, false);
            page.OnAction(UiAction.Right, false);
            await Wait(700);
            var lit = page.LitElement!;
            var m = lit.TransformToVisual(page.View)!.Value;
            Assert.InRange(new Point(0, 0).Transform(m).Y, 64, 1030);
            Assert.InRange(new Point(0, lit.Bounds.Height).Transform(m).Y, 64, 1030);
        }

        [AvaloniaFact]
        public async Task Request_opens_a_sheet_and_choosing_asks_seerr_and_says_so()
        {
            var (page, rig) = await Simpsons();
            rig.Ctx.OpenShow(page.Details!.Title);
            page.OnAction(UiAction.Right, false); // Request…
            page.OnAction(UiAction.Ok, false);
            var list = (ChoiceList)rig.Ctx.Nav.State.Overlay!.Params!;
            Assert.Equal(new[] { "Season 4" }, list.Choices.Select(c => c.Label));
            var sheet = new ChoiceSheet(rig.Ctx, list);
            sheet.OnAction(UiAction.Ok, false);
            await Wait(50);
            await page.Loaded;
            Assert.Null(rig.Ctx.Nav.State.Overlay);
            Assert.Equal(new[] { 4 }, rig.World.Seerr.Requests.Single().Seasons);
            Assert.Contains("Requested The Simpsons, season 4", rig.Home.Toasts.Last());
        }

        [AvaloniaFact]
        public async Task Ok_on_a_missing_episode_offers_its_season_saying_seerr_requests_whole_seasons()
        {
            var (page, rig) = await Simpsons();
            page.OnAction(UiAction.Down, false);
            for (var i = 0; i < 3; i++) page.OnAction(UiAction.Right, false); // season 4
            page.OnAction(UiAction.Down, false);
            page.OnAction(UiAction.Ok, false);
            var list = (ChoiceList)rig.Ctx.Nav.State.Overlay!.Params!;
            var first = list.Choices[0];
            Assert.Equal("Season 4, for episode 1", first.Label);
            Assert.Contains("whole seasons", first.Hint);
        }

        [AvaloniaFact]
        public async Task A_film_only_in_seerr_offers_request_and_a_requested_one_its_percentage()
        {
            var rig = Build();
            rig.World.Seerr.Status[999] = new Baird.Services.JellyseerrMediaStatus(3, Array.Empty<Baird.Services.JellyseerrSeason>(), new[] { new Baird.Services.JellyseerrDownload(null, null, 100, 25) });
            var movie = new ShowPage(rig.Ctx, ShowDataFor("tmdb:movie:999", "The Simpsons Movie", "requested"));
            await movie.Loaded;
            Assert.Equal("Requested · 75%", movie.ButtonLabels[0]);

            var sev = new ShowPage(rig.Ctx, ShowDataFor("tmdb:tv:95396", "Severance", "get", CardKind.Series));
            await sev.Loaded;
            Assert.Equal(new[] { "Request…" }, sev.ButtonLabels);
        }

        private static CatalogueTitle ShowDataFor(string id, string name, string badge, CardKind kind = CardKind.Film) =>
            Baird.NewUi.Adapters.ShowData.FromCard(new HomeCard(kind, id, name, null, null, null, badge, "Seerr", PlayVia.None));

        // ------------------------------------------------------------------ slow sources (Jellyfin 12)
        private static (HomeScreenTests.Rig Rig, CatalogueTests.Gated Jellyfin) SlowRig()
        {
            var home = HomeScreenTests.Context();
            var (search, shows, jf, _) = CatalogueTests.Slow();
            home.Ctx.Search = search; home.Ctx.Shows = shows; home.Ctx.Channels = CatalogueTests.Channels; home.Ctx.Timers = home.Timers;
            return (home, jf);
        }

        private static async Task Until(Func<bool> condition)
        {
            for (var i = 0; i < 200 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Assert.True(condition());
        }

        [AvaloniaFact]
        public async Task Search_shows_fast_sources_first_checks_the_slow_library_and_keeps_the_lit_result_lit()
        {
            var (rig, jf) = SlowRig();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            foreach (var ch in "loki") TypeLetter(s, ch);
            rig.Timers.Advance(SearchScreen.Debounce);
            var run = s.RunAsync();
            await Until(() => s.ResultRows.SelectMany(r => r).Any(h => h.Title?.Name == "Loki") && s.ResultRows.SelectMany(r => r).Any(h => h.Title?.Name == "Loki Odyssey"));
            var loki = s.ResultRows.SelectMany(r => r).Single(h => h.Title?.Name == "Loki");
            Assert.EndsWith("Checking your library…", loki.Card.Sublabel);
            Assert.Null(loki.Card.Badge);

            // Light Loki Odyssey (iPlayer, already in), then let Jellyfin answer: Loki Odyssey stays lit.
            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Right, false);
            for (var i = 0; i < 6 && ((Card)s.LitElement!).Data.Label != "Loki Odyssey"; i++) s.OnAction(UiAction.Down, false);
            Assert.Equal("Loki Odyssey", ((Card)s.LitElement!).Data.Label);
            jf.Gate.SetResult();
            await run;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Loki Odyssey", ((Card)s.LitElement!).Data.Label);
            var found = s.ResultRows.SelectMany(r => r).Single(h => h.Title?.Name == "Loki").Title!;
            Assert.Equal("loki", found.Best!.ItemId);
        }

        [AvaloniaFact]
        public async Task A_show_page_opened_while_the_library_is_checking_says_so_then_offers_play()
        {
            var (rig, jf) = SlowRig();
            var page = new ShowPage(rig.Ctx, ShowDataFor("tmdb:tv:84958", "Loki", "", CardKind.Series));
            Show(page.View);
            await Until(() => page.Details != null);
            Assert.Equal(new[] { "Checking your library…" }, page.ButtonLabels);
            jf.Gate.SetResult();
            await page.Loaded;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Play S1 E1", page.ButtonLabels[0]);
        }

        [AvaloniaFact]
        public async Task A_netflix_result_plays_in_the_new_interface()
        {
            var rig = Build();
            rig.World.IPlayer.Items.Add(CatalogueTests.Item("nf-1", "Wednesday", source: "Netflix", stream: "https://www.netflix.com/watch/1"));
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            await Type(s, rig, "wednesday");
            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Right, false);
            s.OnAction(UiAction.Ok, false);
            Assert.Equal("https://www.netflix.com/watch/1", rig.Home.Engine.Plays.Single().Url);
            Assert.Empty(rig.Home.Toasts);
        }

        // ------------------------------------------------------------------ actions, the guide, back chips
        private static async Task<(SearchScreen S, Rig Rig, List<(string Id, DateTimeOffset At)> Guide)> Searched(string q)
        {
            var rig = Build();
            var guide = new List<(string, DateTimeOffset)>();
            rig.Ctx.OpenGuide = (id, at) => guide.Add((id, at));
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            await Type(s, rig, q);
            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Right, false);
            return (s, rig, guide);
        }

        [AvaloniaFact]
        public async Task Hold_ok_on_a_title_or_a_channel_opens_its_actions()
        {
            var (s, rig, _) = await Searched("simpsons");
            s.OnAction(UiAction.HoldOk, false);
            var o = rig.Ctx.Nav.State.Overlay!;
            Assert.Equal("itemactions", o.Name);
            Assert.Equal("The Simpsons", ((HomeCard)o.Params!).Label);
            Assert.Empty(rig.Ctx.Nav.State.Pages);

            var (s2, rig2, _) = await Searched("104");
            s2.OnAction(UiAction.HoldOk, false);
            Assert.Equal("ch4", ((HomeCard)rig2.Ctx.Nav.State.Overlay!.Params!).Channel!.Id);
        }

        [AvaloniaFact]
        public async Task A_later_listing_opens_the_guide_on_it_and_hold_ok_offers_watch_or_the_guide()
        {
            var (s, rig, guide) = await Searched("hollyoaks");
            var hit = s.ResultRows[0][0];
            Assert.Equal(HitKind.Listing, hit.Kind);
            s.OnAction(UiAction.Ok, false);
            Assert.Equal(("ch4", hit.Card.Programme!.Start), guide.Single());
            Assert.Empty(rig.Home.Tuned);

            s.OnAction(UiAction.HoldOk, false);
            var list = (ChoiceList)rig.Ctx.Nav.State.Overlay!.Params!;
            Assert.Equal(new[] { "Show it in the guide" }, list.Choices.Select(c => c.Label)); // not on yet: nothing to watch

            var (s2, rig2, _) = await Searched("simpsons");
            for (var i = 0; i < 4; i++) s2.OnAction(UiAction.Down, false); // the On TV row: on now
            s2.OnAction(UiAction.HoldOk, false);
            var now = (ChoiceList)rig2.Ctx.Nav.State.Overlay!.Params!;
            Assert.Equal(new[] { "Watch now on Channel 4", "Show it in the guide" }, now.Choices.Select(c => c.Label));
        }

        [AvaloniaFact]
        public async Task Nothing_found_says_so_in_the_results_column()
        {
            var (s, _, _) = await Searched("zzzz");
            Assert.True(s.EmptyMessage.IsVisible);
            Assert.Contains("Nothing called “zzzz”", s.EmptyMessage.Text);
            await Wait(50);
            var x = new Point(0, 0).Transform(s.EmptyMessage.TransformToVisual(s.View)!.Value).X;
            Assert.True(x > 96 + 700, $"{x}");
        }

        [AvaloniaFact]
        public async Task The_show_page_says_when_it_is_on_tv_and_opens_the_guide_on_it()
        {
            var (page, rig) = await Simpsons();
            var guide = new List<(string, DateTimeOffset)>();
            rig.Ctx.OpenGuide = (id, at) => guide.Add((id, at));
            page.OnAction(UiAction.Right, false);
            page.OnAction(UiAction.Right, false);
            Assert.Equal("On TV now · Channel 4", ((PillButton)page.LitElement!).Label);
            page.OnAction(UiAction.Ok, false);
            Assert.Equal("ch4", guide.Single().Item1);
        }

        [AvaloniaFact]
        public async Task Hold_ok_on_the_show_pages_buttons_opens_the_titles_actions_without_reopening_the_page()
        {
            var (page, rig) = await Simpsons();
            rig.Ctx.OpenShow(page.Details!.Title);
            page.OnAction(UiAction.HoldOk, false);
            var o = rig.Ctx.Nav.State.Overlay!;
            Assert.Equal("itemactions", o.Name);
            var actions = new ItemActions(rig.Ctx, (HomeCard)o.Params!);
            Assert.DoesNotContain("Open the page", actions.Items);
            Assert.Contains("Add to watchlist", actions.Items);
        }

        [AvaloniaFact]
        public async Task Back_from_what_you_play_on_a_show_page_names_the_show()
        {
            var (page, rig) = await Simpsons();
            var chips = new List<string>();
            rig.Ctx.FlashBackChip = chips.Add;
            rig.Ctx.OpenShow(page.Details!.Title);
            page.OnAction(UiAction.Ok, false);
            Assert.Equal("The Simpsons", chips.Single());
        }

        [AvaloniaFact]
        public void Series_and_seerr_cards_on_home_open_their_page_instead_of_a_toast()
        {
            var rig = Build();
            rig.Ctx.Activate(new HomeCard(CardKind.Series, "simp", "The Simpsons", null, null, null, null, "Jellyfin: nas", PlayVia.Mpv));
            Assert.Equal("The Simpsons", ((CatalogueTitle)rig.Ctx.Nav.State.Pages.Single().Params!).Name);
            rig.Ctx.Nav.Back();
            rig.Ctx.Activate(new HomeCard(CardKind.Series, "tmdb:tv:7", "Andor", "2022", null, null, "get", "Seerr", PlayVia.None));
            var t = (CatalogueTitle)rig.Ctx.Nav.State.Pages.Single().Params!;
            Assert.Equal(7, t.Seerr!.TmdbId);
            Assert.Empty(rig.Home.Toasts);
        }

        [AvaloniaFact]
        public async Task A_real_keyboard_types_into_the_query_and_backspace_deletes()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            Assert.True(s.AcceptsText);
            s.TypeText("A"); s.TypeText("l"); s.TypeText("i"); s.TypeText("x");
            s.TypeText(null);
            Assert.Equal("Ali", s.Query);
            rig.Home.Timers.Advance(SearchScreen.Debounce);
            await s.RunAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Alien", s.ResultRows[0][0].Title!.Name);
        }

        [AvaloniaFact]
        public void Typing_while_a_result_is_lit_goes_back_to_the_query()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);
            s.SetQuery("al");
            Dispatcher.UIThread.RunJobs();
            s.OnAction(UiAction.Right, false);
            s.TypeText("i");
            Assert.Equal("ali", s.Query);
        }

        [AvaloniaFact]
        public void The_caret_sits_right_after_the_last_letter_and_before_the_hint_when_empty()
        {
            var rig = Build();
            var s = new SearchScreen(rig.Ctx);
            Show(s.View);

            static double X(Control c, Control root) => c.TranslatePoint(new Point(0, 0), root)!.Value.X;

            Assert.True(X(s.Caret, s.View) < X(s.QueryText, s.View)); // empty: caret, then the hint

            s.TypeText("Alien");
            Dispatcher.UIThread.RunJobs();
            var textEnd = X(s.QueryText, s.View) + s.QueryText.Bounds.Width;
            var gap = X(s.Caret, s.View) - textEnd;
            Assert.InRange(gap, 0, 3); // no space-sized gap between the last letter and the caret
        }
    }
}
