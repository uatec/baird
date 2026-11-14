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
            Assert.True(bar.OnScrubRow);
            bar.OnAction(UiAction.Down, false);           // down to the buttons
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
            Assert.True(bar.OnAction(UiAction.Up, false));
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

        private static HomeCard Ep(string id, string label) => new(CardKind.Episode, id, label, null, null, null, null, "Jellyfin", PlayVia.Mpv, $"http://jf/{id}");

        [Fact]
        public void The_next_episode_is_the_one_after_whats_playing_in_the_shows_order()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Following = new[] { Ep("e1", "Glorious Purpose"), Ep("e2", "The Variant"), Ep("e3", "Lamentis") };
            rig.Ctx.PlayItem(rig.Ctx.Following[1]);
            Assert.Equal("e3", rig.Ctx.NextEpisode()?.Id);
            rig.Ctx.PlayItem(rig.Ctx.Following[2]);
            Assert.Null(rig.Ctx.NextEpisode());
        }

        [AvaloniaFact]
        public void The_up_next_card_counts_down_and_plays_the_next_episode()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Following = new[] { Ep("e1", "Glorious Purpose"), Ep("e2", "The Variant") };
            rig.Ctx.PlayItem(rig.Ctx.Following[0]);
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("upnext"));
            var card = new UpNextCard(rig.Ctx, rig.Timers, rig.Ctx.Following[1]);
            for (var i = 0; i < UpNextCard.Countdown.TotalSeconds - 1; i++) rig.Timers.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("e1", rig.Ctx.Playback.Current?.ItemId);
            rig.Timers.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("e2", rig.Ctx.Playback.Current?.ItemId);
            Assert.Null(rig.Ctx.Nav.State.Overlay);
        }

        [AvaloniaFact]
        public void Ok_on_up_next_plays_now_and_back_goes_back_to_the_show()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Following = new[] { Ep("e1", "Glorious Purpose"), Ep("e2", "The Variant") };
            rig.Ctx.PlayItem(rig.Ctx.Following[0]);
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("upnext"));
            var card = new UpNextCard(rig.Ctx, rig.Timers, rig.Ctx.Following[1]);
            card.OnAction(UiAction.Ok, false);
            Assert.Equal("e2", rig.Ctx.Playback.Current?.ItemId);

            rig.Ctx.Nav.OpenOverlay(new OverlayRef("upnext"));
            var again = new UpNextCard(rig.Ctx, rig.Timers, rig.Ctx.Following[1]);
            again.OnAction(UiAction.Right, false); // "Back to show"
            again.OnAction(UiAction.Ok, false);
            Assert.Null(rig.Ctx.Playback.Current);
            Assert.False(rig.Ctx.Nav.State.AtVideo);
            for (var i = 0; i < UpNextCard.Countdown.TotalSeconds; i++) rig.Timers.Advance(TimeSpan.FromSeconds(1));
            Assert.Null(rig.Ctx.Playback.Current);
        }

        [AvaloniaFact]
        public void The_info_bar_opens_on_the_scrubber_where_left_and_right_seek_and_ok_pauses()
        {
            var (bar, rig) = InfoOver(live: false);
            Assert.True(bar.OnScrubRow);
            bar.OnAction(UiAction.Left, false);
            bar.OnAction(UiAction.Right, true);
            Assert.Equal(new[] { "rel -30", "rel 120" }, rig.Engine.Calls.Where(c => c.StartsWith("rel")));
            bar.OnAction(UiAction.Ok, false);
            Assert.Contains("pause", rig.Engine.Calls);
            bar.OnAction(UiAction.Down, false);
            Assert.False(bar.OnScrubRow);
            bar.OnAction(UiAction.Up, false);
            Assert.True(bar.OnScrubRow);
        }

        [Fact]
        public void Pausing_or_seeking_on_the_bare_video_shows_where_you_are()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.PlayItem(new HomeCard(CardKind.Film, "f", "Film", null, null, null, null, "Jellyfin", PlayVia.Mpv, "http://jf/f"));
            rig.Engine.Loaded();
            var keys = new VideoActions(rig.Ctx, rig.Timers);
            keys.OnAction(UiAction.PlayPause, false);
            Assert.Equal("info", rig.Ctx.Nav.State.Overlay?.Name);
            rig.Ctx.Nav.CloseOverlay();
            keys.OnAction(UiAction.Right, false);
            Assert.Contains("rel 30", rig.Engine.Calls);
            Assert.Equal("info", rig.Ctx.Nav.State.Overlay?.Name);
            rig.Ctx.Nav.CloseOverlay();
            keys.OnAction(UiAction.Prev, false);
            Assert.Equal("info", rig.Ctx.Nav.State.Overlay?.Name);
        }

        [AvaloniaFact]
        public void The_first_press_that_opens_the_channel_list_also_moves_one_channel()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Channels = new[] { Bbc1, Bbc2, C4 };
            rig.Ctx.PlayLive(Bbc1);
            rig.Ctx.Nav.OpenOverlay(new OverlayRef("channels", UiAction.Down));
            var list = new ChannelList(rig.Ctx, rig.Timers, UiAction.Down);
            Assert.Equal(Bbc1, list.Lit);
            rig.Timers.Advance(ChannelList.FirstStep);
            Assert.Equal(Bbc2, list.Lit);
        }

        [Fact]
        public async Task An_episode_started_from_home_finds_its_shows_order_for_up_next()
        {
            var rig = HomeScreenTests.Context();
            var e1 = Ep("e1", "Glorious Purpose"); var e2 = Ep("e2", "The Variant");
            var asked = new System.Collections.Generic.List<string>();
            rig.Ctx.FindFollowing = card => { asked.Add(card.Id); return Task.FromResult<(IReadOnlyList<HomeCard>, string)?>((new[] { e1, e2 }, "Loki")); };
            rig.Ctx.PlayItem(e1);
            await Task.Yield();
            Assert.Equal(new[] { "e1" }, asked);
            Assert.Equal("e2", rig.Ctx.NextEpisode()?.Id);
            Assert.Equal("Loki", rig.Ctx.FollowingShow);
        }

        [AvaloniaFact]
        public void Channel_up_tunes_at_once_and_shows_the_channel_strip()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Channels = new[] { Bbc1, Bbc2, C4 };
            rig.Ctx.PlayLive(Bbc1);
            var keys = new VideoActions(rig.Ctx, rig.Timers);
            keys.OnAction(UiAction.PageUp, false);
            Assert.Equal(Bbc2, rig.Ctx.Playback.Current!.Channel);
            Assert.Equal("channels", rig.Ctx.Nav.State.Overlay?.Name);
            var list = new ChannelList(rig.Ctx, rig.Timers, rig.Ctx.Nav.State.Overlay!.Params);
            Assert.Equal(Bbc2, list.Lit);
            list.OnAction(UiAction.PageUp, false);                   // again, with the strip up: tunes at once
            Assert.Equal(C4, rig.Ctx.Playback.Current!.Channel);
            Assert.Equal(C4, list.Lit);
        }

        [AvaloniaFact]
        public void Channel_up_opens_the_strip_on_the_channel_you_were_on_so_the_move_shows()
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Channels = new[] { Bbc1, Bbc2, C4 };
            rig.Ctx.PlayLive(Bbc1);
            new VideoActions(rig.Ctx, rig.Timers).OnAction(UiAction.PageUp, false);
            var list = new ChannelList(rig.Ctx, rig.Timers, rig.Ctx.Nav.State.Overlay!.Params);
            Assert.Equal(Bbc1, list.OpenedOn); // drawn there first, then the highlight slides on
            Assert.Equal(Bbc2, list.Lit);
        }

        [AvaloniaFact]
        public void The_strip_says_a_channel_is_tuning_until_its_picture_arrives()
        {
            var (list, rig) = Watching(Bbc1);
            rig.Engine.Loaded();
            new Avalonia.Controls.Window { Width = 1920, Height = 1080, Content = list.View }.Show();
            Assert.Equal("watching", list.RowOf("ch1")!.Status);
            list.OnAction(UiAction.PageUp, false);
            Assert.Equal("tuning…", list.RowOf("ch2")!.Status);
            Assert.Null(list.RowOf("ch1")!.Status);
            rig.Timers.Advance(PlaybackController.TuneDebounce);
            rig.Engine.Loaded();
            Assert.Equal("watching", list.RowOf("ch2")!.Status);
            list.Destroy();
        }

        // ---- the live bar: its right end is live; the playhead sits there and moves back as you rewind or pause ----
        private static Programme OnAirSince(DateTimeOffset now, int minutesAgo) =>
            new("p", "ch1", "News", null, null, now.AddMinutes(-minutesAgo), now.AddMinutes(30 - minutesAgo));

        [Fact]
        public void At_live_the_playhead_is_at_the_right_end_whatever_the_programme_progress()
        {
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(1.0, InfoBar.LiveScale(TimeSpan.Zero, OnAirSince(now, 25), now).Fraction);   // late in the programme
            Assert.Equal(1.0, InfoBar.LiveScale(TimeSpan.Zero, OnAirSince(now, 0), now).Fraction);    // at its very start
            Assert.Equal(1.0, InfoBar.LiveScale(TimeSpan.Zero, null, now).Fraction);                   // no programme information
        }

        [Fact]
        public void Going_behind_live_moves_the_playhead_left_on_a_scale_of_the_programme_so_far()
        {
            var now = DateTimeOffset.UtcNow;
            var scale = InfoBar.LiveScale(TimeSpan.FromMinutes(5), OnAirSince(now, 40), now);   // 40 min of the programme, 5 behind
            Assert.Equal(0.875, scale.Fraction, 3);
            Assert.Equal(now.AddMinutes(-40), scale.LeftEdge);                                   // the left end is the programme's start
        }

        [Fact]
        public void A_programme_that_has_only_just_started_still_has_a_usable_scale_and_never_goes_off_the_left_end()
        {
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(0.9, InfoBar.LiveScale(TimeSpan.FromMinutes(1), OnAirSince(now, 2), now).Fraction, 3); // at least ten minutes wide
            Assert.Equal(0.0, InfoBar.LiveScale(TimeSpan.FromMinutes(90), OnAirSince(now, 30), now).Fraction, 3); // further back than the programme: the left end
            Assert.InRange(InfoBar.LiveScale(TimeSpan.FromMinutes(1), null, now).Fraction, 0.9, 1.0);            // no programme: a half-hour scale
        }

        private static double FilledWidth(InfoBar bar)
        {
            var track = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(bar.View).OfType<Avalonia.Controls.Grid>().First(g => g.Height == 8);
            return ((Avalonia.Controls.Border)track.Children[1]).Width;
        }

        [AvaloniaFact]
        public void The_live_bar_is_full_at_live_and_shortens_as_you_rewind_or_pause()
        {
            var (bar, rig) = InfoOver(live: true);
            var full = Baird.NewUi.Theme.T.W - 128 - 88;
            Assert.Equal(full, FilledWidth(bar), 1);                    // at live: the playhead is at the right end

            bar.OnAction(UiAction.Left, false);                         // rewind 30 s
            var back = FilledWidth(bar);
            Assert.True(back < full - 2, $"rewound but the bar is still {back} of {full}");
            var expected = InfoBar.LiveScale(rig.Ctx.Playback.EffectiveBehindLive, rig.Ctx.Programme, rig.Ctx.Now()).Fraction * full;
            Assert.Equal(expected, back, 1);

            bar.OnAction(UiAction.Right, false);                        // back to live
            Assert.Equal(full, FilledWidth(bar), 1);
            bar.OnAction(UiAction.Ok, false);                           // pause
            rig.Timers.Advance(TimeSpan.FromSeconds(120));              // two minutes pass, paused
            bar.OnAction(UiAction.Down, false); bar.OnAction(UiAction.Up, false); // any redraw
            Assert.True(FilledWidth(bar) < full - 2, "paused for two minutes but the playhead has not moved back");
        }
    }
}
