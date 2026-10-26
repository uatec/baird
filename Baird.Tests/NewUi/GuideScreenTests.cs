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
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class GuideScreenTests
    {
        // 18:10 local: the window starts at 17:30.
        private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 26, 18, 10, 0, DateTimeKind.Local));

        private sealed record Rig(GuideScreen Guide, NewUiContext Ctx, PlaybackControllerTests.FakeTimers Timers, List<string> Toasts, List<Channel> Tuned, GuideModel Model, List<GuideLayout> Saved);

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

        private static async Task<Rig> Shown(GuideModel? model = null, GuideLayout layout = GuideLayout.Rows, bool load = true)
        {
            model ??= Model(20, 3);
            var engine = new PlaybackControllerTests.FakeEngine();
            var timers = new PlaybackControllerTests.FakeTimers();
            var playback = new PlaybackController(engine, a => a(), timers, () => timers.Now, null);
            var ctx = new NewUiContext(new Navigator(), playback, HomeDataTests.Build().Home, () => Now);
            var toasts = new List<string>();
            var tuned = new List<Channel>();
            var saved = new List<GuideLayout>();
            ctx.Toast = toasts.Add;
            ctx.Tuning += tuned.Add;
            ctx.SetGuideLayout(layout, save: false);
            ctx.SaveGuideLayout = saved.Add; // never the real ~/.baird/config.ini
            var guide = new GuideScreen(ctx, _ => Task.FromResult(model), timers);
            ctx.Nav.SetPlace(Place.LiveTv);
            var w = new Window { Width = 1920, Height = 1080, Content = guide.View };
            w.Show();
            if (load)
            {
                await guide.LoadAsync();
                guide.OnShow();
            }
            Dispatcher.UIThread.RunJobs();
            return new Rig(guide, ctx, timers, toasts, tuned, model, saved);
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
        public async Task Up_off_the_top_row_lights_the_strip_then_is_left_to_the_shell()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Down);
            Press(r.Guide, UiAction.Up);
            Assert.Equal(0, r.Guide.Pos.Ch);
            Assert.False(r.Guide.StripFocused);
            Press(r.Guide, UiAction.Up);
            Assert.True(r.Guide.StripFocused);
            Assert.Contains("focused", r.Guide.LitElement!.Classes);
            Assert.DoesNotContain(r.Guide.Cells, c => c.Lit); // one lit thing: the strip item
            Assert.False(r.Guide.OnAction(UiAction.Up, false)); // the shell lights the tab bar
            Assert.False(r.Guide.StripFocused);
        }

        [AvaloniaFact]
        public async Task The_strip_jumps_in_time_and_down_returns_to_the_grid()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Up); // the strip, on "Now"
            Press(r.Guide, UiAction.Right); // "This evening": 19:00
            Press(r.Guide, UiAction.Ok);
            Assert.False(r.Guide.StripFocused);
            Assert.Equal(new DateTime(2026, 10, 26, 19, 0, 0), r.Guide.Lit.Start.LocalDateTime);
            Press(r.Guide, UiAction.Up);
            Press(r.Guide, UiAction.Down);
            Assert.False(r.Guide.StripFocused);
            Assert.Single(r.Guide.Cells, c => c.Lit);
        }

        [AvaloniaFact]
        public async Task The_strip_switches_the_layout_keeping_your_place_and_saves_it()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Right, 3);
            var lit = r.Guide.Lit;
            Press(r.Guide, UiAction.Up); // the top row already: the strip
            Press(r.Guide, UiAction.Right, 10); // the last item: the layout switch
            Press(r.Guide, UiAction.Ok);
            Assert.Equal(GuideLayout.Columns, r.Guide.Layout);
            Assert.Equal(GuideLayout.Columns, r.Ctx.GuideLayout);
            Assert.Equal(new[] { GuideLayout.Columns }, r.Saved);
            Assert.Contains("channels across", r.Toasts.Last());
            Press(r.Guide, UiAction.Down);
            Assert.Equal(lit.Id, r.Guide.Lit.Id);
            Assert.Single(r.Guide.Cells, c => c.Lit);
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
        public async Task Ok_on_a_later_programme_sets_a_reminder_with_a_bell_and_ok_again_cancels_it()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Right, 2);
            Press(r.Guide, UiAction.Ok);
            Assert.Empty(r.Tuned);
            Assert.Contains("Reminder set: Show 0.3", r.Toasts.Single());
            Assert.Contains("19:00", r.Toasts.Single());
            Assert.True(r.Ctx.Reminders.Has("ch0-p3"));
            Assert.True(r.Guide.Cells.Single(c => c.Programme.Id == "ch0-p3").Bell);
            Assert.False(r.Guide.Cells.Single(c => c.Programme.Id == "ch0-p2").Bell);
            Press(r.Guide, UiAction.HoldOk);
            Assert.Contains("Cancel reminder", r.Guide.ActionLabels);
            Press(r.Guide, UiAction.Back);
            Press(r.Guide, UiAction.Ok);
            Assert.False(r.Ctx.Reminders.Has("ch0-p3"));
            Assert.Contains("Reminder cancelled", r.Toasts.Last());
            Assert.False(r.Guide.Cells.Single(c => c.Programme.Id == "ch0-p3").Bell);
        }

        [AvaloniaFact]
        public async Task Columns_move_channels_with_left_right_time_with_up_down_and_an_hour_with_pg()
        {
            var r = await Shown(layout: GuideLayout.Columns);
            Assert.Equal(GuideLayout.Columns, r.Guide.Layout);
            Assert.Equal("Show 0.1", r.Guide.Lit.Title);
            Press(r.Guide, UiAction.Down); // later
            Assert.Equal("Show 0.2", r.Guide.Lit.Title);
            Press(r.Guide, UiAction.Right, 2); // two channels across, same time
            Assert.Equal("Show 2.2", r.Guide.Lit.Title);
            Press(r.Guide, UiAction.PageDown); // an hour on: 19:30
            Assert.Equal("Show 2.4", r.Guide.Lit.Title);
            Press(r.Guide, UiAction.PageUp, 3); // never before the window
            Assert.Equal("Show 2.0", r.Guide.Lit.Title);
            Press(r.Guide, UiAction.Left, 5);
            Assert.Equal(0, r.Guide.Pos.Ch);
            Press(r.Guide, UiAction.Up); // already the earliest: the strip
            Assert.True(r.Guide.StripFocused);
            Assert.False(r.Guide.OnAction(UiAction.Up, false));
        }

        [AvaloniaFact]
        public async Task Columns_realise_only_the_visible_window_and_keep_the_lit_cell_on_screen()
        {
            var r = await Shown(Model(150), GuideLayout.Columns);
            Assert.InRange(r.Guide.RealisedCells, 1, 120);
            foreach (var a in new[] { UiAction.Right, UiAction.Right, UiAction.Right, UiAction.Right, UiAction.Right, UiAction.Down, UiAction.Down, UiAction.Down, UiAction.Down, UiAction.PageDown })
            {
                Press(r.Guide, a);
                await Wait(550);
                var m = r.Guide.LitElement!.TransformToVisual(r.Guide.View)!.Value;
                var tl = new Point(0, 0).Transform(m);
                Assert.InRange(tl.X, GuideGeometry.Columns.ChStart - 1, 1920 - GuideGeometry.Columns.ChSize + 20);
                Assert.InRange(tl.Y, GuideGeometry.Columns.TimeStart - 1, 1080 - 60);
            }
            Assert.InRange(r.Guide.RealisedCells, 1, 120);
            Assert.Single(r.Guide.Cells, c => c.Lit);
        }

        [AvaloniaFact]
        public async Task Focus_lights_a_channel_at_a_time_before_or_after_loading()
        {
            var early = await Shown(load: false);
            early.Guide.Focus("ch7", Now);
            await early.Guide.LoadAsync();
            Assert.Equal((7, "Show 7.1"), (early.Guide.Pos.Ch, early.Guide.Lit.Title)); // 17:50–18:20 covers 18:10

            var r = await Shown();
            Press(r.Guide, UiAction.Right, 4);
            r.Guide.Focus("ch12", Now);
            Assert.Equal((12, "Show 12.1"), (r.Guide.Pos.Ch, r.Guide.Lit.Title));
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
            nav.Play(new VideoRef("live", "ch1"));
            nav.OpenPage(new PageRef("guide")); // ≡ while watching: the guide over the video
            Assert.True(GuideScreen.ShowsPreview(nav.State, playing: true));
            nav.OpenPage(new PageRef("show"));
            Assert.False(GuideScreen.ShowsPreview(nav.State, playing: true));
            nav.Back(); nav.Back();
            Assert.False(GuideScreen.ShowsPreview(nav.State, playing: true)); // the bare picture again
        }

        [AvaloniaFact]
        public void Settings_switches_the_guide_layout_with_left_and_right()
        {
            var layout = GuideLayout.Rows;
            var set = new List<GuideLayout>();
            var s = new SettingsScreen(() => UiMode.New, _ => { }, 1.0, _ => { }, () => layout, l => { layout = l; set.Add(l); });
            s.OnAction(UiAction.Down, false);
            s.OnAction(UiAction.Down, false);
            s.OnAction(UiAction.Right, false);
            Assert.Equal(GuideLayout.Columns, layout);
            s.OnAction(UiAction.Left, false);
            Assert.Equal(new[] { GuideLayout.Columns, GuideLayout.Rows }, set);
        }

        [AvaloniaFact]
        public async Task A_layout_change_from_settings_relays_the_guide_out_in_place()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Down, 4);
            var lit = r.Guide.Lit;
            r.Ctx.SetGuideLayout(GuideLayout.Columns);
            Assert.Equal(GuideLayout.Columns, r.Guide.Layout);
            Assert.Equal(lit.Id, r.Guide.Lit.Id);
            Assert.Single(r.Guide.Cells, c => c.Lit);
            Assert.Equal(new[] { GuideLayout.Columns }, r.Saved);
        }

        [AvaloniaFact]
        public async Task This_channels_schedule_opens_the_guide_lit_on_the_playing_channel_at_now()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Right, 6); // somewhere else entirely
            r.Ctx.PlayLive(r.Model.Channels[9].Channel);
            var nav = r.Ctx.Nav;
            ((Window)TopLevel.GetTopLevel(r.Guide.View)!).Content = null; // the shell hosts it now
            var shell = new Baird.NewUi.Shell.Shell(new Baird.NewUi.Shell.ShellDeps(new Border(), nav,
                p => p == Place.LiveTv ? r.Guide : new EmptyScreen(), _ => new EmptyScreen(), _ => new EmptyScreen(), (_, _) => true, () => "x"));
            nav.ToVideo();
            nav.Back(); // off the video, on the Live TV place (as from a channel card's actions)
            GuideScreen.OpenSchedule(shell, nav, "ch9", Now);
            Assert.Equal(Place.LiveTv, nav.State.Place);
            Assert.False(nav.State.AtVideo);
            Assert.Equal((9, "Show 9.1"), (r.Guide.Pos.Ch, r.Guide.Lit.Title)); // 17:50–18:20 covers 18:10
        }

        [AvaloniaFact]
        public async Task This_channels_schedule_while_watching_opens_the_guide_over_the_video()
        {
            var r = await Shown();
            r.Ctx.PlayLive(r.Model.Channels[9].Channel);
            var nav = r.Ctx.Nav;
            ((Window)TopLevel.GetTopLevel(r.Guide.View)!).Content = null;
            var shell = new Baird.NewUi.Shell.Shell(new Baird.NewUi.Shell.ShellDeps(new Border(), nav,
                p => p == Place.LiveTv ? r.Guide : new EmptyScreen(), _ => new EmptyScreen(), _ => new EmptyScreen(), (_, _) => true, () => "x"));
            GuideScreen.OpenSchedule(shell, nav, "ch9", Now);
            var st = nav.State;
            Assert.True(st.AtVideo);
            var page = Assert.Single(st.Pages);
            Assert.Equal(("guide", (object?)"ch9"), (page.Name, page.Params));
            Assert.True(GuideScreen.ShowsPreview(st, playing: true));
        }

        [AvaloniaFact]
        public async Task A_guide_page_opens_lit_on_its_channel_and_lets_back_go_to_the_video()
        {
            var r = await Shown(load: false);
            var page = GuideScreen.ForPage(r.Ctx, _ => Task.FromResult(r.Model), r.Timers, new PageRef("guide", "ch12"));
            await page.LoadAsync();
            Assert.Equal((12, "Show 12.1"), (page.Pos.Ch, page.Lit.Title));
            Assert.False(page.OnAction(UiAction.Back, false));
            page.Destroy();
            r.Ctx.SetGuideLayout(GuideLayout.Columns); // no longer listening
            Assert.Equal(GuideLayout.Rows, page.Layout);
        }

        [AvaloniaFact]
        public async Task The_guide_key_inside_the_guide_lights_the_strip()
        {
            var r = await Shown();
            Press(r.Guide, UiAction.Down, 3);
            Press(r.Guide, UiAction.Guide);
            Assert.True(r.Guide.StripFocused);
            Assert.Equal(3, r.Guide.Pos.Ch);
            Press(r.Guide, UiAction.Down);
            Assert.False(r.Guide.StripFocused);
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
