using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Baird.NewUi.Diagnostics
{
    /// <summary>
    /// What Baird has been doing, for freeze reports: the last few things that happened (keys, screens, playback) and the
    /// operations under way right now (nested), so a report names what the UI thread was stuck in.
    /// </summary>
    public sealed class Breadcrumbs
    {
        public const int Size = 60;
        private readonly Queue<string> _recent = new();
        private readonly List<(int Id, string What, DateTime Since)> _doing = new();
        private readonly object _lock = new();
        private int _next;

        public static Breadcrumbs Shared { get; } = new();

        public void Add(string what)
        {
            lock (_lock)
            {
                _recent.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {what}");
                while (_recent.Count > Size) _recent.Dequeue();
            }
        }

        /// <summary>Marks an operation as under way until disposed.</summary>
        public IDisposable Doing(string what)
        {
            int id;
            lock (_lock) { id = ++_next; _doing.Add((id, what, DateTime.Now)); }
            return new Done(this, id);
        }

        private void End(int id) { lock (_lock) _doing.RemoveAll(d => d.Id == id); }

        public (IReadOnlyList<string> Recent, IReadOnlyList<string> Doing) Snapshot()
        {
            lock (_lock)
                return (_recent.ToList(), _doing.Select(d => $"{d.What}  (since {d.Since:HH:mm:ss.fff})").ToList());
        }

        private sealed class Done : IDisposable
        {
            private readonly Breadcrumbs _b; private readonly int _id; private bool _done;
            public Done(Breadcrumbs b, int id) { _b = b; _id = id; }
            public void Dispose() { if (_done) return; _done = true; _b.End(_id); }
        }
    }

    /// <summary>
    /// Notices the UI thread not answering (a freeze): every half second it posts a ping to the UI thread; if a ping goes
    /// unanswered for <see cref="Threshold"/>, it writes a report (what was under way, what led up to it, where the UI was,
    /// process state) to ~/.baird/freezes/ and logs it; when the UI answers again it logs how long the freeze lasted.
    /// </summary>
    public sealed class FreezeWatchdog
    {
        public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);
        public const int Keep = 5;
        public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "freezes");

        private readonly Action<Action> _post;
        private readonly Func<DateTime> _now;
        private readonly string _dir;
        private readonly Breadcrumbs _crumbs;
        private readonly Func<string> _context;
        private DateTime _pingSent, _lastAnswer;
        private bool _pending, _frozen;
        private DateTime _frozeAt;
        private Thread? _thread;

        public TimeSpan? LastFreeze { get; private set; }

        public FreezeWatchdog(Action<Action> post, Func<DateTime> now, string dir, Breadcrumbs crumbs, Func<string> context)
        {
            _post = post; _now = now; _dir = dir; _crumbs = crumbs; _context = context;
            _lastAnswer = now();
        }

        /// <summary>Runs <see cref="Check"/> every half second on a background thread.</summary>
        public void Start()
        {
            _thread = new Thread(() => { while (true) { try { Check(); } catch (Exception ex) { Console.WriteLine($"[Freeze] Watchdog error: {ex.Message}"); } Thread.Sleep(500); } })
            { IsBackground = true, Name = "Baird freeze watchdog", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void Check()
        {
            var now = _now();
            bool ping;
            lock (this)
            {
                if (!_pending)
                {
                    // The last ping was answered: if we were frozen, that's the end of it.
                    if (_frozen)
                    {
                        _frozen = false;
                        LastFreeze = _lastAnswer - _frozeAt;
                        Console.WriteLine($"[Freeze] The UI answered again after {LastFreeze.Value.TotalSeconds:0.0} s.");
                        _crumbs.Add($"freeze ended after {LastFreeze.Value.TotalSeconds:0.0} s");
                    }
                    _pending = true;
                    _pingSent = now;
                    ping = true;
                }
                else
                {
                    if (_frozen || now - _pingSent < Threshold) return;
                    _frozen = true;
                    _frozeAt = _pingSent;
                    ping = false;
                }
            }
            if (ping) _post(() => { lock (this) { _pending = false; _lastAnswer = _now(); } });
            else Report(now);
        }

        private void Report(DateTime now)
        {
            try
            {
                var (recent, doing) = _crumbs.Snapshot();
                var p = Process.GetCurrentProcess();
                ThreadPool.GetAvailableThreads(out var workers, out var io);
                var sb = new StringBuilder();
                sb.AppendLine($"Baird freeze report — the UI thread hasn't answered since {_frozeAt:yyyy-MM-dd HH:mm:ss.fff} ({(now - _frozeAt).TotalSeconds:0.0} s)");
                sb.AppendLine();
                sb.AppendLine("Under way (outermost first):");
                foreach (var d in doing) sb.AppendLine("  " + d);
                if (doing.Count == 0) sb.AppendLine("  (nothing marked: stuck in rendering, layout or a library)");
                sb.AppendLine();
                sb.AppendLine("Where: " + SafeContext());
                sb.AppendLine();
                sb.AppendLine($"Process: working set {p.WorkingSet64 / 1048576} MB, GC heap {GC.GetTotalMemory(false) / 1048576} MB, " +
                              $"threads {p.Threads.Count}, CPU {p.TotalProcessorTime.TotalSeconds:0} s, thread pool free {workers} workers / {io} IO, " +
                              $"GC counts {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                sb.AppendLine();
                sb.AppendLine("Memory (the GC heap above can't see native memory):");
                sb.AppendLine(Proc.NativeMemory());
                sb.AppendLine();
                sb.AppendLine($"Last {recent.Count} events:");
                foreach (var r in recent) sb.AppendLine("  " + r);

                Directory.CreateDirectory(_dir);
                var path = Path.Combine(_dir, $"freeze-{_frozeAt:yyyyMMdd-HHmmss-fff}.txt");
                File.WriteAllText(path, sb.ToString());
                foreach (var old in Directory.GetFiles(_dir, "freeze-*.txt").OrderByDescending(f => f).Skip(Keep)) File.Delete(old);
                Console.WriteLine($"[Freeze] The UI has been stuck for {(now - _frozeAt).TotalSeconds:0} s; report: {path}. Under way: {string.Join(" > ", doing)}");
            }
            catch (Exception ex) { Console.WriteLine($"[Freeze] Couldn't write a report: {ex.Message}"); }
        }

        private string SafeContext()
        {
            try { return _context(); } catch (Exception ex) { return $"(unknown: {ex.Message})"; }
        }
    }
}
