using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Baird.NewUi.Core;
using Baird.NewUi.Screens;
using Baird.NewUi.Shell;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>≡ (Guide) and voice, as the prototype's app.js has them, and places switching in one step.</summary>
    public class ShellGuideKeyTests
    {
        private sealed class Recording : IScreen
        {
            public readonly string Name;
            public readonly List<UiAction> Actions = new();
            public int FocusIns;
            public Control View { get; }
            public Recording(string name) { Name = name; View = new Border { Width = 1920, Height = 1080 }; }
            public bool OnAction(UiAction a, bool repeat) { Actions.Add(a); return a != UiAction.Back && a != UiAction.Up; }
            public void OnFocusIn() => FocusIns++;
        }

        private sealed record Rig(Shell Shell, Navigator Nav, Dictionary<string, Recording> Screens, List<Place> PlacesSeen);

        private static Rig Build()
        {
            var nav = new Navigator();
            var screens = new Dictionary<string, Recording>();
            Recording Make(string n) => screens[n] = new Recording(n);
            var shell = new Shell(new ShellDeps(new Border(), nav, p => Make(p.ToString()), pr => Make("page:" + pr.Name), o => Make("overlay:" + o.Name), (_, _) => true, () => "BBC One"));
            var w = new Window { Width = 1920, Height = 1080, Content = shell };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            var seen = new List<Place>();
            nav.Changed += () => seen.Add(nav.State.Place);
            return new Rig(shell, nav, screens, seen);
        }

        [Fact]
        public void Going_to_a_place_resets_the_stack_in_one_change_and_keeps_the_video()
        {
            var n = new Navigator();
            n.SetPlace(Place.Search); n.OpenPage(new PageRef("show")); n.Play(new VideoRef("live", "bbc1")); n.OpenOverlay(new OverlayRef("info"));
            var changes = 0;
            n.Changed += () => changes++;
            n.GoPlace(Place.LiveTv);
            Assert.Equal(1, changes);
            var st = n.State;
            Assert.Equal((Place.LiveTv, 0, 0, false, (OverlayRef?)null), (st.Place, st.Pages.Count, st.OriginPages.Count, st.AtVideo, st.Overlay));
            Assert.NotNull(st.Video);
            n.GoPlace(Place.LiveTv);
            Assert.Equal(1, changes); // already there: nothing to tell anyone
        }

        [AvaloniaFact]
        public void Guide_from_another_place_goes_straight_there_without_home_in_between()
        {
            var r = Build();
            r.Nav.SetPlace(Place.Search);
            r.PlacesSeen.Clear();
            r.Shell.OnAction(UiAction.Guide, false);
            Assert.Equal(new[] { Place.LiveTv }, r.PlacesSeen);
            Assert.False(r.Shell.TabFocus);
            Assert.DoesNotContain(UiAction.Guide, r.Screens["LiveTv"].Actions); // arriving isn't pressing ≡ inside it
        }

        [AvaloniaFact]
        public void Guide_while_watching_opens_the_guide_over_the_video_and_back_returns_to_the_picture()
        {
            var r = Build();
            r.Nav.SetPlace(Place.Search);
            r.Nav.OpenPage(new PageRef("show"));
            r.Nav.Play(new VideoRef("live", "ch1")); // origin: Search › show
            r.Nav.OpenOverlay(new OverlayRef("info"));
            r.Shell.OnAction(UiAction.Guide, false);
            var st = r.Nav.State;
            Assert.True(st.AtVideo);
            Assert.Equal("guide", st.Pages.Single().Name);
            Assert.Null(st.Overlay);
            Assert.Equal("show", st.OriginPages.Single().Name);
            Assert.Same(r.Screens["page:guide"], r.Shell.ActiveScreen);
            r.Shell.OnAction(UiAction.Back, false); // the guide page lets Back go: the shell pops it
            st = r.Nav.State;
            Assert.True(st.AtVideo);
            Assert.Empty(st.Pages);
            Assert.Equal("show", st.OriginPages.Single().Name); // the journey back is intact
            Assert.Equal(Place.Search, st.Place);
        }

        [AvaloniaFact]
        public void Guide_inside_the_guide_page_is_the_guides_own_key()
        {
            var r = Build();
            r.Nav.Play(new VideoRef("live", "ch1"));
            r.Shell.OnAction(UiAction.Guide, false);
            var changes = r.PlacesSeen.Count;
            r.Shell.OnAction(UiAction.Guide, false);
            Assert.Equal(changes, r.PlacesSeen.Count);
            Assert.Single(r.Nav.State.Pages);
            Assert.Contains(UiAction.Guide, r.Screens["page:guide"].Actions);
        }

        [AvaloniaFact]
        public void Guide_on_the_guide_place_is_the_guides_own_key_and_takes_focus_from_the_tabs()
        {
            var r = Build();
            r.Nav.SetPlace(Place.LiveTv);
            r.Shell.OnAction(UiAction.Up, false); // the guide fake gives Up away: the tab bar lights
            Assert.True(r.Shell.TabFocus);
            r.PlacesSeen.Clear();
            r.Shell.OnAction(UiAction.Guide, false);
            Assert.Empty(r.PlacesSeen);
            Assert.False(r.Shell.TabFocus);
            Assert.Contains(UiAction.Guide, r.Screens["LiveTv"].Actions);
        }

        [AvaloniaFact]
        public void Voice_on_search_already_there_does_not_go_there_again()
        {
            var r = Build();
            r.Nav.SetPlace(Place.Search);
            var search = r.Screens["Search"];
            var focusIns = search.FocusIns;
            r.PlacesSeen.Clear();
            r.Shell.OnAction(UiAction.Voice, false);
            Assert.Empty(r.PlacesSeen);
            Assert.Equal(focusIns, search.FocusIns); // its focus (e.g. in the results) stays put
            Assert.Contains(UiAction.Voice, search.Actions);

            r.Nav.SetPlace(Place.Settings);
            r.PlacesSeen.Clear();
            r.Shell.OnAction(UiAction.Voice, false);
            Assert.Equal(new[] { Place.Search }, r.PlacesSeen);
        }

        [AvaloniaFact]
        public void Show_place_switches_once_or_not_at_all()
        {
            var r = Build();
            r.Nav.SetPlace(Place.Search);
            r.PlacesSeen.Clear();
            r.Shell.ShowPlace(Place.Search);
            Assert.Empty(r.PlacesSeen);
            r.Shell.ShowPlace(Place.Settings);
            Assert.Equal(new[] { Place.Settings }, r.PlacesSeen);
        }
    }
}
