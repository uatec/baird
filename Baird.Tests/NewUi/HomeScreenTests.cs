using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Baird.NewUi;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Screens;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class HomeScreenTests
    {
        internal sealed record Rig(NewUiContext Ctx, PlaybackControllerTests.FakeEngine Engine, PlaybackControllerTests.FakeTimers Timers, List<string> Toasts, List<Channel> Tuned);

        internal static Rig Context(HomeDataTests.Seerr? seerr = null, HomeDataTests.Watchlist? watchlist = null)
        {
            var engine = new PlaybackControllerTests.FakeEngine();
            var timers = new PlaybackControllerTests.FakeTimers();
            var playback = new PlaybackController(engine, a => a(), timers, () => timers.Now, null);
            var ctx = new NewUiContext(new Navigator(), playback, HomeDataTests.Build(seerr: seerr, watchlist: watchlist).Home, () => DateTimeOffset.UtcNow);
            var toasts = new List<string>();
            var tuned = new List<Channel>();
            ctx.Toast = toasts.Add;
            ctx.Tuning += tuned.Add;
            return new Rig(ctx, engine, timers, toasts, tuned);
        }

        private static async Task<(HomeScreen Home, Rig Rig, Window Window)> Shown(HomeDataTests.Seerr? seerr = null, HomeDataTests.Watchlist? watchlist = null)
        {
            var rig = Context(seerr, watchlist);
            var home = new HomeScreen(rig.Ctx);
            await home.LoadAsync();
            var w = new Window { Width = 1920, Height = 1080, Content = home.View };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            return (home, rig, w);
        }

        private static async Task Wait(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); } }

        private static IEnumerable<Card> Cards(Control root) => root.GetLogicalDescendants().OfType<Card>();

        [AvaloniaFact]
        public async Task Adding_to_the_watchlist_updates_the_Home_row_as_it_happens()
        {
            var list = new HomeDataTests.Watchlist();
            var (home, _, _) = await Shown(watchlist: list);
            int Count() => home.CurrentRows().First(r => r.Title == "Your watchlist").Cards.Count;
            Assert.Equal(1, Count());
            await list.AddAsync("ep1");
            await Wait(300);
            Assert.Equal(2, Count());
            await list.RemoveAsync("ep1");
            await Wait(300);
            Assert.Equal(1, Count());
        }

        [AvaloniaFact]
        public async Task Home_shows_its_rows_with_exactly_one_lit_card()
        {
            var (home, _, _) = await Shown();
            var cards = Cards(home.View).ToList();
            Assert.True(cards.Count >= 5, $"{cards.Count} cards");
            Assert.Single(cards, c => c.Lit);
            Assert.Same(home.LitElement, cards.Single(c => c.Lit));
            Assert.Equal("Casper", ((Card)home.LitElement!).Data.Label); // Continue watching, most recent first
        }

        [AvaloniaFact]
        public async Task The_row_above_peeks_in_dimmed_and_cropped_and_rows_further_up_stay_hidden()
        {
            var (home, _, _) = await Shown();
            var rows = home.View.GetLogicalDescendants().OfType<Avalonia.Controls.Canvas>().First().Children;
            Assert.Null(rows[0].Clip); // on the first row nothing is above
            home.OnAction(UiAction.Down, false);
            await Wait(700);
            Assert.InRange(rows[0].Opacity, 0.2, 0.9); // the row just above peeks in
            Assert.NotNull(rows[0].Clip);              // cropped to a sliver
            Assert.Equal(1, rows[1].Opacity);          // the lit row is whole
            Assert.Null(rows[1].Clip);
            if (rows.Count > 2 && home.OnAction(UiAction.Down, false))
            {
                await Wait(700);
                Assert.Equal(0, rows[0].Opacity);      // two up: out of sight
                if (rows[1].Opacity > 0) Assert.NotNull(rows[1].Clip); // the row above only peeks where it fits under the hero
            }
        }

        [AvaloniaFact]
        public async Task The_row_above_never_runs_over_the_hero_text()
        {
            var (home, _, _) = await Shown();
            var rows = home.View.GetLogicalDescendants().OfType<Avalonia.Controls.Canvas>().First().Children;
            var hero = home.View.GetLogicalDescendants().OfType<StackPanel>().First(p => p.Width == 900);
            for (var row = 1; row < rows.Count; row++)
            {
                if (!home.OnAction(UiAction.Down, false)) break;
                await Wait(700);
                var heroBottom = hero.TranslatePoint(new Point(0, hero.Bounds.Height), home.View)!.Value.Y;
                for (var i = 0; i < row; i++)
                {
                    if (rows[i].Opacity == 0) continue;
                    var clipTop = rows[i].Clip?.Bounds.Top ?? 0;
                    var top = rows[i].TranslatePoint(new Point(0, clipTop), home.View)!.Value.Y;
                    Assert.True(top >= heroBottom, $"row {i} shows from y={top}, over the hero text that ends at y={heroBottom} (lit row {row})");
                    if (rows[i].Clip is { } clip) Assert.Equal(90, clip.Bounds.Height); // whole or not at all: a part sliver cuts through its labels
                }
            }
        }

        [AvaloniaFact]
        public async Task Up_on_the_first_row_is_left_to_the_shell()
        {
            var (home, _, _) = await Shown();
            Assert.False(home.OnAction(UiAction.Up, false));
        }

        [AvaloniaFact]
        public async Task Ok_on_a_channel_tunes_it_and_goes_to_the_video()
        {
            var (home, rig, _) = await Shown();
            home.OnAction(UiAction.Down, false); // On now
            var card = ((Card)home.LitElement!).Data;
            Assert.Equal(CardKind.Channel, card.Kind);
            home.OnAction(UiAction.Ok, false);
            Assert.Equal("live", rig.Ctx.Playback.Current!.Kind);
            Assert.Equal(card.Channel!.Id, rig.Tuned.Single().Id);
            Assert.True(rig.Ctx.Nav.State.AtVideo);
            rig.Timers.Advance(PlaybackController.TuneDebounce);
            Assert.Equal(card.Channel.StreamUrl, rig.Engine.Plays.Single().Url);
        }

        [AvaloniaFact]
        public async Task Ok_on_something_unfinished_resumes_it()
        {
            var (home, rig, _) = await Shown();
            home.OnAction(UiAction.Ok, false); // Casper, 40 min in
            Assert.Equal(("http://stream/film1", (double?)TimeSpan.FromMinutes(40).TotalSeconds), rig.Engine.Plays.Single());
            Assert.True(rig.Ctx.Nav.State.AtVideo);
        }

        [AvaloniaFact]
        public void Browser_items_play_like_any_other()
        {
            var rig = Context();
            rig.Ctx.Activate(new HomeCard(CardKind.Episode, "nf1", "Stranger Things", null, null, null, null, "Netflix", PlayVia.Browser, "https://www.netflix.com/watch/1"));
            Assert.Equal("https://www.netflix.com/watch/1", rig.Engine.Plays.Single().Url);
            Assert.True(rig.Ctx.Nav.State.AtVideo);
        }

        [AvaloniaFact]
        public async Task A_lit_poster_is_wholly_on_screen()
        {
            var (home, _, w) = await Shown();
            for (var i = 0; i < 3; i++) home.OnAction(UiAction.Down, false); // Discover posters
            await Wait(800);
            var lit = (Card)home.LitElement!;
            Assert.Equal("Seerr", lit.Data.Source);
            var m = lit.TransformToVisual(home.View)!.Value;
            var top = new Point(0, 0).Transform(m).Y;
            var bottom = new Point(0, lit.Bounds.Height).Transform(m).Y;
            Assert.InRange(top, 64, 1030);
            Assert.InRange(bottom, 64, 1030);
        }

        [AvaloniaFact]
        public void Settings_interface_size_steps_by_ten_percent_within_80_to_130()
        {
            var sizes = new List<double>();
            var s = new SettingsScreen(1.25, sizes.Add);
            s.OnAction(UiAction.Right, false);
            s.OnAction(UiAction.Right, false);
            Assert.Equal(new[] { 1.3, 1.3 }, sizes);
        }

        [AvaloniaFact]
        public async Task On_now_stays_current_when_a_programme_changes()
        {
            var (home, rig, _) = await Shown();
            var epg = (Baird.Tests.Mocks.MockEpgService)typeof(Baird.NewUi.Adapters.HomeData).GetField("_epg", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(rig.Ctx.Home)!;
            var now = DateTimeOffset.UtcNow;
            epg.MockSchedules["ch1"] = new System.Collections.Generic.List<Baird.Models.EpgProgram> { new() { Id = "p9", ChannelId = "ch1", Title = "Regional News", StartUnix = now.AddMinutes(-1).ToUnixTimeSeconds(), StopUnix = now.AddMinutes(29).ToUnixTimeSeconds() } };
            home.OnAction(UiAction.Down, false); // lit: On now, first channel
            var litBefore = ((Card)home.LitElement!).Data.Id;
            await home.RefreshOnNowAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(Cards(home.View), c => c.Data.Programme?.Title == "Regional News");
            Assert.Equal(litBefore, ((Card)home.LitElement!).Data.Id);
        }

        [AvaloniaFact]
        public async Task A_programme_changing_remakes_only_its_card_so_the_rest_of_home_doesnt_flicker()
        {
            var (home, rig, _) = await Shown();
            var before = Cards(home.View).ToList();
            var epg = (Baird.Tests.Mocks.MockEpgService)typeof(Baird.NewUi.Adapters.HomeData).GetField("_epg", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(rig.Ctx.Home)!;
            var now = DateTimeOffset.UtcNow;
            epg.MockSchedules["ch1"] = new System.Collections.Generic.List<Baird.Models.EpgProgram> { new() { Id = "p9", ChannelId = "ch1", Title = "Regional News", StartUnix = now.AddMinutes(-1).ToUnixTimeSeconds(), StopUnix = now.AddMinutes(29).ToUnixTimeSeconds() } };
            await home.RefreshOnNowAsync();
            Dispatcher.UIThread.RunJobs();
            var after = Cards(home.View).ToList();
            var changed = after.Single(c => c.Data.Programme?.Title == "Regional News");
            Assert.DoesNotContain(changed, before);                       // the changed card is new…
            Assert.All(after.Where(c => c != changed), c => Assert.Contains(c, before)); // …every other card is the same one, artwork already shown
        }

        [AvaloniaFact]
        public async Task The_home_key_goes_back_to_the_first_card()
        {
            var (home, rig, _) = await Shown();
            home.OnAction(UiAction.Down, false); home.OnAction(UiAction.Right, false);
            home.Reset();
            Assert.Equal("Casper", ((Card)home.LitElement!).Data.Label);
        }

        [AvaloniaFact]
        public async Task Whats_playing_is_the_first_card_and_lit_so_one_ok_goes_back_to_it()
        {
            var (home, rig, _) = await Shown();
            home.OnAction(UiAction.Right, false); home.OnAction(UiAction.Right, false);
            home.OnAction(UiAction.Ok, false);                 // play the third Continue watching card (a video; the second is a channel)
            var playing = rig.Ctx.Playback.Current!.ItemId;
            var plays = rig.Engine.Plays.Count;
            rig.Ctx.Nav.Back();                                 // back to Home over the video
            home.OnShow();
            Dispatcher.UIThread.RunJobs();
            var lit = (Card)home.LitElement!;
            Assert.Equal(playing, lit.Data.Id);
            Assert.Equal("playing", lit.Data.Badge);
            Assert.Single(Cards(home.View), c => c.Data.Id == playing); // not twice
            home.OnAction(UiAction.Ok, false);
            Assert.True(rig.Ctx.Nav.State.AtVideo);
            Assert.Equal(plays, rig.Engine.Plays.Count);        // carried on, not restarted
        }

        [AvaloniaFact]
        public async Task A_channel_being_watched_is_first_too_and_goes_when_playback_stops()
        {
            var (home, rig, _) = await Shown();
            home.OnAction(UiAction.Down, false);
            var ch = ((Card)home.LitElement!).Data.Channel!;
            home.OnAction(UiAction.Ok, false);
            rig.Ctx.Nav.Back();
            home.OnShow();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ch.Id, ((Card)home.LitElement!).Data.Channel?.Id);
            rig.Ctx.Playback.Stop();
            rig.Ctx.Nav.Stop();
            home.OnShow();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(Cards(home.View), c => c.Data.Badge == "playing");
        }

        [AvaloniaFact]
        public async Task Coming_back_to_home_while_something_plays_doesnt_rebuild_or_move_the_highlight_needlessly()
        {
            var (home, rig, _) = await Shown();
            home.OnAction(UiAction.Ok, false);              // play the first Continue watching card
            rig.Ctx.Nav.Back();
            home.OnShow();
            Dispatcher.UIThread.RunJobs();
            home.OnAction(UiAction.Down, false);            // the owner moves on…
            home.OnAction(UiAction.Right, false);
            var lit = ((Card)home.LitElement!).Data.Id;
            var before = Cards(home.View).ToList();
            await home.RefreshOnNowAsync();                 // …and refreshes find nothing new
            await Task.Delay(50); Dispatcher.UIThread.RunJobs();
            Assert.Equal(lit, ((Card)home.LitElement!).Data.Id);
            Assert.Equal(before, Cards(home.View).ToList()); // same card objects: not rebuilt
        }

        [AvaloniaFact]
        public async Task Coming_back_from_a_page_while_something_plays_keeps_the_card_you_left_on()
        {
            var (home, rig, _) = await Shown();
            home.OnAction(UiAction.Ok, false);              // play the first Continue watching card
            rig.Ctx.Nav.Back();                             // Home over the video: the playing card is lit
            home.OnShow();
            Dispatcher.UIThread.RunJobs();
            home.OnAction(UiAction.Down, false);
            home.OnAction(UiAction.Right, false);           // the owner moves to a card in another row…
            var lit = ((Card)home.LitElement!).Data.Id;
            home.OnShow();                                  // …opens a page, and Back shows Home again
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(lit, ((Card)home.LitElement!).Data.Id);
        }

        [AvaloniaFact]
        public async Task A_rebuild_keeps_the_same_card_lit()
        {
            var (home, rig, _) = await Shown();
            home.OnAction(UiAction.Down, false);
            home.OnAction(UiAction.Right, false);
            var lit = ((Card)home.LitElement!).Data.Id;
            home.Build(home.CurrentRows());
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(lit, ((Card)home.LitElement!).Data.Id);
        }

        [AvaloniaFact]
        public async Task Rows_are_in_the_agreed_order()
        {
            var seerr = new HomeDataTests.Seerr
            {
                Movies = { new() { Id = 2, MediaType = "movie", Title = "Heat", PosterPath = "/h.jpg" } },
            };
            seerr.Requests.Add(new JellyseerrRequest { Id = 1, Status = 2, TmdbId = 9, MediaType = "tv", Title = "Severance", CreatedAt = DateTime.UtcNow.ToString("o"), UpdatedAt = DateTime.UtcNow.ToString("o"), MediaInfoStatus = 2 });
            var (home, _, _) = await Shown(seerr);
            await Wait(300);
            Assert.Equal(new[] { "Continue watching", "On now", "Your watchlist", "Downloads", "Discover", "Netflix", "Disney+", "ITVX", "Channel 4", "BBC iPlayer" },
                home.CurrentRows().Select(r => r.Title));
            Assert.Equal("Severance", home.CurrentRows().Single(r => r.Title == "Downloads").Cards.Single().Label);
            Assert.Equal("Casper", ((Card)home.LitElement!).Data.Label); // Home still opens on Continue watching
        }

        [AvaloniaFact]
        public async Task A_service_row_with_no_cards_is_left_out()
        {
            var (home, _, _) = await Shown();
            await Wait(300);
            var titles = home.CurrentRows().Select(r => r.Title).ToList();
            Assert.DoesNotContain("Netflix", titles);
            Assert.DoesNotContain("Downloads", titles);
            Assert.Equal(new[] { "Continue watching", "On now", "Your watchlist", "Discover" }, titles);
        }

        [AvaloniaFact]
        public async Task Downloads_refreshes_in_the_background_and_a_new_request_comes_first()
        {
            var seerr = new HomeDataTests.Seerr();
            seerr.Requests.Add(new JellyseerrRequest { Id = 1, Status = 2, TmdbId = 9, MediaType = "tv", Title = "Severance", CreatedAt = DateTime.UtcNow.AddDays(-1).ToString("o"), UpdatedAt = DateTime.UtcNow.ToString("o"), MediaInfoStatus = 2 });
            var (home, _, _) = await Shown(seerr);
            await Wait(300);
            Assert.Equal(new[] { "Severance" }, home.CurrentRows().Single(r => r.Title == "Downloads").Cards.Select(c => c.Label));

            seerr.Requests.Add(new JellyseerrRequest { Id = 2, Status = 1, TmdbId = 10, MediaType = "movie", Title = "Heat", CreatedAt = DateTime.UtcNow.ToString("o"), UpdatedAt = DateTime.UtcNow.ToString("o"), MediaInfoStatus = 2 });
            home.DownloadsEvery = TimeSpan.Zero;
            home.RefreshInBackground(); // what the timer does while Home is up
            await Wait(300);
            Assert.Equal(new[] { "Heat", "Severance" }, home.CurrentRows().Single(r => r.Title == "Downloads").Cards.Select(c => c.Label));
        }

        [AvaloniaFact]
        public async Task A_failed_refresh_leaves_Downloads_where_it_is()
        {
            var seerr = new HomeDataTests.Seerr();
            seerr.Requests.Add(new JellyseerrRequest { Id = 1, Status = 2, TmdbId = 9, MediaType = "tv", Title = "Severance", CreatedAt = DateTime.UtcNow.AddDays(-1).ToString("o"), UpdatedAt = DateTime.UtcNow.ToString("o"), MediaInfoStatus = 2 });
            var (home, _, _) = await Shown(seerr);
            await Wait(300);
            var before = Cards(home.View).ToList();
            var titles = home.CurrentRows().Select(r => r.Title).ToList();

            seerr.RequestsThrow = true;                     // Seerr times out during a refresh: that is not "no downloads"
            home.DownloadsEvery = TimeSpan.Zero;
            home.RefreshInBackground();
            await Wait(300);
            Assert.Equal(titles, home.CurrentRows().Select(r => r.Title));
            Assert.Equal(before, Cards(home.View).ToList()); // nothing was rebuilt
        }

        [AvaloniaFact]
        public async Task A_rebuild_puts_rows_in_place_without_sliding_them_in_from_the_corner()
        {
            var (home, _, _) = await Shown();
            await Wait(300);
            var rows = home.CurrentRows();
            rows[1] = (rows[1].Title, rows[1].Eyebrow, rows[1].Shape, rows[1].Cards.Take(1).ToList()); // something changed: rows are rebuilt
            home.Build(rows);                               // read at once: no time for a transition to have moved anything
            var views = home.View.GetLogicalDescendants().OfType<StackPanel>()
                .Where(p => p.Children.Count == 2 && p.Children[0] is TextBlock t && rows.Any(r => r.Title == t.Text)).ToList();
            Assert.NotEmpty(views);
            foreach (var v in views)
            {
                var y = v.RenderTransform?.Value.M32 ?? 0;
                Assert.True(v.RenderTransform != null && (y != 0 || v.Opacity == 0), $"row {((TextBlock)v.Children[0]).Text} starts at the top-left corner");
            }
        }

        [AvaloniaFact]
        public async Task Going_right_does_not_scroll_the_row_while_the_lit_card_is_still_on_screen()
        {
            var (home, _, _) = await Shown();
            var cards = Enumerable.Range(0, 12).Select(i => new HomeCard(CardKind.Series, $"s{i}", $"Show {i}", null, null, null, null, "Jellyfin: nas", PlayVia.Mpv)).ToList();
            home.Build(new[] { ("Row", "Row", CardShape.Wide, (IReadOnlyList<HomeCard>)cards) });
            home.OnShow();
            double Scrolled() => ((Avalonia.Visual)((Card)home.LitElement!).Parent!).RenderTransform?.Value.M31 ?? 0;
            Assert.Equal(0, Scrolled());
            home.OnAction(UiAction.Right, false); home.OnAction(UiAction.Right, false); home.OnAction(UiAction.Right, false); // the fourth card
            await Task.Delay(700); Dispatcher.UIThread.RunJobs();       // the row's slide has finished (if it were going to slide)
            Assert.Equal(0, Scrolled());                                // it is well within the screen: before, the row slid from the third card on
            for (var i = 0; i < 8; i++) home.OnAction(UiAction.Right, false); // the twelfth
            await Task.Delay(700); Dispatcher.UIThread.RunJobs();
            Assert.True(Scrolled() < 0);                                // now it has had to move
        }
    }
}
