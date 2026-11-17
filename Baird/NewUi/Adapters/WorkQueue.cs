using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Playback;

namespace Baird.NewUi.Adapters
{
    public enum WarmMode { Run, Slow, Paused }

    /// <summary>Says how busy the app is right now; the queue reads it before starting each job.</summary>
    public interface IWarmGate { WarmMode Mode { get; } }

    public enum Rank { Page = 0, Child = 1, Sibling = 2 }

    public sealed record WorkItem(string Key, Rank Rank, Func<CancellationToken, Task> Run);

    /// <summary>
    /// One prioritised queue for everything the show data layer fetches in the background (refreshes and warming).
    /// Order: urgent items first (newest first), then batches newest-first, within a batch Page, Child, Sibling,
    /// ties in submission order. One job at a time on a single pool worker. A running job is never cancelled by
    /// new submissions (only Dispose cancels it).
    ///
    /// Keys: submitting a key that is already queued leaves one entry with the better rank, in the newer batch.
    /// An urgent item replaces the queued entry for its key. A key whose job is running right now is skipped
    /// (not queued again): the running job is already fetching it, so a second run straight after is wasted work.
    ///
    /// Gate: Run = next job straight away; Slow = 1.5 s gap between jobs; Paused = start nothing, re-check every 1 s.
    /// Urgent items ignore the gate entirely (Slow and Paused): they are what the viewer just asked for on screen,
    /// so holding them back for background-load reasons would defeat the point of an urgent lane.
    ///
    /// Over capacity the item that would run last is dropped (the running job does not count).
    /// </summary>
    public sealed class WorkQueue : IDisposable
    {
        public static readonly TimeSpan SlowGap = TimeSpan.FromSeconds(1.5);
        public static readonly TimeSpan PausedRecheck = TimeSpan.FromSeconds(1);

        private sealed class Entry
        {
            public WorkItem Item = null!;
            public long Batch;
            public long Seq;
            public Rank Rank;
            public bool Urgent;
        }

        private readonly ITimers _timers;
        private readonly IWarmGate _gate;
        private readonly int _capacity;
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Entry> _queue = new();
        private long _batchCounter;
        private long _seqCounter;
        private bool _workerRunning;
        private bool _disposed;
        private string? _runningKey;
        private TaskCompletionSource _idle = Completed();
        private TaskCompletionSource _wake = New();

        public WorkQueue(ITimers timers, IWarmGate gate, int capacity = 200)
        {
            _timers = timers;
            _gate = gate;
            _capacity = Math.Max(1, capacity);
        }

        /// <summary>Completes when nothing is queued and nothing is running. A fresh pending task appears whenever work arrives.</summary>
        public Task Idle { get { lock (_lock) return _idle.Task; } }

        public void Submit(IReadOnlyList<WorkItem> batch)
        {
            lock (_lock)
            {
                if (_disposed || batch.Count == 0) return;
                var id = ++_batchCounter;
                foreach (var item in batch)
                {
                    if (item.Key == _runningKey) continue;
                    var existing = _queue.Find(e => e.Item.Key == item.Key);
                    if (existing != null)
                    {
                        if (existing.Urgent) continue; // already at the front; nothing to improve
                        _queue.Remove(existing);
                        _queue.Add(new Entry { Item = item, Batch = id, Seq = ++_seqCounter, Rank = (Rank)Math.Min((int)existing.Rank, (int)item.Rank) });
                    }
                    else
                    {
                        _queue.Add(new Entry { Item = item, Batch = id, Seq = ++_seqCounter, Rank = item.Rank });
                    }
                }
                Trim();
                Arrived();
            }
        }

        public void Urgent(WorkItem item)
        {
            TaskCompletionSource? wake;
            lock (_lock)
            {
                if (_disposed || item.Key == _runningKey) return;
                _queue.RemoveAll(e => e.Item.Key == item.Key);
                _queue.Add(new Entry { Item = item, Batch = ++_batchCounter, Seq = ++_seqCounter, Rank = item.Rank, Urgent = true });
                Trim();
                Arrived();
                wake = _wake;
                _wake = New();
            }
            wake.TrySetResult(); // cut short a Slow/Paused wait: urgent work does not obey the gate
        }

        public void Dispose()
        {
            TaskCompletionSource idle, wake;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _queue.Clear();
                idle = _idle;
                wake = _wake;
            }
            _cts.Cancel();
            wake.TrySetResult();
            idle.TrySetResult(); // nobody should hang waiting on a queue that is gone
        }

        // ---- internals (call under _lock unless noted) ----

        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource Completed() { var t = New(); t.SetResult(); return t; }

        private void Arrived()
        {
            if (_idle.Task.IsCompleted) _idle = New();
            if (_workerRunning) return;
            _workerRunning = true;
            _ = Task.Run(Worker);
        }

        /// <summary>Negative when a should run before b.</summary>
        private static int Order(Entry a, Entry b)
        {
            if (a.Urgent != b.Urgent) return a.Urgent ? -1 : 1;
            if (a.Urgent) return b.Seq.CompareTo(a.Seq);
            if (a.Batch != b.Batch) return b.Batch.CompareTo(a.Batch);
            if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
            return a.Seq.CompareTo(b.Seq);
        }

        private Entry? Best()
        {
            Entry? best = null;
            foreach (var e in _queue) if (best == null || Order(e, best) < 0) best = e;
            return best;
        }

        private void Trim()
        {
            while (_queue.Count > _capacity)
            {
                Entry last = _queue[0];
                foreach (var e in _queue) if (Order(e, last) > 0) last = e;
                _queue.Remove(last);
            }
        }

        private async Task Wait(TimeSpan delay, Task wake, bool paused)
        {
            var fired = New();
            IDisposable? timer = null;
            try { timer = _timers.After(delay, () => fired.TrySetResult()); }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkQueue] timer failed: {ex.GetType().Name}: {ex.Message}");
                // Slow: skip the gap (jobs still run, just unpaced). Paused: fall back to a plain delay so we never spin.
                if (!paused) return;
                timer = null;
                fired = New();
                _ = Task.Delay(delay).ContinueWith(_ => fired.TrySetResult(), TaskScheduler.Default);
            }
            try { await Task.WhenAny(fired.Task, wake).ConfigureAwait(false); }
            finally { timer?.Dispose(); }
        }

        /// <summary>A throwing gate (e.g. player state torn down at shutdown) is treated as Slow: keep going, unhurried.</summary>
        private WarmMode ReadGate()
        {
            try { return _gate.Mode; }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkQueue] gate failed, treating as Slow: {ex.GetType().Name}: {ex.Message}");
                return WarmMode.Slow;
            }
        }

        /// <summary>Last line of defence: if the loop ever dies unexpectedly, free the worker slot and restart it when work is waiting.</summary>
        private async Task Worker()
        {
            try { await WorkerLoop().ConfigureAwait(false); }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkQueue] worker crashed: {ex.GetType().Name}: {ex.Message}");
                TaskCompletionSource? idle = null;
                lock (_lock)
                {
                    _workerRunning = false;
                    _runningKey = null;
                    if (!_disposed && _queue.Count > 0) { _workerRunning = true; _ = Task.Run(Worker); }
                    else idle = _idle;
                }
                idle?.TrySetResult();
            }
        }

        private async Task WorkerLoop()
        {
            var ranBefore = false; // a job has run since the queue last went empty
            var gapPaid = false;
            while (true)
            {
                Entry entry;
                Task wake;
                lock (_lock)
                {
                    wake = _wake.Task; // taken with the pick, so an urgent arrival after it still cuts the wait short
                    var best = _disposed ? null : Best();
                    if (best == null)
                    {
                        _workerRunning = false;
                        _runningKey = null;
                        var idle = _idle;
                        idle.TrySetResult();
                        return;
                    }
                    entry = best;
                }

                if (!entry.Urgent)
                {
                    var mode = ReadGate();
                    if (mode == WarmMode.Paused) { await Wait(PausedRecheck, wake, true).ConfigureAwait(false); continue; }
                    if (mode == WarmMode.Slow && ranBefore && !gapPaid) { await Wait(SlowGap, wake, false).ConfigureAwait(false); gapPaid = true; continue; }
                }

                lock (_lock)
                {
                    if (_disposed || !_queue.Remove(entry)) continue; // changed while we waited: pick again
                    _runningKey = entry.Item.Key;
                }

                try { await entry.Item.Run(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
                catch (Exception ex) { Console.WriteLine($"[WorkQueue] {entry.Item.Key} failed: {ex.GetType().Name}: {ex.Message}"); }

                lock (_lock) _runningKey = null;
                ranBefore = true;
                gapPaid = false;
            }
        }
    }
}
