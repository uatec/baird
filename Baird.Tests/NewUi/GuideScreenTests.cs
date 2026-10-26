using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Baird.NewUi;
using Baird.NewUi.Adapters;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class GuideScreenTests
    {
        // 18:10 local: the window starts at 17:30.
        private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 26, 18, 10, 0, DateTimeKind.Local));

        private sealed record Rig(GuideScreen Guide, NewUiContext Ctx, PlaybackControllerTests.FakeTimers Timers, List<string> Toasts, List<Channel> Tuned, GuideModel Model);

        /// <summary>Channels numbered 101…; every row is 30-minute programmes, except a 20-minute offset on odd rows and no EPG on rows listed.</summary>
        internal static GuideModel Model(int channels, params int[] noEpg)
        {
            var start = GuideData.WindowStartFor(Now);
            var end = start + GuideData.Ahead;
            var list = new List<GuideChannel>();
            for (var i = 0; i < channels; i++)
            {
                var ch = new Channel($"ch{i}", (101 + i).ToString(), $"Channel {i}", null, $"http://tvh/{i}");
                var progs = new List<Programme>();
                if (!noEpg.Contains(i))
                {
                    var t = start.AddMinutes(i % 2 == 1 ? -10 : 0);
                    for (var k = 0; t < end; k++, t = t.AddMinutes(30))
                        progs.Add(new Programme($"ch{i}-p{k}", ch.Id, $"Show {i}.{k}", null, "Synopsis", t, t.AddMinutes(30)));
                }
                list.Add(new GuideChannel(ch, GuideData.Fill(ch.Id, progs, start, end)));
            }
            return new GuideModel(start, end, list);
        }

        private static async Task<Rig> Shown(GuideModel? model = null)
        {
            model ??= Model(20, 3);
            var engine = new PlaybackControllerTests.FakeEngine();
            var timers = new PlaybackControllerTests.FakeTimers();
            var playback = new PlaybackController(engine, a => a(), timers, () => timers.Now, null);
            var ctx = new NewUiContext(new Navigator(), playback, HomeDataTests.Build().Home, () => Now);
            var toasts = new List<string>();
            var tuned = new List<Channel>();
            ctx.Toast = toasts.Add;
            ctx.Tuning += tuned.Add;
            var guide = new GuideScreen(ctx, _ => Task.FromResult(model), timers);
            ctx.Nav.SetPlace(Place.LiveTv);
            var w = new Window { Width = 1920, Height = 1080, Content = guide.View };
            w.Show();
            await guide.LoadAsync();
            guide.OnShow();
            Dispatcher.UIThread.RunJobs();
            return new Rig(guide, ctx, timers, toasts, tuned, model);
        }

        private static async Task Wait(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); } }

        private static void Press(GuideScreen g, UiAction a, int times = 1) { for (var i = 0; i < times; i++) Assert.True(g.OnAction(a, false), $"{a} not handled"); }

        [AvaloniaFact]
        public async Task It_opens_on_whats_on_now_on_the_first_channel_with_one_lit_cell()
        {
            var r = await Shown();
            Assert.Equal(0, r.Guide.Pos.Ch);
            Assert.Equal("Show 0.1", r.Guide.Lit.Title); // 18:00–18:30
            Assert.NotNull(r.Guide.LitElement);
            Assert.Single(r.Guide.View.GetVisualDescendants().OfType<GuideCell>(), c => c.Lit);
        }

        [AvaloniaFact]
        public async Task Left_and_right_move_through_time_and_stop_at_the_window_start()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Right);
            Assert.Equal("Show 0.2", r.Guide.Lit.Title);
            Press(r.Guide, UiAction.Left, 2);
            Assert.Equal("Show 0.0", r.Guide.Lit.Title); // 17:30, the first in the window
            Press(r.Guide, UiAction.Left);
            Assert.Equal("Show 0.0", r.Guide.Lit.Title);
        }

        [AvaloniaFact]
        public async Task Up_and_down_keep_the_time_column_you_are_in()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Right, 2); // 19:00–19:30 on channel 0
            Press(r.Guide, UiAction.Down);     // channel 1 runs :20/:50: the one covering 19:00 is 18:50–19:20
            Assert.Equal(1, r.Guide.Pos.Ch);
            Assert.Equal(new DateTime(2026, 10, 26, 18, 50, 0), r.Guide.Lit.Start.LocalDateTime);
            Press(r.Guide, UiAction.Down);
            Assert.Equal("Show 2.3", r.Guide.Lit.Title); // back to 19:00, not 18:50
        }

        [AvaloniaFact]
        public async Task Up_off_the_top_row_is_left_to_the_shell()
        {
            var r = await Shown();
            Assert.False(r.Guide.OnAction(UiAction.Up, false));
            Press(r.Guide, UiAction.Down);
            Assert.True(r.Guide.OnAction(UiAction.Up, false));
            Assert.Equal(0, r.Guide.Pos.Ch);
        }

        [AvaloniaFact]
        public async Task Page_down_and_up_move_a_page_of_channels_and_clamp()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.PageDown);
            Assert.Equal(GuideScreen.VisibleChannels, r.Guide.Pos.Ch);
            Press(r.Guide, UiAction.PageDown, 5);
            Assert.Equal(19, r.Guide.Pos.Ch);
            Press(r.Guide, UiAction.PageUp);
            Assert.Equal(19 - GuideScreen.VisibleChannels, r.Guide.Pos.Ch);
        }

        [AvaloniaFact]
        public async Task Ok_on_a_programme_on_now_tunes_its_channel()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Down, 2);
            Press(r.Guide, UiAction.Ok);
            Assert.Equal("ch2", r.Tuned.Single().Id);
            Assert.Equal("live", r.Ctx.Playback.Current!.Kind);
            Assert.True(r.Ctx.Nav.State.AtVideo);
        }

        [AvaloniaFact]
        public async Task Ok_on_a_channel_with_no_guide_data_tunes_it()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Down, 3);
            Assert.True(r.Guide.Lit.NoInfo);
            Assert.Equal(GuideData.NoInfoTitle, r.Guide.Lit.Title);
            Press(r.Guide, UiAction.Ok);
            Assert.Equal("ch3", r.Tuned.Single().Id);
        }

        [AvaloniaFact]
        public async Task Ok_on_a_later_programme_says_when_it_is_on_and_does_not_tune()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Right, 2);
            Press(r.Guide, UiAction.Ok);
            Assert.Empty(r.Tuned);
            Assert.Contains("Show 0.3", r.Toasts.Single());
            Assert.Contains("19:00", r.Toasts.Single());
        }

        [AvaloniaFact]
        public async Task Digits_jump_to_a_channel_keeping_the_time()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Right); // 18:30
            Press(r.Guide, UiAction.Digit1); Press(r.Guide, UiAction.Digit1); Press(r.Guide, UiAction.Digit2);
            r.Timers.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(11, r.Guide.Pos.Ch); // 112
            Assert.Equal("Show 11.2", r.Guide.Lit.Title); // 18:20–18:50 covers 18:30
            Assert.Empty(r.Tuned);
        }

        [AvaloniaFact]
        public async Task A_number_that_is_not_a_channel_says_so()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Digit9); Press(r.Guide, UiAction.Digit9);
            r.Timers.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(0, r.Guide.Pos.Ch);
            Assert.Contains("No channel 99", r.Guide.EntryText);
        }

        [AvaloniaFact]
        public async Task Only_the_visible_window_of_cells_is_realised()
        {
            var r = await Shown(Model(150));
            var total = r.Model.Channels.Sum(c => c.Schedule.Count);
            Assert.True(total > 7000, $"{total}");
            Assert.InRange(r.Guide.RealisedCells, 1, 120);
            Press(r.Guide, UiAction.PageDown, 10);
            Press(r.Guide, UiAction.Right, 20);
            Press(r.Guide, UiAction.Down, 7);
            Assert.InRange(r.Guide.RealisedCells, 1, 120);
            Assert.Equal(r.Guide.View.GetVisualDescendants().OfType<GuideCell>().Count(), r.Guide.RealisedCells);
            Assert.Single(r.Guide.View.GetVisualDescendants().OfType<GuideCell>(), c => c.Lit);
        }

        [AvaloniaFact]
        public async Task The_lit_cell_stays_on_screen_as_focus_moves()
        {
            var r = await Shown(Model(60));
            foreach (var a in new[] { UiAction.PageDown, UiAction.PageDown, UiAction.Right, UiAction.Right, UiAction.Right, UiAction.Right, UiAction.Right, UiAction.Down })
            {
                Press(r.Guide, a);
                await Wait(600);
                var lit = r.Guide.LitElement!;
                var m = lit.TransformToVisual(r.Guide.View)!.Value;
                var tl = new Point(0, 0).Transform(m);
                Assert.InRange(tl.Y, GuideScreen.BodyTop - 1, 1080 - 76);
                Assert.InRange(tl.X, GuideScreen.TimeStart - 1, 1920 - 40);
            }
        }

        [AvaloniaFact]
        public async Task Hold_ok_opens_actions_and_back_closes_them()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.HoldOk);
            Assert.True(r.Guide.ActionsOpen);
            Press(r.Guide, UiAction.Back);
            Assert.False(r.Guide.ActionsOpen);
            Assert.False(r.Guide.OnAction(UiAction.Back, false)); // then the shell lights the tab
        }

        [AvaloniaFact]
        public async Task All_showings_lists_later_showings_and_jumps_the_grid_to_one()
        {
            var model = Model(4);
            // Channel 2 repeats channel 0's 19:00 programme tomorrow-ish at its own slot.
            var ch2 = model.Channels[2];
            var target = ch2.Schedule[20];
            var renamed = ch2.Schedule.Select(p => p.Id == target.Id ? p with { Title = "Show 0.3" } : p).ToList();
            model = model with { Channels = model.Channels.Select((c, i) => i == 2 ? c with { Schedule = renamed } : c).ToList() };
            var r = await Shown(model);
            Press(r.Guide, UiAction.Right, 2); // Show 0.3
            Press(r.Guide, UiAction.HoldOk);
            Assert.Contains("All showings", r.Guide.ActionLabels);
            r.Guide.RunAction("All showings");
            Assert.Equal(2, r.Guide.ActionLabels.Count);
            Press(r.Guide, UiAction.Down);
            Press(r.Guide, UiAction.Ok);
            Assert.False(r.Guide.ActionsOpen);
            Assert.Equal((2, target.Id), (r.Guide.Pos.Ch, r.Guide.Lit.Id));
        }

        [AvaloniaFact]
        public void The_preview_frames_the_video_only_on_the_guide_while_something_plays()
        {
            var nav = new Navigator();
            nav.SetPlace(Place.LiveTv);
            Assert.True(GuideScreen.ShowsPreview(nav.State, playing: true));
            Assert.False(GuideScreen.ShowsPreview(nav.State, playing: false));
            nav.Play(new VideoRef("live", "ch1"));
            Assert.False(GuideScreen.ShowsPreview(nav.State, playing: true));
            nav.Back();
            Assert.True(GuideScreen.ShowsPreview(nav.State, playing: true));
            nav.SetPlace(Place.Home);
            Assert.False(GuideScreen.ShowsPreview(nav.State, playing: true));
        }

        [AvaloniaFact]
        public void The_preview_transform_maps_the_whole_surface_onto_the_frame()
        {
            var t = GuideScreen.PreviewTransform(new Rect(1304, 130, 520, 292), new Size(1920, 1080)).Value;
            Assert.Equal(new Point(1304, 130), new Point(0, 0).Transform(t));
            var br = new Point(1920, 1080).Transform(t);
            Assert.Equal(1824, br.X, 1);
            Assert.Equal(422, br.Y, 1);
        }
    }
}
