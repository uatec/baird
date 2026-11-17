using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.NewUi.Playback;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ShowDataQueueTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
        private sealed class Gate : IWarmGate { public volatile WarmMode Value = WarmMode.Run; public WarmMode Mode => Value; }
        private sealed class Clock { public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); }

        private sealed class FakeTimers : ITimers
        {
            private readonly object _lock = new();
            private readonly Queue<Action> _pending = new();
            private readonly SemaphoreSlim _requested = new(0);
            public IDisposable After(TimeSpan delay, Action action)
            {
                lock (_lock) _pending.Enqueue(action);
                _requested.Release();
                return new Noop();
            }
            /// <summary>Waits until a timer is pending (without taking it).</summary>
            public async Task Arrived()
            {
                await _requested.WaitAsync(Wait).WaitAsync(Wait);
                _requested.Release();
            }
            public void FireOldest()
            {
                Action a;
                lock (_lock) { a = _pending.Dequeue(); _requested.Wait(0); }
                a();
            }
            private sealed class Noop : IDisposable { public void Dispose() { } }
        }

        private static async Task<(CatalogueTests.World W, ShowData Shows, Baird.NewUi.Model.CatalogueTitle Title, Clock Clock, WorkQueue Queue, Gate Gate, FakeTimers Timers)> Setup(WarmMode mode)
        {
            var w = CatalogueTests.Build();
            var clock = new Clock();
            var gate = new Gate { Value = mode };
            var timers = new FakeTimers();
            var queue = new WorkQueue(timers, gate);
            var shows = new ShowData(w.Data, w.Seerr, w.Search, TimeSpan.FromMilliseconds(200), clock: () => clock.Now);
            shows.RefreshSink = (key, run) => queue.Submit(new[] { new WorkItem(key, Rank.Page, run) });
            var r = await w.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow);
            return (w, shows, r.Top!.Title!, clock, queue, gate, timers);
        }

        [Fact]
        public async Task A_stale_refresh_runs_once_through_the_queue_and_the_cache_becomes_fresh()
        {
            var (w, shows, title, clock, queue, _, _) = await Setup(WarmMode.Run);
            using var _q = queue;
            await shows.LibrarySeasonsAsync(title);
            clock.Now += TimeSpan.FromSeconds(61);
            w.Jellyfin.Children["simp-s2"].Add(CatalogueTests.Item("s2e2", "Another"));
            var calls = w.Jellyfin.ChildrenCalls;

            await shows.LibrarySeasonsAsync(title); // stale: served at once, refresh queued
            await WaitUntil(async () => (await shows.LibrarySeasonsAsync(title)).First(x => x.Number == 2).Episodes.Count == 2);
            await queue.Idle.WaitAsync(Wait);

            var after = w.Jellyfin.ChildrenCalls;
            Assert.True(after > calls);
            await shows.LibrarySeasonsAsync(title); // fresh now: nobody is asked again
            await Task.Delay(100);
            Assert.Equal(after, w.Jellyfin.ChildrenCalls);
        }

        [Fact]
        public async Task A_paused_gate_holds_the_refresh_until_it_opens()
        {
            var (w, shows, title, clock, queue, gate, timers) = await Setup(WarmMode.Run);
            using var _q = queue;
            await shows.LibrarySeasonsAsync(title);
            clock.Now += TimeSpan.FromSeconds(61);
            w.Jellyfin.Children["simp-s2"].Add(CatalogueTests.Item("s2e2", "Another"));
            gate.Value = WarmMode.Paused;
            var calls = w.Jellyfin.ChildrenCalls;

            await shows.LibrarySeasonsAsync(title);
            await timers.Arrived();      // the worker is waiting out its pause
            timers.FireOldest();
            await timers.Arrived();      // it looked again, still paused, and waits once more: the job has not started
            Assert.Equal(calls, w.Jellyfin.ChildrenCalls);

            gate.Value = WarmMode.Run;
            timers.FireOldest();
            await WaitUntil(async () => (await shows.LibrarySeasonsAsync(title)).First(x => x.Number == 2).Episodes.Count == 2);
            Assert.True(w.Jellyfin.ChildrenCalls > calls);
        }

        private static async Task WaitUntil(Func<Task<bool>> cond)
        {
            var until = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < until)
            {
                if (await cond()) return;
                await Task.Delay(20);
            }
            Assert.Fail("condition never became true");
        }
    }
}
