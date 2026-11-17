using System;
using System.Globalization;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Controls;

namespace Baird.NewUi.Diagnostics
{
    /// <summary>
    /// Turns frame timestamps into one line per window: achieved fps, the worst gap between frames, and how many gaps were
    /// longer than <see cref="SlowMs"/> (a visible hitch at 60 Hz starts around 25 ms).
    /// </summary>
    public sealed class FrameStats
    {
        public const double SlowMs = 25;
        private readonly TimeSpan _window;
        private TimeSpan? _windowStart, _last;
        private int _frames, _slow;
        private double _worstMs;

        public FrameStats(TimeSpan window) => _window = window;

        /// <summary>Feeds one frame time (monotonic); returns the summary when a window has just ended, otherwise null.</summary>
        public string? Frame(TimeSpan at)
        {
            string? line = null;
            if (_windowStart is { } start && at - start >= _window)
            {
                var seconds = (at - start).TotalSeconds;
                line = string.Create(CultureInfo.InvariantCulture, $"frames fps={_frames / seconds:0.0} worst_gap={_worstMs:0}ms slow(>{SlowMs:0}ms)={_slow} of {_frames}");
                _windowStart = at; _frames = 0; _slow = 0; _worstMs = 0;
            }
            _windowStart ??= at;
            if (_last is { } last)
            {
                var gap = (at - last).TotalMilliseconds;
                if (gap > _worstMs) _worstMs = gap;
                if (gap > SlowMs) _slow++;
            }
            _last = at; _frames++;
            return line;
        }
    }

    /// <summary>
    /// Opt-in (BAIRD_FPS_PROBE=true): asks for a frame after every frame and appends the <see cref="FrameStats"/> line every
    /// <see cref="Window"/> to ~/.baird/resources.log, after the key presses and screen changes of that window (so a drop can be matched
    /// to what was being done) and with the busiest threads of this process (is the UI thread the one that is saturated?). It keeps the UI rendering at full rate even when idle, so the figures are the most the UI can do
    /// on this machine, and it costs some CPU: leave it off normally.
    /// </summary>
    public sealed class FrameProbe
    {
        private readonly TopLevel _top;
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
        private readonly FrameStats _stats = new(Window);
        private readonly ConcurrentQueue<string> _events = new();
        private readonly Func<string>? _busiest;
        private readonly Func<DateTime> _now;
        private readonly Action<string> _write;

        public FrameProbe(TopLevel top, Func<DateTime> now, Action<string> write, Func<string>? busiest = null) { _top = top; _now = now; _write = write; _busiest = busiest; }

        /// <summary>Remembers something that happened (any thread); it is written just before the next frames line.</summary>
        public void Note(string what) => _events.Enqueue(what);

        public static FrameProbe ForResourcesLog(TopLevel top, Breadcrumbs crumbs)
        {
            var threads = CpuSampler.ForThisProcessThreads();
            threads.Top(1);   // baseline
            var probe = new FrameProbe(top, () => DateTime.Now, Append, () => threads.Top(3));
            crumbs.Added += probe.Note;
            return probe;
        }

        private static void Append(string text)
        {
            try
            {
                var path = ResourceRecorder.DefaultPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, text + "\n");
            }
            catch (Exception ex) { Console.WriteLine($"[FrameProbe] Couldn't record: {ex.Message}"); }
        }

        public void Start() => _top.RequestAnimationFrame(OnFrame);

        private void OnFrame(TimeSpan at)
        {
            if (_stats.Frame(at) is { } line)
            {
                var day = _now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var batch = new System.Text.StringBuilder();
                while (_events.TryDequeue(out var e)) batch.Append(day).Append(" event ").Append(e).Append('\n');
                var busy = _busiest?.Invoke() ?? "";
                batch.Append(_now().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(' ').Append(line).Append(busy.Length > 0 ? " busiest_threads: " + busy : "");
                _write(batch.ToString());
            }
            _top.RequestAnimationFrame(OnFrame);
        }
    }
}
