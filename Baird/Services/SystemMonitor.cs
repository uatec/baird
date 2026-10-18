using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Baird.Services
{
    public sealed record ThreadCpu(string Name, double Percent);

    public sealed record SystemStats(
        double? ProcessCpuPercent,
        IReadOnlyList<ThreadCpu> TopThreads,
        double? CpuTempCelsius,
        double? ArmClockMhz,
        string? Throttled);

    /// <summary>
    /// Cheap Linux/Raspberry Pi health readings for the A/V debug page. Everything is read from
    /// /proc and /sys (no process spawning except a rate-limited vcgencmd fallback) and fails soft
    /// to null on other platforms. CPU figures are % of one core since the previous Sample().
    /// </summary>
    public class SystemMonitor
    {
        private const int TopThreadCount = 5;
        private const double UserHz = 100; // USER_HZ: /proc tick unit, 100 on all mainstream Linux
        private static readonly TimeSpan VcgencmdInterval = TimeSpan.FromSeconds(10);

        private readonly Dictionary<int, long> _lastThreadTicks = new();
        private long _lastProcessTicks = -1;
        private DateTime _lastSample = DateTime.MinValue;
        private string? _throttled;
        private DateTime _lastVcgencmd = DateTime.MinValue;
        private bool _vcgencmdUnavailable;

        public SystemStats Sample()
        {
            var now = DateTime.UtcNow;
            double elapsed = _lastSample == DateTime.MinValue ? 0 : (now - _lastSample).TotalSeconds;
            _lastSample = now;

            return new SystemStats(
                ProcessCpuPercent: SampleProcessCpu(elapsed),
                TopThreads: SampleThreads(elapsed),
                CpuTempCelsius: ReadNumber("/sys/class/thermal/thermal_zone0/temp") / 1000,
                ArmClockMhz: ReadNumber("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq") / 1000,
                Throttled: ReadThrottled());
        }

        private double? SampleProcessCpu(double elapsed)
        {
            var stat = ReadStat("/proc/self/stat");
            if (stat == null) return null;
            long ticks = stat.Value.Ticks;
            double? percent = _lastProcessTicks >= 0 && elapsed > 0
                ? (ticks - _lastProcessTicks) / UserHz / elapsed * 100
                : null;
            _lastProcessTicks = ticks;
            return percent;
        }

        private IReadOnlyList<ThreadCpu> SampleThreads(double elapsed)
        {
            var result = new List<ThreadCpu>();
            if (!Directory.Exists("/proc/self/task")) return result;

            var seen = new HashSet<int>();
            foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out int tid)) continue;
                var stat = ReadStat(Path.Combine(dir, "stat"));
                if (stat == null) continue;
                seen.Add(tid);

                if (elapsed > 0 && _lastThreadTicks.TryGetValue(tid, out long last))
                {
                    double percent = (stat.Value.Ticks - last) / UserHz / elapsed * 100;
                    // The main thread is Avalonia's UI thread, which is also mpv's render thread.
                    var name = tid == Environment.ProcessId ? "UI/render" : stat.Value.Name;
                    if (percent > 0) result.Add(new ThreadCpu(name, percent));
                }
                _lastThreadTicks[tid] = stat.Value.Ticks;
            }

            foreach (var gone in _lastThreadTicks.Keys.Where(t => !seen.Contains(t)).ToList())
                _lastThreadTicks.Remove(gone);

            return result.OrderByDescending(t => t.Percent).Take(TopThreadCount).ToList();
        }

        /// <summary>Parses comm and utime+stime from a /proc stat file.</summary>
        private static (string Name, long Ticks)? ReadStat(string path)
        {
            try
            {
                var text = File.ReadAllText(path);
                // comm is in parentheses and may contain spaces; fields after it are space-separated.
                int open = text.IndexOf('(');
                int close = text.LastIndexOf(')');
                if (open < 0 || close < open) return null;
                var name = text[(open + 1)..close];
                var fields = text[(close + 2)..].Split(' ');
                // After comm: [0]=state ... utime is field 14 overall => index 11, stime => 12.
                long utime = long.Parse(fields[11], CultureInfo.InvariantCulture);
                long stime = long.Parse(fields[12], CultureInfo.InvariantCulture);
                return (name, utime + stime);
            }
            catch
            {
                return null;
            }
        }

        private static double? ReadNumber(string path)
        {
            try
            {
                return double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Raspberry Pi firmware throttling flags (0x0 = healthy; bit 0 under-voltage now,
        /// bit 1 ARM frequency capped, bit 2 throttled, bit 3 soft temp limit; bits 16-19 = has occurred).
        /// </summary>
        private string? ReadThrottled()
        {
            if (_vcgencmdUnavailable || DateTime.UtcNow - _lastVcgencmd < VcgencmdInterval) return _throttled;
            _lastVcgencmd = DateTime.UtcNow;
            try
            {
                var psi = new ProcessStartInfo("vcgencmd", "get_throttled")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return _throttled = null;
                var output = proc.StandardOutput.ReadToEnd().Trim(); // "throttled=0x0"
                if (!proc.WaitForExit(1000)) proc.Kill();
                _throttled = output.StartsWith("throttled=", StringComparison.Ordinal) ? output["throttled=".Length..] : null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _vcgencmdUnavailable = true; // not a Pi: vcgencmd isn't installed, stop trying
                _throttled = null;
            }
            catch
            {
                _throttled = null;
            }
            return _throttled;
        }
    }
}
