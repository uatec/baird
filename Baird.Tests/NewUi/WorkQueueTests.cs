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
    public class WorkQueueTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        private sealed class Gate : IWarmGate { public WarmMode Mode { get; set; } = WarmMode.Run; }

        private sealed class ThrowingGate : IWarmGate
        {
            public int Throws;
            public int Reads;
            public WarmMode Mode { get { Reads++; if (Throws > 0) { Throws--; throw new InvalidOperationException("torn down"); } return WarmMode.Run; } }
        }

        private sealed class ThrowingTimers : ITimers { public IDisposable After(TimeSpan delay, Action action) => throw new InvalidOperationException("no dispatcher"); }

        /// <summary>Records each requested delay; the test fires them by hand. No clock, no sleeping.</summary>
        private sealed class RecordingTimers : ITimers
        {
            private readonly object _lock = new();
            private readonly Queue<Action> _pending = new();
            private readonly SemaphoreSlim _requested = new(0);
            private readonly List<TimeSpan> _delays = new();
            public IReadOnlyList<TimeSpan> Delays { get { lock (_lock) return _delays.ToList(); } }
            public IDisposable After(TimeSpan delay, Action action)
            {
                lock (_lock) { _delays.Add(delay); _pending.Enqueue(action); }
                _requested.Release();
                return new Noop();
            }
            public async Task<TimeSpan> NextRequest()
            {
                await _requested.WaitAsync(Wait).WaitAsync(Wait);
                lock (_lock) return _delays[^1];
            }
            public void FireOldest() { Action a; lock (_lock) a = _pending.Dequeue(); a(); }
            private sealed class Noop : IDisposable { public void Dispose() { } }
        }

        [Fact]
        public async Task Pending_counts_what_is_running_and_what_waits_and_falls_back_to_zero()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var q = new WorkQueue(new ThrowingTimers(), new Gate());
            Assert.Equal(0, q.Pending);
            q.Submit(new[] { new WorkItem("a", Rank.Page, async ct => { started.TrySetResult(); await release.Task.WaitAsync(ct); }) });
            await started.Task.WaitAsync(Wait);
            q.Submit(new[] { new WorkItem("b", Rank.Child, _ => Task.CompletedTask), new WorkItem("c", Rank.Child, _ => Task.CompletedTask) });
            Assert.Equal(3, q.Pending);                       // one running, two waiting
            release.SetResult();
            await q.Idle.WaitAsync(Wait);
            Assert.Equal(0, q.Pending);
        }

        private sealed class Harness : IDisposable
        {
            public readonly Gate Gate = new();
            public readonly RecordingTimers Timers = new();
            public readonly WorkQueue Queue;
            public readonly List<string> Ran = new();
            private readonly TaskCompletionSource _blockerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Harness(int capacity = 200) { Queue = new WorkQueue(Timers, Gate, capacity); }

            public WorkItem Job(string key, Rank rank = Rank.Page) => new(key, rank, _ => { lock (Ran) Ran.Add(key); return Task.CompletedTask; });

            /// <summary>Submits a job that holds the worker until Release, so later submissions queue up.</summary>
            public async Task Block()
            {
                Queue.Submit(new[] { new WorkItem("blocker", Rank.Page, async ct => { _blockerStarted.TrySetResult(); await _release.Task.WaitAsync(ct); }) });
                await _blockerStarted.Task.WaitAsync(Wait);
            }

            public void Release() => _release.TrySetResult();
            public async Task Drain() { Release(); await Queue.Idle.WaitAsync(Wait); }
            public void Dispose() => Queue.Dispose();
        }

        [Fact]
        public async Task A_newer_batch_runs_before_an_older_one()
        {
            using var h = new Harness();
            await h.Block();
            h.Queue.Submit(new[] { h.Job("old1"), h.Job("old2") });
            h.Queue.Submit(new[] { h.Job("new1"), h.Job("new2") });
            await h.Drain();
            Assert.Equal(new[] { "new1", "new2", "old1", "old2" }, h.Ran);
        }

        [Fact]
        public async Task Within_a_batch_page_then_children_then_siblings()
        {
            using var h = new Harness();
            await h.Block();
            h.Queue.Submit(new[] { h.Job("sib", Rank.Sibling), h.Job("kid1", Rank.Child), h.Job("page", Rank.Page), h.Job("kid2", Rank.Child) });
            await h.Drain();
            Assert.Equal(new[] { "page", "kid1", "kid2", "sib" }, h.Ran);
        }

        [Fact]
        public async Task Urgent_jumps_the_queue()
        {
            using var h = new Harness();
            await h.Block();
            h.Queue.Submit(new[] { h.Job("a"), h.Job("b") });
            h.Queue.Urgent(h.Job("u1", Rank.Sibling));
            h.Queue.Urgent(h.Job("u2", Rank.Sibling));
            await h.Drain();
            Assert.Equal(new[] { "u2", "u1", "a", "b" }, h.Ran); // most recent urgent first
        }

        [Fact]
        public async Task Duplicate_keys_merge_keeping_the_better_rank_and_newer_batch()
        {
            using var h = new Harness();
            await h.Block();
            h.Queue.Submit(new[] { h.Job("A", Rank.Page), h.Job("B", Rank.Page), h.Job("D", Rank.Sibling) });
            h.Queue.Submit(new[] { h.Job("A", Rank.Sibling), h.Job("C", Rank.Sibling), h.Job("D", Rank.Page) });
            await h.Drain();
            // A keeps Page (better) and moves to the newer batch; D is upgraded to Page (A first: submitted first); B stays behind.
            Assert.Equal(new[] { "A", "D", "C", "B" }, h.Ran);
        }

        [Fact]
        public async Task The_201st_oldest_item_is_dropped()
        {
            using var h = new Harness();
            await h.Block();
            h.Queue.Submit(Enumerable.Range(0, 200).Select(i => h.Job($"o{i}")).ToList());
            h.Queue.Submit(new[] { h.Job("n") });
            await h.Drain();
            Assert.Equal(200, h.Ran.Count);
            Assert.DoesNotContain("o199", h.Ran);
            Assert.Equal("n", h.Ran[0]);
            Assert.Equal("o198", h.Ran[^1]);
        }

        [Fact]
        public async Task Slow_mode_waits_between_jobs_and_Paused_runs_nothing()
        {
            using var h = new Harness();
            h.Gate.Mode = WarmMode.Slow;
            h.Queue.Submit(new[] { h.Job("a"), h.Job("b") });
            Assert.Equal(TimeSpan.FromSeconds(1.5), await h.Timers.NextRequest());
            Assert.Equal(new[] { "a" }, h.Ran);
            h.Timers.FireOldest();
            await h.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "a", "b" }, h.Ran);

            h.Gate.Mode = WarmMode.Paused;
            h.Queue.Submit(new[] { h.Job("c") });
            await h.Timers.NextRequest();
            Assert.Equal(new[] { "a", "b" }, h.Ran);
            Assert.False(h.Queue.Idle.IsCompleted);
        }

        [Fact]
        public async Task One_job_runs_at_a_time()
        {
            using var h = new Harness();
            int current = 0, max = 0;
            WorkItem J(string k) => new(k, Rank.Page, async _ =>
            {
                var now = Interlocked.Increment(ref current);
                int seen; while ((seen = Volatile.Read(ref max)) < now && Interlocked.CompareExchange(ref max, now, seen) != seen) { }
                await Task.Yield();
                await Task.Yield();
                Interlocked.Decrement(ref current);
            });
            h.Queue.Submit(Enumerable.Range(0, 20).Select(i => J($"j{i}")).ToList());
            await h.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(1, max);
        }

        [Fact]
        public async Task Slow_gap_and_Paused_recheck_use_the_timers_exactly()
        {
            using var h = new Harness();
            h.Gate.Mode = WarmMode.Paused;
            h.Queue.Submit(new[] { h.Job("a"), h.Job("b") });
            Assert.Equal(TimeSpan.FromSeconds(1), await h.Timers.NextRequest());
            h.Timers.FireOldest();
            Assert.Equal(TimeSpan.FromSeconds(1), await h.Timers.NextRequest()); // still paused: asks again
            Assert.Empty(h.Ran);
            h.Gate.Mode = WarmMode.Slow;
            h.Timers.FireOldest();
            Assert.Equal(TimeSpan.FromSeconds(1.5), await h.Timers.NextRequest()); // a ran, gap before b
            Assert.Equal(new[] { "a" }, h.Ran);
            h.Gate.Mode = WarmMode.Run;
            h.Timers.FireOldest();
            await h.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "a", "b" }, h.Ran);
            Assert.Equal(new[] { 1.0, 1.0, 1.5 }, h.Timers.Delays.Select(d => d.TotalSeconds));
        }

        [Fact]
        public async Task A_failing_job_does_not_stop_the_queue()
        {
            using var h = new Harness();
            h.Queue.Submit(new[] { new WorkItem("bad", Rank.Page, _ => throw new InvalidOperationException("boom")), new WorkItem("bad2", Rank.Page, async _ => { await Task.Yield(); throw new InvalidOperationException("boom2"); }), h.Job("good") });
            await h.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "good" }, h.Ran);
        }

        [Fact]
        public async Task Urgent_ignores_the_gate_even_when_paused()
        {
            using var h = new Harness();
            h.Gate.Mode = WarmMode.Paused;
            h.Queue.Urgent(h.Job("u"));
            await h.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "u" }, h.Ran);
            Assert.Empty(h.Timers.Delays);
        }

        [Fact]
        public async Task UrgentUnlessPaused_waits_its_turn_while_paused_and_jumps_the_queue_otherwise()
        {
            using var h = new Harness();
            h.Gate.Mode = WarmMode.Paused;
            Assert.False(h.Queue.UrgentUnlessPaused(h.Job("lit", Rank.Page)));
            Assert.Equal(WorkQueue.PausedRecheck, await h.Timers.NextRequest()); // held by the gate like any batch
            Assert.Empty(h.Ran);
            h.Gate.Mode = WarmMode.Run;
            h.Timers.FireOldest();
            await h.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "lit" }, h.Ran);

            using var h2 = new Harness();
            await h2.Block();
            h2.Queue.Submit(new[] { h2.Job("a") });
            Assert.True(h2.Queue.UrgentUnlessPaused(h2.Job("lit", Rank.Page))); // open gate: urgent, ahead of the batch
            await h2.Drain();
            Assert.Equal(new[] { "lit", "a" }, h2.Ran);

            using var h3 = new Harness();
            h3.Gate.Mode = WarmMode.Slow;                                       // video playing, no drops: still urgent
            Assert.True(h3.Queue.UrgentUnlessPaused(h3.Job("lit", Rank.Page)));
            await h3.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "lit" }, h3.Ran);
        }

        [Fact]
        public async Task A_running_job_is_not_cancelled_by_a_new_batch()
        {
            using var h = new Harness();
            CancellationToken seen = default;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Queue.Submit(new[] { new WorkItem("long", Rank.Page, async ct => { seen = ct; started.SetResult(); await release.Task; lock (h.Ran) h.Ran.Add("long"); }) });
            await started.Task.WaitAsync(Wait);
            h.Queue.Submit(new[] { h.Job("x") });
            h.Queue.Urgent(h.Job("u"));
            Assert.False(seen.IsCancellationRequested);
            release.SetResult();
            await h.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "long", "u", "x" }, h.Ran);
        }

        [Fact]
        public async Task A_key_that_is_running_is_not_queued_again()
        {
            using var h = new Harness();
            await h.Block();
            h.Queue.Submit(new[] { h.Job("blocker") });
            await h.Drain();
            Assert.Empty(h.Ran);
        }

        [Fact]
        public async Task Dispose_stops_the_worker()
        {
            var h = new Harness();
            CancellationToken seen = default;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Queue.Submit(new[] { new WorkItem("long", Rank.Page, async ct => { seen = ct; started.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { finished.SetResult(); } }), h.Job("x") });
            await started.Task.WaitAsync(Wait);
            h.Queue.Dispose();
            await finished.Task.WaitAsync(Wait);
            Assert.True(seen.IsCancellationRequested);
            await h.Queue.Idle.WaitAsync(Wait);
            h.Queue.Submit(new[] { h.Job("y") });
            h.Queue.Urgent(h.Job("z"));
            await h.Queue.Idle.WaitAsync(Wait);
            Assert.Empty(h.Ran);
        }

        [Fact]
        public async Task A_gate_that_throws_is_treated_as_Slow_and_the_queue_keeps_going()
        {
            var gate = new ThrowingGate { Throws = 1 };
            var timers = new RecordingTimers();
            using var q = new WorkQueue(timers, gate);
            var ran = new List<string>();
            WorkItem J(string k) => new(k, Rank.Page, _ => { lock (ran) ran.Add(k); return Task.CompletedTask; });
            q.Submit(new[] { J("a"), J("b") });
            await q.Idle.WaitAsync(Wait); // the throw reads as Slow, but the first job of a busy period has no gap before it
            Assert.Equal(new[] { "a", "b" }, ran);
            gate.Throws = 1;
            q.Submit(new[] { J("c") });
            await q.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "a", "b", "c" }, ran);
        }

        [Fact]
        public async Task A_gate_that_throws_once_does_not_leave_the_worker_dead()
        {
            var gate = new ThrowingGate { Throws = 1 };
            using var q = new WorkQueue(new RecordingTimers(), gate);
            var ran = new List<string>();
            WorkItem J(string k) => new(k, Rank.Page, _ => { lock (ran) ran.Add(k); return Task.CompletedTask; });
            q.Submit(new[] { J("a") });
            await q.Idle.WaitAsync(Wait);
            q.Submit(new[] { J("b") });
            await q.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "a", "b" }, ran);
        }

        [Fact]
        public async Task Timers_that_throw_do_not_kill_the_worker()
        {
            var gate = new Gate { Mode = WarmMode.Slow };
            using var q = new WorkQueue(new ThrowingTimers(), gate);
            var ran = new List<string>();
            WorkItem J(string k) => new(k, Rank.Page, _ => { lock (ran) ran.Add(k); return Task.CompletedTask; });
            q.Submit(new[] { J("a"), J("b") }); // Slow gap cannot be timed: skipped
            await q.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "a", "b" }, ran);
            // Paused with broken timers falls back to Task.Delay, and urgent work still wakes it at once.
            gate.Mode = WarmMode.Paused;
            q.Submit(new[] { J("c") });
            q.Urgent(J("u"));
            await Task.Run(async () => { while (true) { lock (ran) if (ran.Contains("u")) return; await Task.Yield(); } }).WaitAsync(Wait);
            Assert.DoesNotContain("c", ran);
        }

        [Fact]
        public async Task A_submit_during_a_Slow_or_Paused_wait_is_not_stranded()
        {
            using var h = new Harness();
            h.Gate.Mode = WarmMode.Slow;
            h.Queue.Submit(new[] { h.Job("a"), h.Job("b") });
            await h.Timers.NextRequest();
            h.Queue.Submit(new[] { h.Job("c") });
            h.Timers.FireOldest();
            await h.Timers.NextRequest(); // gap before b
            h.Timers.FireOldest();
            await h.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "a", "c", "b" }, h.Ran);

            h.Gate.Mode = WarmMode.Paused;
            h.Queue.Submit(new[] { h.Job("d") });
            await h.Timers.NextRequest();
            h.Queue.Submit(new[] { h.Job("e") });
            h.Gate.Mode = WarmMode.Run;
            h.Timers.FireOldest();
            await h.Queue.Idle.WaitAsync(Wait);
            Assert.Equal(new[] { "a", "c", "b", "e", "d" }, h.Ran);
        }

        [Fact]
        public async Task An_urgent_during_a_Paused_wait_wakes_the_worker_at_once()
        {
            using var h = new Harness();
            h.Gate.Mode = WarmMode.Paused;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Queue.Submit(new[] { h.Job("a") });
            await h.Timers.NextRequest();
            h.Queue.Urgent(new WorkItem("u", Rank.Page, _ => { lock (h.Ran) h.Ran.Add("u"); done.SetResult(); return Task.CompletedTask; }));
            await done.Task.WaitAsync(Wait); // no timer fired
            Assert.Equal(new[] { "u" }, h.Ran);
            await h.Timers.NextRequest(); // back to waiting on the pause for "a"
            Assert.Equal(new[] { "u" }, h.Ran);
        }
    }
}
