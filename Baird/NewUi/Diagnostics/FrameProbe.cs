using System;
using System.Globalization;
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
    /// Opt-in (BAIRD_FPS_PROBE=true): asks for a frame after every frame and appends the <see cref="FrameStats"/> line every 10 s to
    /// ~/.baird/resources.log. It keeps the UI rendering at full rate even when idle, so the figures are the most the UI can do
    /// on this machine, and it costs some CPU: leave it off normally.
    /// </summary>
    public sealed class FrameProbe
    {
        private readonly TopLevel _top;
        private readonly FrameStats _stats = new(TimeSpan.FromSeconds(10));
        private readonly Func<DateTime> _now;
        private readonly Action<string> _write;

        public FrameProbe(TopLevel top, Func<DateTime> now, Action<string> write) { _top = top; _now = now; _write = write; }

        public static FrameProbe ForResourcesLog(TopLevel top) => new(top, () => DateTime.Now, text =>
        {
            try
            {
                var path = ResourceRecorder.DefaultPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, text + "\n");
            }
            catch (Exception ex) { Console.WriteLine($"[FrameProbe] Couldn't record: {ex.Message}"); }
        });

        public void Start() => _top.RequestAnimationFrame(OnFrame);

        private void OnFrame(TimeSpan at)
        {
            if (_stats.Frame(at) is { } line) _write($"{_now():yyyy-MM-dd HH:mm:ss} {line}");
            _top.RequestAnimationFrame(OnFrame);
        }
    }
}
