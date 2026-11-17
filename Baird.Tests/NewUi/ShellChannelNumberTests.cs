using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Baird.NewUi.Adapters;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Screens;
using Baird.NewUi.Shell;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>Typing a channel number works from any screen that doesn't use digits itself, not only while watching.</summary>
    public class ShellChannelNumberTests
    {
        private sealed class Screen : IScreen
        {
            private readonly bool _takesDigits;
            public Screen(bool takesDigits) => _takesDigits = takesDigits;
            public Control View { get; } = new Border { Width = 1920, Height = 1080 };
            public bool OnAction(UiAction a, bool repeat) => InputMap.DigitOf(a) != null ? _takesDigits : a != UiAction.Back && a != UiAction.Up;
        }

        private static (Shell Shell, Navigator Nav, List<int> Digits) Build(bool screenTakesDigits, bool overlayTakesDigits = false)
        {
            var nav = new Navigator();
            var digits = new List<int>();
            var shell = new Shell(new ShellDeps(new Border(), nav, _ => new Screen(screenTakesDigits), _ => new Screen(screenTakesDigits), _ => new Screen(overlayTakesDigits),
                (_, _) => false, () => "x", ChannelNumber: d => { digits.Add(d); return true; }));
            new Window { Width = 1920, Height = 1080, Content = shell }.Show();
            Dispatcher.UIThread.RunJobs();
            return (shell, nav, digits);
        }

        [AvaloniaFact]
        public void A_digit_on_a_screen_that_does_not_use_digits_starts_a_channel_number()
        {
            var (shell, nav, digits) = Build(screenTakesDigits: false);
            nav.OpenPage(new PageRef("show")); // a show page: nothing there types
            shell.OnAction(UiAction.Digit1, false);
            shell.OnAction(UiAction.Digit0, false);
            Assert.Equal(new[] { 1, 0 }, digits);
        }

        [AvaloniaFact]
        public void Home_and_the_other_places_too()
        {
            var (shell, _, digits) = Build(screenTakesDigits: false);
            shell.OnAction(UiAction.Digit4, false);
            Assert.Equal(new[] { 4 }, digits);
        }

        [AvaloniaFact]
        public void A_screen_that_uses_digits_keeps_them_like_the_search_keyboard_and_the_guide()
        {
            var (shell, _, digits) = Build(screenTakesDigits: true);
            shell.OnAction(UiAction.Digit1, false);
            Assert.Empty(digits);
        }

        [AvaloniaFact]
        public void A_menu_or_dialog_that_is_open_is_not_interrupted_by_digits()
        {
            var (shell, nav, digits) = Build(screenTakesDigits: false, overlayTakesDigits: false);
            nav.OpenOverlay(new OverlayRef("choices"));
            shell.OnAction(UiAction.Digit1, false);
            Assert.Empty(digits);
        }

        [AvaloniaFact]
        public void The_context_opens_the_number_panel_when_there_are_channels_and_not_when_there_are_none()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Channels = new List<Channel>();
            Assert.False(rig.Ctx.TryEnterChannelNumber(1));
            Assert.Null(rig.Ctx.Nav.State.Overlay);

            rig.Ctx.Channels = CatalogueTests.Channels;
            Assert.True(rig.Ctx.TryEnterChannelNumber(1));
            Assert.Equal("number", rig.Ctx.Nav.State.Overlay!.Name);
            Assert.Equal(1, rig.Ctx.Nav.State.Overlay.Params);
        }

        [AvaloniaFact]
        public async System.Threading.Tasks.Task Home_does_not_swallow_digits_before_or_after_it_has_rows()
        {
            var rig = HomeScreenTests.Context();
            var home = new HomeScreen(rig.Ctx);
            Assert.False(home.OnAction(UiAction.Digit1, false)); // no rows yet: it still must not eat the digit
            await home.LoadAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.False(home.OnAction(UiAction.Digit1, false));
        }

        [AvaloniaFact]
        public void A_show_page_does_not_swallow_digits_while_it_loads()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows;
            var page = new ShowPage(rig.Ctx, ShowData.FromCard(new HomeCard(CardKind.Film, "tmdb:movie:348", "Alien", "1979", null, null, null, "Seerr", PlayVia.None)));
            Assert.False(page.OnAction(UiAction.Digit1, false)); // still loading
        }
    }
}
