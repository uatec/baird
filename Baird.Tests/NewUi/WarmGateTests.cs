using System;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.NewUi.Playback;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class WarmGateTests
    {
        private sealed class Inputs
        {
            public bool Playing; public long Drops; public bool FullScreen;
            public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            public WarmGate Gate() => new(() => Playing, () => Drops, () => FullScreen, () => Now);
        }

        [Fact]
        public void Idle_runs()
        {
            var i = new Inputs();
            Assert.Equal(WarmMode.Run, i.Gate().Mode);
        }

        [Fact]
        public void Playing_is_slow_and_dropped_frames_pause()
        {
            var i = new Inputs { Playing = true };
            var g = i.Gate();
            Assert.Equal(WarmMode.Slow, g.Mode);
            i.Drops = 3;
            Assert.Equal(WarmMode.Paused, g.Mode);
            i.Now += TimeSpan.FromSeconds(9);
            Assert.Equal(WarmMode.Paused, g.Mode);
        }

        [Fact]
        public void Drops_stopping_goes_back_to_slow_after_the_window()
        {
            var i = new Inputs { Playing = true };
            var g = i.Gate();
            _ = g.Mode;
            i.Drops = 5;
            Assert.Equal(WarmMode.Paused, g.Mode);
            i.Now += WarmGate.DropWindow + TimeSpan.FromSeconds(1);
            Assert.Equal(WarmMode.Slow, g.Mode);
        }

        [Fact]
        public void A_stream_loading_is_slow_and_a_paused_one_runs()
        {
            // The shell's input: a channel change (loading) is the player's busiest moment, so warming is paced, never unpaced.
            var i = new Inputs { Playing = WarmGate.BusyPlaying(current: true, paused: false, loading: true) };
            i.FullScreen = WarmGate.IsFullScreenPlayback(playing: false, atVideo: true, overlay: false, pages: 0); // loading isn't "only the video"
            Assert.Equal(WarmMode.Slow, i.Gate().Mode);

            i.Playing = WarmGate.BusyPlaying(current: true, paused: true, loading: false); // paused: nothing to disturb
            Assert.Equal(WarmMode.Run, i.Gate().Mode);
            Assert.False(WarmGate.BusyPlaying(current: false, paused: false, loading: false));
            Assert.True(WarmGate.BusyPlaying(current: true, paused: false, loading: false));
        }

        [Fact]
        public void Home_closed_during_fullscreen_playback_pauses()
        {
            var i = new Inputs { Playing = true, FullScreen = true };
            var g = i.Gate();
            Assert.Equal(WarmMode.Paused, g.Mode);
            i.FullScreen = false;
            Assert.Equal(WarmMode.Slow, g.Mode);
            i.Playing = false; i.FullScreen = true;
            Assert.Equal(WarmMode.Run, g.Mode);
        }

        [Fact]
        public void A_counter_reset_for_a_new_video_is_not_a_drop()
        {
            var i = new Inputs { Playing = true, Drops = 50 };
            var g = i.Gate();
            Assert.Equal(WarmMode.Slow, g.Mode);
            i.Drops = 0;
            Assert.Equal(WarmMode.Slow, g.Mode);
        }

        [Fact]
        public void A_throwing_input_reads_as_slow()
        {
            var g = new WarmGate(() => true, () => throw new InvalidOperationException("torn down"), () => false, () => DateTimeOffset.UnixEpoch);
            Assert.Equal(WarmMode.Slow, g.Mode);
            var g2 = new WarmGate(() => throw new InvalidOperationException(), () => 0, () => false, () => DateTimeOffset.UnixEpoch);
            Assert.Equal(WarmMode.Slow, g2.Mode);
        }
    }
}
