using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Baird.NewUi.Core;
using Baird.NewUi.Screens;
using Baird.NewUi.Shell;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ShellTests
    {
        private sealed class FakeScreen : IScreen
        {
            public Control View { get; }
            public bool TopEdge = true;
            public int FocusIns, FocusOuts;
            public FakeScreen(string name) => View = new Border { Name = name, Width = 1920, Height = 1080 };
            public bool OnAction(UiAction a, bool repeat) => !(a == UiAction.Up && TopEdge) && a != UiAction.Back;
            public void OnFocusIn() => FocusIns++;
            public void OnFocusOut() => FocusOuts++;
        }

        private static (Shell Shell, Navigator Nav, Border Video, Window Window) Build()
        {
            var nav = new Navigator();
            var video = new Border { Name = "video" };
            var shell = new Shell(new ShellDeps(video, nav, p => new FakeScreen(p.ToString()), pr => new FakeScreen(pr.Name), o => new FakeScreen(o.Name), (_, _) => true, () => "BBC One"));
            var w = new Window { Width = 1920, Height = 1080, Content = shell };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            return (shell, nav, video, w);
        }

        private static async Task Wait(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); } }
        private static Panel PlaceHost(Shell s) => (Panel)s.Stage.Children[0];
        private static Panel PageHost(Shell s) => (Panel)s.Stage.Children[1];

        [AvaloniaFact]
        public async Task The_video_is_never_re_parented_whatever_the_stack_does()
        {
            var (shell, nav, video, _) = Build();
            nav.SetPlace(Place.Search); nav.OpenPage(new PageRef("show")); nav.Play(new VideoRef("live", "bbc1"));
            nav.OpenOverlay(new OverlayRef("info")); nav.Back(); nav.Back(); nav.Home();
            await Wait(600);
            Assert.Same(video, shell.Children[0]);
            Assert.Same(shell, video.Parent);
        }

        [AvaloniaFact]
        public async Task Switching_places_keeps_the_old_one_until_the_new_one_has_come_in()
        {
            var (shell, nav, _, _) = Build();
            nav.SetPlace(Place.LiveTv);
            Assert.Equal(2, PlaceHost(shell).Children.Count);
            await Wait(600);
            Assert.Single(PlaceHost(shell).Children);
        }

        [AvaloniaFact]
        public void Up_off_the_top_lights_the_tab_bar_then_right_switches_place_without_ok_and_down_enters()
        {
            var (shell, nav, _, _) = Build();
            shell.OnAction(UiAction.Up, false);
            Assert.True(shell.TabFocus);
            shell.OnAction(UiAction.Right, false);
            Assert.Equal(Place.LiveTv, nav.State.Place);
            Assert.True(shell.TabFocus);
            shell.OnAction(UiAction.Down, false);
            Assert.False(shell.TabFocus);
        }

        [AvaloniaFact]
        public void Back_from_inside_a_place_lights_its_tab_then_goes_home()
        {
            var (shell, nav, _, _) = Build();
            nav.SetPlace(Place.Search);
            shell.OnAction(UiAction.Back, false);
            Assert.True(shell.TabFocus);
            Assert.Equal(Place.Search, nav.State.Place);
            shell.OnAction(UiAction.Back, false);
            Assert.Equal(Place.Home, nav.State.Place);
        }

        [AvaloniaFact]
        public async Task Opening_a_page_keeps_the_place_visible_until_the_page_has_arrived()
        {
            var (shell, nav, _, _) = Build();
            nav.OpenPage(new PageRef("show"));
            Assert.True(PlaceHost(shell).IsVisible);
            await Wait(600);
            Assert.False(PlaceHost(shell).IsVisible);
            nav.Back();
            Assert.True(PlaceHost(shell).IsVisible);
        }

        [AvaloniaFact]
        public void Keep_in_view_moves_a_scroller_so_the_lit_card_is_fully_on_screen()
        {
            var stage = new Canvas { Width = 1920, Height = 1080 };
            var scroller = new Canvas { Width = 1920, Height = 2000 };
            var card = new Border { Width = 216, Height = 324 };
            Canvas.SetTop(card, 900);
            scroller.Children.Add(card);
            stage.Children.Add(scroller);
            var w = new Window { Width = 1920, Height = 1080, Content = stage }; w.Show();
            Dispatcher.UIThread.RunJobs();
            KeepInView.Ensure(scroller, card, stage);
            var y = (double)scroller.Tag!;
            Assert.True(900 + y + 324 + 324 * 0.07 + 8 <= 1030.5, $"shifted by {y}");
        }

        private sealed class LeftScreen : IScreen
        {
            public Control View { get; } = new Border { Width = 600, Height = 1080 };
            public (double X, double Y) EnterFrom => (-80, 0);
            public bool OnAction(UiAction a, bool repeat) => true;
        }

        [AvaloniaFact]
        public void An_overlay_arrives_from_its_own_side()
        {
            var nav = new Navigator();
            var left = new LeftScreen();
            var shell = new Shell(new ShellDeps(new Border(), nav, p => new FakeScreen(p.ToString()), pr => new FakeScreen(pr.Name), o => o.Name == "channels" ? left : new FakeScreen(o.Name), (_, _) => true, () => "x"));
            var w = new Window { Width = 1920, Height = 1080, Content = shell }; w.Show();
            nav.Play(new VideoRef("live", "bbc1"));
            nav.OpenOverlay(new OverlayRef("channels"));
            var t = left.View.RenderTransform?.Value;
            Assert.NotNull(t);
            Assert.Equal(-80, t!.Value.M31);
            Assert.Equal(0, t.Value.M32);
        }

        private sealed class TextScreen : IScreen
        {
            public Control View { get; } = new Border { Width = 1920, Height = 1080 };
            public string Typed = "";
            public bool OnAction(UiAction a, bool repeat) => !(a == UiAction.Up);
            public bool AcceptsText => true;
            public void TypeText(string? text) => Typed += text ?? "⌫";
        }

        [AvaloniaFact]
        public void Typed_text_reaches_a_text_screen_but_not_while_its_tab_is_lit()
        {
            var nav = new Navigator();
            var screen = new TextScreen();
            var shell = new Shell(new ShellDeps(new Border(), nav, _ => screen, pr => new FakeScreen(pr.Name), o => new FakeScreen(o.Name), (_, _) => true, () => "BBC One"));
            var w = new Window { Width = 1920, Height = 1080, Content = shell };
            w.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(shell.AcceptsText);
            shell.TypeText("a"); shell.TypeText(null);
            Assert.Equal("a⌫", screen.Typed);

            shell.OnAction(UiAction.Up, false); // off the top: the tab bar lights
            Assert.True(shell.TabFocus);
            Assert.False(shell.AcceptsText);
            shell.TypeText("b");
            Assert.Equal("a⌫", screen.Typed);
        }
    }
}
