using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Baird.NewUi;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ReminderTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);
        private static readonly Channel Bbc = new("ch1", "101", "BBC One", null, "http://tvh/1");
        private static Programme Prog(string id, int startMin) => new(id, "ch1", $"Show {id}", null, null, T0.AddMinutes(startMin), T0.AddMinutes(startMin + 30));

        private static string TempFile() => Path.Combine(Path.GetTempPath(), $"baird-reminders-{Guid.NewGuid():N}.json");

        [Fact]
        public void Toggle_sets_then_cancels_and_says_which()
        {
            var r = new Reminders(null, () => T0);
            var changes = 0;
            r.Changed += () => changes++;
            Assert.True(r.Toggle(Prog("a", 60), Bbc));
            Assert.True(r.Has("a"));
            Assert.Equal("BBC One", r.All.Single().ChannelName);
            Assert.False(r.Toggle(Prog("a", 60), Bbc));
            Assert.False(r.Has("a"));
            Assert.Equal(2, changes);
        }

        [Fact]
        public void Reminders_persist_to_the_injected_file_and_drop_ones_long_past()
        {
            var path = TempFile();
            try
            {
                var now = T0;
                var r = new Reminders(path, () => now);
                r.Toggle(Prog("a", 60), Bbc);
                r.Toggle(Prog("b", 5), Bbc);
                var again = new Reminders(path, () => now);
                Assert.Equal(new[] { "b", "a" }, again.All.Select(x => x.ProgrammeId));
                Assert.Equal(T0.AddMinutes(60), again.All[1].Start);
                now = T0.AddMinutes(30); // b started 25 minutes ago
                Assert.Equal(new[] { "a" }, new Reminders(path, () => now).All.Select(x => x.ProgrammeId));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_reminder_is_due_once_a_minute_before_it_starts()
        {
            var now = T0;
            var r = new Reminders(null, () => now);
            r.Toggle(Prog("a", 10), Bbc);
            Assert.Null(r.Due());
            now = T0.AddMinutes(9).AddSeconds(1);
            Assert.Equal("a", r.Due()!.ProgrammeId);
            Assert.Null(r.Due()); // only once
        }

        [Fact]
        public void The_clock_opens_the_reminder_card_when_one_is_due()
        {
            var timers = new PlaybackControllerTests.FakeTimers { Now = T0 };
            var nav = new Navigator();
            var r = new Reminders(null, () => timers.Now);
            r.Toggle(Prog("a", 10), Bbc);
            new ReminderClock(r, nav, timers).Start();
            timers.Advance(TimeSpan.FromMinutes(8));
            Assert.Null(nav.State.Overlay);
            timers.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(10));
            Assert.Equal("reminder", nav.State.Overlay?.Name);
            Assert.Equal("a", ((Reminder)nav.State.Overlay!.Params!).ProgrammeId);
        }

        private static (ReminderCard Card, NewUiContext Ctx, PlaybackControllerTests.FakeTimers Timers, List<Channel> Tuned, List<string> Toasts) Card()
        {
            var engine = new PlaybackControllerTests.FakeEngine();
            var timers = new PlaybackControllerTests.FakeTimers { Now = T0.AddMinutes(9) };
            var playback = new PlaybackController(engine, a => a(), timers, () => timers.Now, null);
            var ctx = new NewUiContext(new Navigator(), playback, HomeDataTests.Build().Home, () => timers.Now) { Timers = timers, Channels = new[] { Bbc } };
            ctx.Reminders = new Reminders(null, () => timers.Now);
            var tuned = new List<Channel>(); var toasts = new List<string>();
            ctx.Tuning += tuned.Add; ctx.Toast = toasts.Add;
            ctx.Reminders.Toggle(Prog("a", 10), Bbc);
            var rem = ctx.Reminders.All.Single();
            ctx.Nav.OpenOverlay(new OverlayRef("reminder", rem));
            return (new ReminderCard(ctx, rem), ctx, timers, tuned, toasts);
        }

        [AvaloniaFact]
        public void The_card_says_what_and_when_and_counts_down()
        {
            var (card, _, timers, _, _) = Card();
            Assert.Contains("Show a", card.Text);
            Assert.Contains("BBC One", card.Text);
            Assert.Contains("1:00", card.Text);
            timers.Advance(TimeSpan.FromSeconds(15));
            Assert.Contains("0:45", card.Text);
        }

        [AvaloniaFact]
        public void Unless_dismissed_it_switches_at_the_start()
        {
            var (_, ctx, timers, tuned, _) = Card();
            timers.Advance(TimeSpan.FromSeconds(59));
            Assert.Empty(tuned);
            timers.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal("ch1", tuned.Single().Id);
            Assert.False(ctx.Reminders.Has("a"));
            Assert.True(ctx.Nav.State.AtVideo);
            Assert.Null(ctx.Nav.State.Overlay);
        }

        [AvaloniaFact]
        public void Switch_now_switches_straight_away()
        {
            var (card, _, _, tuned, _) = Card();
            card.OnAction(UiAction.Ok, false);
            Assert.Equal("ch1", tuned.Single().Id);
        }

        [AvaloniaFact]
        public void Dismiss_or_back_stays_put_and_never_switches()
        {
            var (card, ctx, timers, tuned, toasts) = Card();
            card.OnAction(UiAction.Right, false);
            card.OnAction(UiAction.Ok, false);
            Assert.Null(ctx.Nav.State.Overlay);
            Assert.False(ctx.Reminders.Has("a"));
            card.Destroy();
            timers.Advance(TimeSpan.FromMinutes(2));
            Assert.Empty(tuned);
            Assert.Contains("stay", toasts.Single());

            var (card2, ctx2, timers2, tuned2, _) = Card();
            card2.OnAction(UiAction.Back, false);
            card2.Destroy();
            timers2.Advance(TimeSpan.FromMinutes(2));
            Assert.Empty(tuned2);
            Assert.False(ctx2.Reminders.Has("a"));
        }
    }
}
