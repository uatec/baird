using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Baird.NewUi.Adapters;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>The channel list over live video (prototype channels.js) and the near-end card's words (nearend.js).</summary>
    public class ChannelListTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 10, 26, 18, 10, 0, TimeSpan.Zero);
        private static Programme P(string id, string ch, int fromMin, int toMin, string? image = null, bool noInfo = false) =>
            new(id, ch, $"Show {id}", null, null, T0.AddMinutes(fromMin), T0.AddMinutes(toMin), noInfo, image);

        [Fact]
        public void The_timeline_is_the_next_three_hours_to_scale_starting_with_whats_on_now()
        {
            var schedule = new[] { P("a", "c", -10, 20), P("b", "c", 20, 25), P("c", "c", 25, 80), P("d", "c", 80, 200), P("e", "c", 200, 260) };
            var segs = ChannelTimeline.Segments(schedule, T0);
            Assert.Equal(new[] { "a", "b", "c", "d" }, segs.Select(s => s.Programme.Id));
            Assert.True(segs[0].IsNow);
            Assert.False(segs[1].IsNow);
            Assert.Equal(20 / 180.0, segs[0].Share, 3); // only what's left of it
            Assert.Equal(5 / 180.0, segs[1].Share, 3);  // a five-minute bulletin looks like one
            Assert.Equal(100 / 180.0, segs[3].Share, 3); // cut at three hours
        }

        [Fact]
        public void The_timeline_stops_at_a_stretch_with_no_information()
        {
            var segs = ChannelTimeline.Segments(new[] { P("a", "c", -10, 20), P("x", "c", 20, 600, noInfo: true) }, T0);
            Assert.Equal(2, segs.Count);
            Assert.True(segs[1].Programme.NoInfo);
        }

        private static (ChannelList List, HomeScreenTests.Rig Rig, List<Channel> Channels) Open(int count, int watching = 0)
        {
            var rig = HomeScreenTests.Context();
            var channels = Enumerable.Range(0, count).Select(i => new Channel($"c{i}", (101 + i).ToString(), $"Channel {i}", null, $"http://tvh/{i}")).ToList();
            rig.Ctx.Channels = channels;
            rig.Ctx.PlayLive(channels[watching]);
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("channels"));
            var now = rig.Ctx.Now();
            var start = GuideData.WindowStartFor(now);
            var model = new GuideModel(start, start + GuideData.Ahead, channels.Select((c, i) =>
                new GuideChannel(c, GuideData.Fill(c.Id, new[]
                {
                    new Programme($"{c.Id}-now", c.Id, $"Now on {i}", null, null, now.AddMinutes(-5), now.AddMinutes(25), ImageUrl: i == 1 ? "http://img/x.jpg" : null),
                    new Programme($"{c.Id}-next", c.Id, $"Next on {i}", null, null, now.AddMinutes(25), now.AddMinutes(85)),
                }, start, start + GuideData.Ahead))).ToList());
            var list = new ChannelList(rig.Ctx, rig.Timers, null, _ => Task.FromResult(model));
            new Window { Width = 1920, Height = 1080, Content = list.View }.Show();
            return (list, rig, channels);
        }

        [AvaloniaFact]
        public void Rows_show_whats_on_with_a_timeline_and_a_thumbnail()
        {
            var (list, _, _) = Open(5);
            var row0 = list.RowOf("c0")!;
            Assert.Equal("Now on 0", row0.Now!.Title);
            Assert.True(row0.Segments.Count >= 2);
            Assert.True(row0.Segments[0].IsNow);
            Assert.Empty(row0.Thumb.GetVisualDescendants().OfType<ArtImage>());  // no picture: the channel's colours
            Assert.Single(list.RowOf("c1")!.Thumb.GetVisualDescendants().OfType<ArtImage>()); // the programme's picture
        }

        [AvaloniaFact]
        public void Moving_slides_one_highlight_and_keeps_the_rows()
        {
            var (list, _, _) = Open(5);
            var before = list.Rows.ToList();
            var hl = list.LitElement;
            list.OnAction(UiAction.Down, false);
            Assert.Same(hl, list.LitElement);
            Assert.All(before, r => Assert.Contains(r, list.Rows)); // nothing rebuilt
            Assert.Equal("c1", Assert.Single(list.Rows, r => r.Lit).Channel.Id);
        }

        [AvaloniaFact]
        public void Only_rows_around_the_visible_window_exist()
        {
            var (list, _, _) = Open(150);
            Assert.InRange(list.Rows.Count, 1, ChannelList.Visible + 2);
            for (var i = 0; i < 60; i++) list.OnAction(UiAction.Down, false);
            Assert.InRange(list.Rows.Count, 1, 2 * (ChannelList.Visible + 2));
            Assert.Equal("c60", Assert.Single(list.Rows, r => r.Lit).Channel.Id);
        }

        [AvaloniaFact]
        public void Resting_tunes_and_moves_the_watching_mark()
        {
            var (list, rig, channels) = Open(5);
            list.OnAction(UiAction.Down, false);
            rig.Timers.Advance(ChannelList.Rest);
            Assert.Equal(channels[1], rig.Ctx.Playback.Current!.Channel);
        }

        [Fact]
        public void The_near_end_card_says_when_and_what_then()
        {
            var ch = new Channel("c", "101", "BBC One", null, "u");
            var (eyebrow, title, then) = LiveUpNext.Words(ch, P("n", "c", -25, 4), P("x", "c", 4, 34), P("y", "c", 34, 64), T0.AddSeconds(20));
            Assert.Equal("Up next on BBC One · in 4 min", eyebrow);
            Assert.Equal($"{T0.AddMinutes(4).ToLocalTime():HH:mm}  Show x", title);
            Assert.Equal($"then {T0.AddMinutes(34).ToLocalTime():HH:mm} Show y", then);
            Assert.Null(LiveUpNext.Words(ch, P("n", "c", -25, 4), P("x", "c", 4, 34), null, T0).Then);
        }
    }
}
