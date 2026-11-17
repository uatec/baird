using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Baird.NewUi.Playback;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class WarmGateWiringTests
    {
        [Fact]
        public void Only_video_alone_is_full_screen()
        {
            Assert.True(WarmGate.IsFullScreenPlayback(playing: true, atVideo: true, overlay: false, pages: 0));
            Assert.False(WarmGate.IsFullScreenPlayback(true, true, false, pages: 1)); // a show page over the video
            Assert.False(WarmGate.IsFullScreenPlayback(true, false, false, 0));       // Home over the video
            Assert.False(WarmGate.IsFullScreenPlayback(true, true, overlay: true, 0));
            Assert.False(WarmGate.IsFullScreenPlayback(false, true, false, 0));
        }

        [Fact]
        public async Task A_faulting_sample_does_not_throw_out_of_the_tick()
        {
            long stored = -1;
            var s = new AvSampler(() => throw new InvalidOperationException("mpv gone"), v => stored = v);
            await s.TickAsync();
            Assert.Equal(-1, stored);
            var s2 = new AvSampler(() => Task.FromException<long?>(new InvalidOperationException()), v => stored = v);
            await s2.TickAsync();
            Assert.Equal(-1, stored);
        }

        [Fact]
        public async Task A_tick_while_a_sample_is_in_flight_is_skipped()
        {
            var calls = 0;
            var gate = new TaskCompletionSource<long?>();
            long stored = -1;
            var s = new AvSampler(() => { calls++; return gate.Task; }, v => stored = v);
            var first = s.TickAsync();
            await s.TickAsync();
            Assert.Equal(1, calls);
            gate.SetResult(7);
            await first;
            Assert.Equal(7, stored);
            await s.TickAsync();
            Assert.Equal(2, calls);
        }

        [AvaloniaFact]
        public async Task Dispatcher_timers_can_be_asked_from_a_pool_thread_and_still_fire()
        {
            Exception? thrown = null;
            var fired = new TaskCompletionSource();
            await Task.Run(() =>
            {
                try { new DispatcherTimers().After(TimeSpan.FromMilliseconds(20), () => fired.TrySetResult()); }
                catch (Exception ex) { thrown = ex; }
            });
            Assert.Null(thrown);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!fired.Task.IsCompleted && !cts.IsCancellationRequested)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Assert.True(fired.Task.IsCompleted);
        }
    }
}
