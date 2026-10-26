using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>Phase 3: the layers over the video.</summary>
    public class WatchingTests
    {
        private static readonly Channel Bbc1 = new("ch1", "101", "BBC One", null, "http://tvh/101");
        private static readonly Channel Bbc2 = new("ch2", "102", "BBC Two", null, "http://tvh/102");
        private static readonly Channel C4 = new("ch4", "104", "Channel 4", null, "http://tvh/104");

        private static (ChannelList List, HomeScreenTests.Rig Rig) Watching(Channel ch)
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Channels = new[] { Bbc1, Bbc2, C4 };
            rig.Ctx.PlayLive(ch);
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("channels"));
            rig.Tuned.Clear();
            return (new ChannelList(rig.Ctx, rig.Timers), rig);
        }

        [AvaloniaFact]
        public void The_channel_list_opens_on_the_channel_being_watched()
        {
            var (list, _) = Watching(Bbc2);
            Assert.Equal(Bbc2, list.Lit);
        }

        [AvaloniaFact]
        public void Moving_tunes_only_once_you_rest_on_a_channel()
        {
            var (list, rig) = Watching(Bbc1);
            list.OnAction(UiAction.Down, false);
            rig.Timers.Advance(TimeSpan.FromMilliseconds(500));
            Assert.Empty(rig.Tuned);
            list.OnAction(UiAction.Down, false);
            rig.Timers.Advance(ChannelList.Rest);
            Assert.Equal(new[] { C4 }, rig.Tuned);
            Assert.Equal("channels", rig.Ctx.Nav.State.Overlay?.Name); // the list stays while you browse
        }

        [AvaloniaFact]
        public void Up_from_the_first_channel_wraps_to_the_last()
        {
            var (list, _) = Watching(Bbc1);
            list.OnAction(UiAction.Up, false);
            Assert.Equal(C4, list.Lit);
        }

        [AvaloniaFact]
        public void Ok_tunes_straight_away_and_closes_the_list()
        {
            var (list, rig) = Watching(Bbc1);
            list.OnAction(UiAction.Down, false);
            list.OnAction(UiAction.Ok, false);
            Assert.Equal(new[] { Bbc2 }, rig.Tuned);
            Assert.Null(rig.Ctx.Nav.State.Overlay);
            rig.Timers.Advance(ChannelList.Rest);
            Assert.Single(rig.Tuned);
        }

        [AvaloniaFact]
        public void Back_closes_without_tuning()
        {
            var (list, rig) = Watching(Bbc1);
            list.OnAction(UiAction.Down, false);
            Assert.False(list.OnAction(UiAction.Back, false)); // the shell's Back closes the overlay
            rig.Ctx.Nav.Back();
            rig.Timers.Advance(ChannelList.Rest);
            Assert.Empty(rig.Tuned);
            Assert.Null(rig.Ctx.Nav.State.Overlay);
        }

        [AvaloniaFact]
        public void The_list_goes_away_by_itself_when_left_alone()
        {
            var (list, rig) = Watching(Bbc1);
            rig.Timers.Advance(ChannelList.Idle);
            Assert.Null(rig.Ctx.Nav.State.Overlay);
        }

        [Fact]
        public void Up_and_down_on_live_video_open_the_channel_list()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Channels = new[] { Bbc1, Bbc2, C4 };
            rig.Ctx.PlayLive(Bbc1);
            var keys = new VideoActions(rig.Ctx, rig.Timers);
            Assert.True(keys.OnAction(UiAction.Down, false));
            Assert.Equal("channels", rig.Ctx.Nav.State.Overlay?.Name);
        }

        [Fact]
        public void Subtitles_toggle_and_remember_their_state()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.PlayItem(new HomeCard(CardKind.Film, "f", "Film", null, null, null, null, "Jellyfin", PlayVia.Mpv, "http://jf/f"));
            Assert.True(rig.Ctx.Playback.SubtitlesOn);
            rig.Ctx.Playback.ToggleSubtitles();
            Assert.False(rig.Ctx.Playback.SubtitlesOn);
            Assert.Contains("subs False", rig.Engine.Calls);
        }

        [Fact]
        public void Near_the_end_of_a_programme_the_next_one_is_offered_once()
        {
            var now = DateTimeOffset.Parse("2026-10-26T18:26:00Z");
            var prog = new Programme("p", "ch1", "News", null, null, now.AddMinutes(-26), now.AddMinutes(4));
            var card = new LiveUpNext();
            Assert.True(card.ShouldShow(prog, now));
            card.Dismiss(prog);
            Assert.False(card.ShouldShow(prog, now.AddMinutes(1)));
            Assert.False(new LiveUpNext().ShouldShow(prog, now.AddMinutes(-10)));
        }

        [Fact]
        public async Task The_next_programme_is_the_one_after_whats_on_now()
        {
            var (home, _, epg, _) = HomeDataTests.Build();
            var now = DateTimeOffset.UtcNow;
            epg.MockSchedules["ch1"].Add(new Baird.Models.EpgProgram { Id = "p2", ChannelId = "ch1", Title = "Regional News", StartUnix = now.AddMinutes(15).ToUnixTimeSeconds(), StopUnix = now.AddMinutes(45).ToUnixTimeSeconds() });
            var next = await home.NextProgrammeAsync("ch1", now);
            Assert.Equal("Regional News", next?.Title);
            Assert.Null(await home.NextProgrammeAsync("nothing", now));
        }

        private static (InfoBar Bar, HomeScreenTests.Rig Rig) InfoOver(bool live)
        {
            var rig = HomeScreenTests.Context();
            if (live) { rig.Ctx.Channels = new[] { Bbc1 }; rig.Ctx.PlayLive(Bbc1); rig.Timers.Advance(PlaybackController.TuneDebounce); }
            else rig.Ctx.PlayItem(new HomeCard(CardKind.Film, "f", "Film", null, null, null, null, "Jellyfin", PlayVia.Mpv, "http://jf/f"));
            rig.Engine.Loaded();
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("info"));
            return (new InfoBar(rig.Ctx), rig);
        }

        [AvaloniaFact]
        public void The_info_bar_has_play_subtitles_and_audio_buttons_that_ok_presses()
        {
            var (bar, rig) = InfoOver(live: false);
            Assert.Equal(new[] { "Pause", "Subtitles on", "Audio" }, bar.Buttons);
            Assert.Equal(0, bar.LitButton);
            bar.OnAction(UiAction.Right, false);
            bar.OnAction(UiAction.Ok, false);
            Assert.False(rig.Ctx.Playback.SubtitlesOn);
            Assert.Equal("Subtitles off", bar.Buttons[1]);
            bar.OnAction(UiAction.Left, false);
            bar.OnAction(UiAction.Ok, false);
            Assert.Contains("pause", rig.Engine.Calls);
        }

        [AvaloniaFact]
        public void Up_or_down_on_live_info_bar_swaps_it_for_the_channel_list()
        {
            var (bar, rig) = InfoOver(live: true);
            Assert.True(bar.OnAction(UiAction.Down, false));
            Assert.Equal("channels", rig.Ctx.Nav.State.Overlay?.Name);
        }

        [AvaloniaFact]
        public void Skip_keys_still_scan_with_the_info_bar_up()
        {
            var (bar, rig) = InfoOver(live: false);
            bar.OnAction(UiAction.Next, false);
            Assert.Contains("rel 30", rig.Engine.Calls);
        }

        [Fact]
        public void Holding_ok_on_the_video_opens_the_actions_panel()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.PlayLive(Bbc1);
            var keys = new VideoActions(rig.Ctx, rig.Timers);
            Assert.True(keys.OnAction(UiAction.HoldOk, false));
            Assert.Equal("actions", rig.Ctx.Nav.State.Overlay?.Name);
        }

        [AvaloniaFact]
        public void The_live_actions_panel_offers_subtitles_first_and_this_channels_schedule()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.PlayLive(Bbc1);
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("actions"));
            var guide = 0;
            var panel = new ActionsPanel(rig.Ctx, () => guide++);
            Assert.Equal("Subtitles off", panel.Items[0]); // on now, so the action turns them off
            Assert.Contains("This channel's schedule", panel.Items);
            panel.OnAction(UiAction.Ok, false);
            Assert.False(rig.Ctx.Playback.SubtitlesOn);
            Assert.Null(rig.Ctx.Nav.State.Overlay);
        }

        [AvaloniaFact]
        public void On_demand_actions_include_start_over()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.PlayItem(new HomeCard(CardKind.Film, "f", "Film", null, null, null, null, "Jellyfin", PlayVia.Mpv, "http://jf/f"));
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("actions"));
            var panel = new ActionsPanel(rig.Ctx, () => { });
            var i = panel.Items.ToList().IndexOf("Start over");
            Assert.True(i >= 0);
            for (var k = 0; k < i; k++) panel.OnAction(UiAction.Down, false);
            panel.OnAction(UiAction.Ok, false);
            Assert.Contains("seek 0", rig.Engine.Calls);
        }
    }
}
