using System;
using System.Collections.Generic;
using Avalonia.Input;
using Baird.NewUi.Core;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>Ported from spikes/ux-redesign-prototype/tests/nav.test.mjs (same cases, same meaning).</summary>
    public class NavigatorTests
    {
        private static PageRef Page(string n) => new(n);
        private static string TopName(Navigator n) => n.Top switch { PageRef p => p.Name, OverlayRef o => o.Name, Place pl => pl.ToString(), string s => s, _ => "?" };

        [Fact] public void Back_over_the_video_closes_the_overlay()
        {
            var n = new Navigator(); n.Play(new("live", "bbc1")); n.OpenOverlay(new("channels"));
            Assert.Equal("channels", TopName(n)); n.Back(); Assert.Equal("video", n.Top);
        }
        [Fact] public void Back_steps_through_pages_to_the_place()
        {
            var n = new Navigator(); n.OpenPage(Page("search")); n.OpenPage(Page("show"));
            n.Back(); Assert.Equal("search", TopName(n)); n.Back(); Assert.Equal(Place.Home, n.Top);
        }
        [Fact] public void Back_from_the_video_retraces_the_journey()
        {
            var n = new Navigator(); n.OpenPage(Page("search")); n.OpenPage(Page("show")); n.Play(new("vod", "loki"));
            Assert.Equal("video", n.Top);
            n.Back(); Assert.Equal("show", TopName(n)); Assert.NotNull(n.State.Video);
            n.Back(); Assert.Equal("search", TopName(n)); n.Back(); Assert.Equal(Place.Home, n.Top);
        }
        [Fact] public void Back_from_a_video_started_on_home_goes_home_and_keeps_it()
        {
            var n = new Navigator(); n.Play(new("vod", "loki")); n.Back();
            Assert.Equal(Place.Home, n.Top); Assert.NotNull(n.State.Video);
        }
        [Fact] public void Back_on_home_does_nothing()
        {
            var n = new Navigator(); var changes = 0; n.Changed += () => changes++;
            n.Back(); Assert.Equal(Place.Home, n.Top); Assert.Equal(0, changes);
        }
        [Fact] public void Guide_over_the_video_backs_to_the_video()
        {
            var n = new Navigator(); n.Play(new("live", "bbc1")); n.OpenPage(Page("guide")); n.Back(); Assert.Equal("video", n.Top);
        }
        [Fact] public void Home_clears_pages_and_keeps_the_video()
        {
            var n = new Navigator(); n.Play(new("live", "bbc1")); n.OpenPage(Page("guide")); n.Home();
            Assert.Equal(Place.Home, n.Top); Assert.NotNull(n.State.Video); n.Back(); Assert.Equal(Place.Home, n.Top);
        }
        [Fact] public void Actions_over_a_page_close_to_that_page()
        {
            var n = new Navigator(); n.OpenPage(Page("show")); n.OpenOverlay(new("actions")); n.Back(); Assert.Equal("show", TopName(n));
        }
        [Fact] public void Changing_channel_on_the_video_keeps_the_origin()
        {
            var n = new Navigator(); n.OpenPage(Page("search")); n.Play(new("live", "bbc1")); n.Play(new("live", "bbc2"));
            n.Back(); Assert.Equal("search", TopName(n));
        }
        [Fact] public void Stop_returns_to_the_origin_pages()
        {
            var n = new Navigator(); n.OpenPage(Page("search")); n.Play(new("vod", "casper")); n.Stop();
            Assert.Null(n.State.Video); Assert.Equal("search", TopName(n));
        }
        [Fact] public void Places_switch_and_back_goes_home()
        {
            var n = new Navigator(); n.SetPlace(Place.Search); Assert.Equal(Place.Search, n.Top);
            n.SetPlace(Place.LiveTv); n.Back(); Assert.Equal(Place.Home, n.Top);
        }
        [Fact] public void A_page_from_a_place_backs_to_the_place()
        {
            var n = new Navigator(); n.SetPlace(Place.Search); n.OpenPage(Page("show")); n.Back(); Assert.Equal(Place.Search, n.Top);
        }
        [Fact] public void A_video_from_a_place_backs_to_the_place()
        {
            var n = new Navigator(); n.SetPlace(Place.LiveTv); n.Play(new("live", "bbc1")); n.Back();
            Assert.Equal(Place.LiveTv, n.Top); Assert.NotNull(n.State.Video);
        }
        [Fact] public void Play_keep_overlay_keeps_the_channel_list()
        {
            var n = new Navigator(); n.Play(new("live", "bbc1")); var list = new OverlayRef("channels"); n.OpenOverlay(list);
            n.Play(new("live", "bbc2"), keepOverlay: true);
            Assert.Same(list, n.Top); Assert.Equal("bbc2", n.State.Video!.Ref);
        }
    }

    public class FocusMathTests
    {
        [Fact] public void Rows_remember_columns_and_clamp()
        {
            var rows = new[] { 5, 8, 4 }; var mem = new Dictionary<int, int>(); var p = (0, 3);
            p = FocusMath.MoveInRows(rows, p, Dir.Down, mem); Assert.Equal((1, 3), p);
            for (var i = 0; i < 3; i++) p = FocusMath.MoveInRows(rows, p, Dir.Right, mem);
            Assert.Equal((1, 6), p);
            p = FocusMath.MoveInRows(rows, p, Dir.Up, mem); Assert.Equal((0, 3), p);
            p = FocusMath.MoveInRows(rows, p, Dir.Down, mem); Assert.Equal((1, 6), p);
            p = FocusMath.MoveInRows(rows, p, Dir.Down, mem); Assert.Equal((2, 3), p);
        }
        [Fact] public void Edges_stay_put()
        {
            var mem = new Dictionary<int, int>();
            Assert.Equal((0, 0), FocusMath.MoveInRows(new[] { 3 }, (0, 0), Dir.Left, mem));
            Assert.Equal((0, 2), FocusMath.MoveInRows(new[] { 3 }, (0, 2), Dir.Right, mem));
            Assert.Equal((0, 1), FocusMath.MoveInRows(new[] { 3 }, (0, 1), Dir.Up, mem));
        }
    }

    public class InputMapTests
    {
        private readonly InputMap _map = new();
        [Theory]
        [InlineData(Key.Enter, UiAction.Ok)] [InlineData(Key.Escape, UiAction.Back)] [InlineData(Key.Back, UiAction.Back)]
        [InlineData(Key.BrowserBack, UiAction.Back)] [InlineData(Key.H, UiAction.Home)] [InlineData(Key.BrowserHome, UiAction.Home)]
        [InlineData(Key.G, UiAction.Guide)] [InlineData(Key.Apps, UiAction.Guide)] [InlineData(Key.Space, UiAction.PlayPause)]
        [InlineData(Key.MediaPlayPause, UiAction.PlayPause)] [InlineData(Key.MediaPreviousTrack, UiAction.Prev)] [InlineData(Key.MediaNextTrack, UiAction.Next)]
        [InlineData(Key.PageUp, UiAction.PageUp)] [InlineData(Key.PageDown, UiAction.PageDown)] [InlineData(Key.Delete, UiAction.Del)]
        [InlineData(Key.V, UiAction.Voice)] [InlineData(Key.D7, UiAction.Digit7)] [InlineData(Key.NumPad4, UiAction.Digit4)] [InlineData(Key.Up, UiAction.Up)]
        public void Maps(Key key, UiAction expected) => Assert.Equal(expected, _map.Map(key));

        [Fact] public void Unmapped_is_null() => Assert.Null(_map.Map(Key.Q));
        [Fact] public void MediaTvGuide_code_is_guide() => Assert.Equal(UiAction.Guide, _map.Map((Key)InputMap.MediaTvGuideCode));
        [Fact] public void Configured_voice_key() => Assert.Equal(UiAction.Voice, new InputMap((Key)242).Map((Key)242));

        [Fact] public void Hold_detector_short_and_long()
        {
            var t = DateTime.UnixEpoch; var d = new HoldDetector(TimeSpan.FromMilliseconds(500), () => t);
            d.Down(); t = t.AddMilliseconds(200); Assert.Equal(UiAction.Ok, d.Up());
            d.Down(); t = t.AddMilliseconds(520); Assert.Equal(UiAction.HoldOk, d.Tick()); Assert.Null(d.Tick()); Assert.Null(d.Up());
        }

        [Fact] public void Number_entry_matches_and_reports_missing()
        {
            var n = new NumberEntry(new[] { "101", "104" });
            n.Push(1); n.Push(0); var r = n.Push(4); Assert.Equal("104", r.Match); Assert.True(n.IsUnique);
            n.Clear(); n.Push(9); n.Push(9); r = n.Push(9); Assert.Null(r.Match); Assert.Equal("No channel 999", r.Error);
            n.Clear(); n.Push(1); n.Push(0); Assert.Equal("1", n.Del().Text);
        }
    }
}
