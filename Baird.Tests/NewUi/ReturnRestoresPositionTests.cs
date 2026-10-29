using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    /// <summary>
    /// The rule behind "Back goes to where I was": leaving a screen (to the tab bar, under a page, under the video) and
    /// coming back never moves the lit element. Only an explicit key (the Home key) resets it. Every screen that keeps a
    /// position is run through every way the shell can hide and re-show it.
    /// </summary>
    public class ReturnRestoresPositionTests
    {
        private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 26, 18, 10, 0, DateTimeKind.Local));

        /// <summary>The ways the shell hides and re-shows a screen: tab bar, covered by a page / the video.</summary>
        private static readonly (string Name, Action<IScreen> Run)[] Departures =
        {
            ("tab bar", s => { s.OnFocusOut(); s.OnFocusIn(); }),
            ("covered then shown", s => s.OnShow()),
            ("tab bar, then covered, then shown", s => { s.OnFocusOut(); s.OnShow(); s.OnFocusIn(); }),
            ("covered twice", s => { s.OnShow(); s.OnShow(); }),
        };

        private static void Flush() => Dispatcher.UIThread.RunJobs();

        private static void Press(IScreen s, params UiAction[] keys) { foreach (var k in keys) s.OnAction(k, false); Flush(); }

        private static void AssertRestored(string screen, IScreen s, Func<string> where, bool moved = true)
        {
            var before = where();
            foreach (var (name, run) in Departures)
            {
                run(s); Flush();
                Assert.True(before == where(), $"{screen}: after '{name}' the lit element moved from {before} to {where()}.");
            }
            Assert.True(moved);
        }

        [AvaloniaFact]
        public async Task Home_keeps_its_position_through_every_departure()
        {
            var rig = HomeScreenTests.Context();
            var home = new HomeScreen(rig.Ctx);
            await home.LoadAsync();
            Show(home.View);
            Press(home, UiAction.Down, UiAction.Right);
            AssertRestored("Home", home, () => ((Card)home.LitElement!).Data.Id);
        }

        [AvaloniaFact]
        public async Task Home_keeps_its_position_through_every_departure_while_something_plays()
        {
            var rig = HomeScreenTests.Context();
            var home = new HomeScreen(rig.Ctx);
            await home.LoadAsync();
            Show(home.View);
            Press(home, UiAction.Ok);                  // play the first card
            rig.Ctx.Nav.Back(); home.OnShow(); Flush(); // Back out of the video: the playing card is lit
            Press(home, UiAction.Down, UiAction.Right);
            AssertRestored("Home (playing)", home, () => ((Card)home.LitElement!).Data.Id);
        }

        [AvaloniaFact]
        public async Task The_guide_keeps_its_position_through_every_departure()
        {
            var engine = new PlaybackControllerTests.FakeEngine();
            var timers = new PlaybackControllerTests.FakeTimers();
            var playback = new PlaybackController(engine, a => a(), timers, () => timers.Now, null);
            var ctx = new NewUiContext(new Navigator(), playback, HomeDataTests.Build().Home, () => Now);
            ctx.SaveGuideLayout = _ => { };
            var model = GuideScreenTests.Model(20, 3);
            var guide = new GuideScreen(ctx, _ => Task.FromResult(model), timers);
            ctx.Nav.SetPlace(Place.LiveTv);
            Show(guide.View);
            await guide.LoadAsync(); guide.OnShow(); Flush();
            Press(guide, UiAction.Down, UiAction.Down, UiAction.Right);
            AssertRestored("Guide", guide, () => guide.Lit.Id);
        }

        [AvaloniaFact]
        public void Settings_keeps_its_position_through_every_departure()
        {
            var s = new SettingsScreen(() => UiMode.New, _ => { }, 1.0, _ => { }, () => GuideLayout.Rows, _ => { });
            Show(s.View);
            Press(s, UiAction.Down, UiAction.Down);
            var lit = s.LitElement;
            AssertRestored("Settings", s, () => ReferenceEquals(s.LitElement, lit) ? "same row" : "another row");
        }

        private static Window Show(Control view)
        {
            var w = new Window { Width = 1920, Height = 1080, Content = view };
            w.Show(); Flush();
            return w;
        }
    }
}
