using System;
using System.Collections.Generic;
using System.Linq;
using Baird.Mpv;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.Playback;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class PlaybackControllerTests
    {
        internal sealed class FakeEngine : IPlaybackEngine
        {
            public readonly List<(string Url, double? Start)> Plays = new();
            public readonly List<string> Calls = new();
            public PlaybackState State { get; set; } = PlaybackState.Idle;
            public double TimePositionSeconds { get; set; }
            public double DurationSeconds { get; set; }
            public event EventHandler? FileLoaded;
            public event EventHandler? StreamEnded;
            public event EventHandler<bool>? PauseStateChanged;
            public event EventHandler<double>? DurationChanged;
            public void Play(string url, double? startSeconds = null) { Plays.Add((url, startSeconds)); State = PlaybackState.Loading; }
            public void Pause() { Calls.Add("pause"); State = PlaybackState.Paused; PauseStateChanged?.Invoke(this, true); }
            public void Resume() { Calls.Add("resume"); State = PlaybackState.Playing; PauseStateChanged?.Invoke(this, false); }
            public void Seek(double s) => Calls.Add($"seek {s}");
            public void SeekRelative(double s) => Calls.Add($"rel {s}");
            public void SetSubtitle(bool e) => Calls.Add($"subs {e}");
            public void Stop() { Calls.Add("stop"); State = PlaybackState.Idle; }
            public void Loaded() { State = PlaybackState.Playing; FileLoaded?.Invoke(this, EventArgs.Empty); }
            public void Ended() => StreamEnded?.Invoke(this, EventArgs.Empty);
            public void Duration(double d) { DurationSeconds = d; DurationChanged?.Invoke(this, d); }
        }

        /// <summary>A manual clock: timers fire when time is advanced past them.</summary>
        internal sealed class FakeTimers : ITimers
        {
            public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
            private readonly List<(DateTimeOffset At, Action Run, bool[] Cancelled)> _pending = new();
            public IDisposable After(TimeSpan delay, Action action)
            {
                var cancelled = new bool[1];
                _pending.Add((Now + delay, action, cancelled));
                return new Cancel(cancelled);
            }
            public void Advance(TimeSpan by)
            {
                Now += by;
                foreach (var t in _pending.Where(p => p.At <= Now && !p.Cancelled[0]).ToList()) { t.Cancelled[0] = true; t.Run(); }
            }
            private sealed class Cancel : IDisposable { private readonly bool[] _c; public Cancel(bool[] c) => _c = c; public void Dispose() => _c[0] = true; }
        }

        private static Channel Ch(string n) => new($"ch{n}", n, $"Channel {n}", null, $"http://tvh/{n}");

        private static (PlaybackController C, FakeEngine E, FakeTimers T, List<(string, TimeSpan)> Saved) Build()
        {
            var e = new FakeEngine(); var t = new FakeTimers(); var saved = new List<(string, TimeSpan)>();
            var c = new PlaybackController(e, a => a(), t, () => t.Now, (id, pos, dur) => saved.Add((id, pos)));
            return (c, e, t, saved);
        }

        [Fact]
        public void Play_item_loads_then_plays()
        {
            var (c, e, _, _) = Build();
            c.PlayItem("ep1", "http://jf/ep1", TimeSpan.FromMinutes(10), "Loki");
            Assert.True(c.IsLoading);
            Assert.Equal(("http://jf/ep1", (double?)600), e.Plays.Single());
            e.Loaded();
            Assert.False(c.IsLoading);
            Assert.Equal(PlaybackState.Playing, c.State);
            Assert.Equal("ep1", c.Current!.ItemId);
        }

        [Fact]
        public void Load_timeout_retries_then_fails()
        {
            var (c, e, t, _) = Build();
            c.PlayItem("ep1", "http://jf/ep1", TimeSpan.Zero, "Loki");
            for (var i = 0; i < 3; i++) t.Advance(PlaybackController.LoadTimeout + TimeSpan.FromMilliseconds(1));
            Assert.Equal(4, e.Plays.Count); // first try + 3 retries
            t.Advance(PlaybackController.LoadTimeout + TimeSpan.FromMilliseconds(1));
            Assert.False(c.IsLoading);
            Assert.NotNull(c.Failure);
        }

        [Fact]
        public void Rapid_channel_changes_play_only_the_last()
        {
            var (c, e, t, _) = Build();
            c.PlayLive(Ch("101")); t.Advance(TimeSpan.FromMilliseconds(100));
            c.PlayLive(Ch("102")); t.Advance(TimeSpan.FromMilliseconds(100));
            c.PlayLive(Ch("103"));
            Assert.Equal("103", c.Current!.Channel!.Number); // the ident can show it straight away
            Assert.Empty(e.Plays);
            t.Advance(PlaybackController.TuneDebounce + TimeSpan.FromMilliseconds(1));
            Assert.Equal("http://tvh/103", e.Plays.Single().Url);
        }

        [Fact]
        public void Pausing_live_tv_counts_time_behind_live_and_seeking_forward_reduces_it()
        {
            var (c, e, t, _) = Build();
            c.PlayLive(Ch("101")); t.Advance(TimeSpan.FromSeconds(1)); e.Loaded();
            c.TogglePause(); t.Advance(TimeSpan.FromSeconds(40)); c.TogglePause();
            Assert.Equal(TimeSpan.FromSeconds(40), c.BehindLive);
            c.SeekRelative(TimeSpan.FromSeconds(30));
            Assert.Equal(TimeSpan.FromSeconds(10), c.BehindLive);
            c.SeekRelative(TimeSpan.FromSeconds(30));
            Assert.Equal(TimeSpan.Zero, c.BehindLive);
            Assert.Contains("rel 30", e.Calls);
        }

        [Fact]
        public void Switching_items_saves_progress_of_the_one_left_once()
        {
            var (c, e, t, saved) = Build();
            c.PlayItem("ep1", "http://jf/ep1", TimeSpan.Zero, "Loki"); e.Loaded();
            e.TimePositionSeconds = 300; e.Duration(3000);
            t.Advance(PlaybackController.WatchedAfter); saved.Clear();
            c.PlayItem("film1", "http://jf/film1", TimeSpan.Zero, "Casper");
            Assert.Equal(new[] { ("ep1", TimeSpan.FromSeconds(300)) }, saved);
        }

        [Fact]
        public void Under_a_minute_is_not_saved()
        {
            var (c, e, t, saved) = Build();
            c.PlayItem("ep1", "http://jf/ep1", TimeSpan.Zero, "Loki"); e.Loaded();
            e.TimePositionSeconds = 40; t.Advance(TimeSpan.FromSeconds(59)); c.Stop();
            Assert.Empty(saved);
        }

        [Fact]
        public void A_minute_of_a_video_is_saved_as_the_minute_passes_and_again_on_leaving()
        {
            var (c, e, t, saved) = Build();
            c.PlayItem("ep1", "http://jf/ep1", TimeSpan.Zero, "Loki"); e.Loaded();
            e.TimePositionSeconds = 60; t.Advance(PlaybackController.WatchedAfter);
            Assert.Equal(new[] { ("ep1", TimeSpan.FromSeconds(60)) }, saved);
            e.TimePositionSeconds = 300; c.Stop();
            Assert.Equal(new[] { ("ep1", TimeSpan.FromSeconds(60)), ("ep1", TimeSpan.FromSeconds(300)) }, saved);
        }

        [Fact]
        public void A_channel_rested_on_for_a_minute_is_saved_but_flicking_past_is_not()
        {
            var (c, e, t, saved) = Build();
            c.PlayLive(Ch("1")); t.Advance(PlaybackController.TuneDebounce); e.Loaded();
            t.Advance(TimeSpan.FromSeconds(5));
            c.PlayLive(Ch("2")); t.Advance(PlaybackController.TuneDebounce); e.Loaded();
            Assert.Empty(saved);
            t.Advance(PlaybackController.WatchedAfter);
            Assert.Equal(new[] { "ch2" }, saved.Select(s => s.Item1));
        }

        [Fact]
        public void Stream_end_saves_and_raises_ended()
        {
            var (c, e, _, saved) = Build();
            var ended = 0; c.Ended += () => ended++;
            c.PlayItem("ep1", "http://jf/ep1", TimeSpan.Zero, "Loki"); e.Loaded();
            e.TimePositionSeconds = 2990; e.Duration(3000); e.Ended();
            Assert.Equal(1, ended);
            Assert.Single(saved);
        }

        [Fact]
        public void An_old_channels_load_timeout_does_not_cut_short_the_next_tune()
        {
            var (c, e, t, _) = Build();
            c.PlayLive(Ch("101"));
            t.Advance(PlaybackController.TuneDebounce);
            t.Advance(PlaybackController.LoadTimeout - TimeSpan.FromMilliseconds(100));
            c.PlayLive(Ch("102"));
            t.Advance(TimeSpan.FromMilliseconds(200)); // the old load timer's moment, inside the new tune's debounce
            Assert.Equal(new[] { "http://tvh/101" }, e.Plays.Select(p => p.Url));
            t.Advance(PlaybackController.TuneDebounce);
            Assert.Equal(new[] { "http://tvh/101", "http://tvh/102" }, e.Plays.Select(p => p.Url));
        }

        [Fact]
        public void A_live_stream_that_drops_is_retuned_rather_than_ended()
        {
            var (c, e, t, _) = Build();
            var ended = 0; c.Ended += () => ended++;
            c.PlayLive(Ch("101"));
            t.Advance(PlaybackController.TuneDebounce);
            e.Loaded();
            t.Advance(TimeSpan.FromMinutes(5));
            e.Ended();
            Assert.Equal(0, ended);
            Assert.Equal(new[] { "http://tvh/101", "http://tvh/101" }, e.Plays.Select(p => p.Url));
        }

        [Fact]
        public void A_live_stream_that_keeps_dropping_at_once_gives_up_and_says_so()
        {
            var (c, e, t, _) = Build();
            c.PlayLive(Ch("101"));
            t.Advance(PlaybackController.TuneDebounce);
            for (var i = 0; i < 6; i++) { e.Loaded(); t.Advance(TimeSpan.FromSeconds(1)); e.Ended(); }
            Assert.NotNull(c.Failure);
            Assert.True(e.Plays.Count <= 4, $"{e.Plays.Count} plays");
        }

        [Fact]
        public void Mpv_is_set_up_for_live_or_on_demand_before_each_play()
        {
            var e = new FakeEngine(); var t = new FakeTimers(); var configured = new List<bool>();
            var c = new PlaybackController(e, a => a(), t, () => t.Now, null, configured.Add);
            c.PlayLive(Ch("101"));
            t.Advance(PlaybackController.TuneDebounce);
            c.PlayItem("film", "http://jf/film", TimeSpan.Zero, "Film");
            Assert.Equal(new[] { true, false }, configured);
        }

        [Fact]
        public void A_browser_service_gets_no_load_timeout_retries()
        {
            var e = new FakeEngine(); var nf = new EngineRouterTests.FakeBrowser("Netflix", "https://www.netflix.com/"); var t = new FakeTimers();
            var c = new PlaybackController(new EngineRouter(e, new[] { nf }), a => a(), t, () => t.Now, null);
            c.PlayItem("nf1", "https://www.netflix.com/watch/1", TimeSpan.Zero, "Stranger Things");
            t.Advance(TimeSpan.FromMinutes(2));
            Assert.Single(nf.Calls, x => x.StartsWith("play"));
            Assert.Null(c.Failure);
        }

        [Fact]
        public void A_browser_services_failure_is_shown()
        {
            var e = new FakeEngine(); var nf = new EngineRouterTests.FakeBrowser("Netflix", "https://www.netflix.com/"); var t = new FakeTimers();
            var c = new PlaybackController(new EngineRouter(e, new[] { nf }), a => a(), t, () => t.Now, null);
            c.PlayItem("nf1", "https://www.netflix.com/watch/1", TimeSpan.Zero, "Stranger Things");
            nf.Fail("Netflix asked for a verification code.");
            Assert.Equal("Netflix asked for a verification code.", c.Failure);
            Assert.False(c.IsLoading);
        }
    }
}
