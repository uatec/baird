using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class PlayFlowTests
    {
        private static readonly Channel Bbc1 = new("ch1", "101", "BBC One", null, "http://tvh/101");
        private static readonly Channel Bbc2 = new("ch2", "102", "BBC Two", null, "http://tvh/102");
        private static readonly Channel C4 = new("ch4", "104", "Channel 4", null, "http://tvh/104");

        private static (VideoActions Keys, HomeScreenTests.Rig Rig) Watching(Channel ch)
        {
            var rig = HomeScreenTests.Context();
            rig.Ctx.Channels = new[] { Bbc1, Bbc2, C4 };
            rig.Ctx.PlayLive(ch);
            return (new VideoActions(rig.Ctx, rig.Timers), rig);
        }

        [Fact]
        public void Page_up_and_down_tune_the_neighbouring_channel_and_wrap()
        {
            var (keys, rig) = Watching(C4);
            keys.OnAction(UiAction.PageUp, false);
            Assert.Equal(Bbc1, rig.Ctx.Playback.Current!.Channel);
            keys.OnAction(UiAction.PageDown, false);
            Assert.Equal(C4, rig.Ctx.Playback.Current!.Channel);
            keys.OnAction(UiAction.PageDown, false);
            Assert.Equal(Bbc2, rig.Ctx.Playback.Current!.Channel);
        }

        [Fact]
        public void Flicking_through_channels_only_tunes_the_one_you_rest_on()
        {
            var (keys, rig) = Watching(Bbc1);
            rig.Timers.Advance(PlaybackController.TuneDebounce);
            keys.OnAction(UiAction.PageUp, false);
            keys.OnAction(UiAction.PageUp, false);
            rig.Timers.Advance(PlaybackController.TuneDebounce);
            Assert.Equal(new[] { Bbc1.StreamUrl, C4.StreamUrl }, rig.Engine.Plays.Select(p => p.Url));
        }

        [Fact]
        public void Typing_a_whole_channel_number_tunes_it_straight_away()
        {
            var (keys, rig) = Watching(Bbc1);
            keys.OnAction(UiAction.Digit1, false);
            keys.OnAction(UiAction.Digit0, false);
            keys.OnAction(UiAction.Digit4, false);
            Assert.Equal(C4, rig.Ctx.Playback.Current!.Channel);
        }

        [Fact]
        public void A_number_that_does_not_exist_says_so_and_stays_put()
        {
            var (keys, rig) = Watching(Bbc1);
            keys.OnAction(UiAction.Digit9, false);
            Assert.Equal("No channel 9", rig.Toasts.Last());
            Assert.Equal(Bbc1, rig.Ctx.Playback.Current!.Channel);
        }

        [Fact]
        public void An_unfinished_number_is_tried_after_a_pause()
        {
            var (keys, rig) = Watching(Bbc1);
            keys.OnAction(UiAction.Digit1, false);
            keys.OnAction(UiAction.Digit0, false);
            rig.Timers.Advance(VideoActions.NumberWait);
            Assert.Equal("No channel 10", rig.Toasts.Last());
            Assert.Equal(Bbc1, rig.Ctx.Playback.Current!.Channel);
        }

        [Fact]
        public void Ok_on_the_video_opens_the_info_bar_and_back_returns_to_where_you_came_from()
        {
            var (keys, rig) = Watching(Bbc1);
            Assert.True(keys.OnAction(UiAction.Ok, false));
            Assert.Equal("info", rig.Ctx.Nav.State.Overlay!.Name);
            rig.Ctx.Nav.Back();
            Assert.Null(rig.Ctx.Nav.State.Overlay);
            rig.Ctx.Nav.Back();
            Assert.False(rig.Ctx.Nav.State.AtVideo);
            Assert.Equal(Place.Home, rig.Ctx.Nav.State.Place);
        }

        [Fact]
        public void Left_and_right_scan_and_a_held_key_scans_further()
        {
            var (keys, rig) = Watching(Bbc1);
            rig.Timers.Advance(PlaybackController.TuneDebounce);
            rig.Engine.Loaded();
            keys.OnAction(UiAction.Left, false);
            keys.OnAction(UiAction.Left, true);
            Assert.Equal(new[] { "rel -30", "rel -120" }, rig.Engine.Calls.Where(c => c.StartsWith("rel")));
        }

        [Fact]
        public void Ok_answers_the_services_prompt_when_one_is_up()
        {
            var (keys, rig) = Watching(Bbc1);
            var answered = 0;
            rig.Ctx.TryActivatePrompt = () => { answered++; return true; };
            keys.OnAction(UiAction.Ok, false);
            Assert.Equal(1, answered);
            Assert.Null(rig.Ctx.Nav.State.Overlay);
        }

        [Fact]
        public async Task Ok_on_a_failure_that_offers_a_fix_runs_it_and_plays_again()
        {
            var (keys, rig) = Watching(Bbc1);
            rig.Timers.Advance(PlaybackController.TuneDebounce);
            for (var i = 0; i <= PlaybackController.MaxRetries; i++) rig.Timers.Advance(PlaybackController.LoadTimeout);
            Assert.NotNull(rig.Ctx.Playback.Failure);
            var plays = rig.Engine.Plays.Count;
            rig.Ctx.FailureAction = () => "sign in to Channel 4";
            rig.Ctx.RunFailureAction = () => Task.FromResult<string?>(null);
            keys.OnAction(UiAction.Ok, false);
            await Task.Yield();
            Assert.Equal(plays + 1, rig.Engine.Plays.Count);
            Assert.Null(rig.Ctx.Playback.Failure);
        }

        [AvaloniaFact]
        public async Task The_tuning_ident_stays_up_at_least_a_moment_even_if_the_stream_loads_at_once()
        {
            var ident = new TuneIdent();
            ident.Show(Bbc1, null);
            ident.Loaded();
            await Wait(500);
            Assert.True(ident.Showing);
            await Wait(1200);
            Assert.False(ident.Showing);
        }

        private static async Task Wait(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); } }
    }
}
