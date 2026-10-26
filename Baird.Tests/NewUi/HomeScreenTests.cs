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

        internal static Rig Context()
        {
            var engine = new PlaybackControllerTests.FakeEngine();
            var timers = new PlaybackControllerTests.FakeTimers();
            var playback = new PlaybackController(engine, a => a(), timers, () => timers.Now, null);
            var ctx = new NewUiContext(new Navigator(), playback, HomeDataTests.Build().Home, () => DateTimeOffset.UtcNow);
            var toasts = new List<string>();
            var tuned = new List<Channel>();
            ctx.Toast = toasts.Add;
            ctx.Tuning += tuned.Add;
            return new Rig(ctx, engine, timers, toasts, tuned);
        }

        private static async Task<(HomeScreen Home, Rig Rig, Window Window)> Shown()
        {
            var rig = Context();
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
        public async Task Home_shows_its_rows_with_exactly_one_lit_card()
        {
            var (home, _, _) = await Shown();
            var cards = Cards(home.View).ToList();
            Assert.True(cards.Count >= 5, $"{cards.Count} cards");
            Assert.Single(cards, c => c.Lit);
            Assert.Same(home.LitElement, cards.Single(c => c.Lit));
            Assert.Equal("Casper", ((Card)home.LitElement!).Data.Label); // Up next, most recent first
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
        public void Settings_interface_switch_asks_to_switch_to_classic_and_restart()
        {
            var asked = new List<UiMode>();
            var s = new SettingsScreen(() => UiMode.New, asked.Add, 1.0, _ => { });
            s.OnAction(UiAction.Right, false);
            Assert.Equal(UiMode.Classic, asked.Single());
        }

        [AvaloniaFact]
        public void Settings_interface_size_steps_by_ten_percent_within_80_to_130()
        {
            var sizes = new List<double>();
            var s = new SettingsScreen(() => UiMode.New, _ => { }, 1.25, sizes.Add);
            s.OnAction(UiAction.Down, false);
            s.OnAction(UiAction.Right, false);
            s.OnAction(UiAction.Right, false);
            Assert.Equal(new[] { 1.3, 1.3 }, sizes);
        }
    }
}
