using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Baird.NewUi.Diagnostics
{
    /// <summary>
    /// Reads Linux /proc text. Everything returns nothing off Linux or when a file can't be read, so diagnostics never throw.
    /// </summary>
    public static class Proc
    {
        public static string? Read(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
        }

        /// <summary>"Key:   123 kB" lines (meminfo, status, smaps_rollup) as key → number (kB where the line has a unit).</summary>
        public static Dictionary<string, long> KeyValues(string? text)
        {
            var map = new Dictionary<string, long>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var parts = line[(colon + 1)..].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) map[line[..colon]] = n;
            }
            return map;
        }

        /// <summary>"pswpin 12" lines (vmstat) as key → number.</summary>
        public static Dictionary<string, long> Counters(string? text)
        {
            var map = new Dictionary<string, long>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) map[parts[0]] = n;
            }
            return map;
        }

        private static readonly Regex MappingHeader = new(@"^[0-9a-f]+-[0-9a-f]+ \S+ \S+ \S+ \S+\s*(.*)$", RegexOptions.Compiled);

        /// <summary>The biggest mappings by resident size (anonymous ones grouped as "[anon]"), from /proc/self/smaps text.</summary>
        public static List<(string Name, long RssKb)> TopMappings(string? smaps, int count)
        {
            var totals = new Dictionary<string, long>();
            string? current = null;
            foreach (var line in (smaps ?? "").Split('\n'))
            {
                var header = MappingHeader.Match(line);
                if (header.Success) { current = header.Groups[1].Value.Trim(); if (current.Length == 0) current = "[anon]"; continue; }
                if (current != null && line.StartsWith("Rss:", StringComparison.Ordinal))
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1 && long.TryParse(parts[1], out var kb)) totals[current] = totals.GetValueOrDefault(current) + kb;
                }
            }
            return totals.OrderByDescending(t => t.Value).Take(count).Select(t => (t.Key, t.Value)).ToList();
        }

        /// <summary>For freeze reports: where this process's resident memory is, which the managed GC heap can't show.</summary>
        public static string NativeMemory()
        {
            try
            {
                var roll = KeyValues(Read("/proc/self/smaps_rollup"));
                if (roll.Count == 0) return "(not available on this system)";
                var sb = new StringBuilder();
                sb.AppendLine($"  Rss {roll.GetValueOrDefault("Rss") / 1024} MB: anon {roll.GetValueOrDefault("Pss_Anon") / 1024} MB, file {roll.GetValueOrDefault("Pss_File") / 1024} MB, shmem {roll.GetValueOrDefault("Pss_Shmem") / 1024} MB, swapped {roll.GetValueOrDefault("Swap") / 1024} MB");
                sb.AppendLine("  Biggest mappings:");
                foreach (var (name, kb) in TopMappings(Read("/proc/self/smaps"), 8)) sb.AppendLine($"    {kb / 1024,6} MB  {name}");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex) { return $"(unknown: {ex.Message})"; }
        }
    }

    /// <summary>
    /// Which programs use the CPU: the share of one core each used since the previous sample, grouped by program name (Chromium's
    /// many processes add up), biggest first. Reads /proc/&lt;pid&gt;/stat through <paramref name="snapshot"/>.
    /// </summary>
    public sealed class CpuSampler
    {
        private readonly Func<IEnumerable<(int Pid, string Name, long Ticks)>> _snapshot;
        private readonly Func<DateTime> _now;
        private readonly int _ticksPerSecond;
        private Dictionary<int, long> _last = new();
        private DateTime _lastAt;

        public CpuSampler(Func<IEnumerable<(int Pid, string Name, long Ticks)>> snapshot, Func<DateTime> now, int ticksPerSecond = 100)
        {
            _snapshot = snapshot; _now = now; _ticksPerSecond = ticksPerSecond;
        }

        public static CpuSampler ForThisMachine() => new(ReadProc, () => DateTime.UtcNow);

        /// <summary>Threads of this process (by thread name) instead of programs: is the UI thread the one that is busy?</summary>
        public static CpuSampler ForThisProcessThreads() => new(ReadThreads, () => DateTime.UtcNow);

        private static IEnumerable<(int, string, long)> ReadThreads()
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var tid)) continue;
                var stat = Proc.Read(Path.Combine(dir, "stat"));
                if (stat == null) continue;
                var open = stat.IndexOf('('); var close = stat.LastIndexOf(')');
                if (open < 0 || close < open) continue;
                var rest = stat[(close + 2)..].Split(' ');
                if (rest.Length > 12 && long.TryParse(rest[11], out var user) && long.TryParse(rest[12], out var sys))
                    yield return (tid, stat[(open + 1)..close], user + sys);
            }
        }

        private static IEnumerable<(int, string, long)> ReadProc()
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                var stat = Proc.Read(Path.Combine(dir, "stat"));
                if (stat == null) continue;
                var open = stat.IndexOf('('); var close = stat.LastIndexOf(')');
                if (open < 0 || close < open) continue;
                var rest = stat[(close + 2)..].Split(' ');   // state is rest[0]; utime and stime are fields 14 and 15 overall
                if (rest.Length > 12 && long.TryParse(rest[11], out var user) && long.TryParse(rest[12], out var sys))
                    yield return (pid, stat[(open + 1)..close], user + sys);
            }
        }

        /// <summary>"name=NN% ..." for the <paramref name="count"/> busiest programs since the last call ("" the first time).</summary>
        public string Top(int count)
        {
            var now = _now(); var current = new Dictionary<int, long>(); var byName = new Dictionary<string, double>();
            var elapsed = (now - _lastAt).TotalSeconds;
            foreach (var (pid, name, ticks) in _snapshot())
            {
                current[pid] = ticks;
                if (_last.TryGetValue(pid, out var before) && elapsed > 0 && ticks > before)
                    byName[name] = byName.GetValueOrDefault(name) + (ticks - before) * 100.0 / (_ticksPerSecond * elapsed);
            }
            _last = current; _lastAt = now;
            return string.Join(" ", byName.OrderByDescending(b => b.Value).Take(count).Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Key}={b.Value:0}%")));
        }
    }

    /// <summary>
    /// Once a minute, appends one line to ~/.baird/resources.log: how much memory Baird and Chromium hold, how much is
    /// left, and whether the system is swapping. It survives reboots (unlike a volatile journal), so after a slowdown the
    /// log shows what grew first. Rotates to resources.log.1 at <see cref="MaxBytes"/>.
    /// </summary>
    public sealed class ResourceRecorder
    {
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
        public const long MaxBytes = 1_000_000;
        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "resources.log");

        private readonly string _path;
        private readonly Func<DateTime> _now;
        private readonly Func<string, string?> _read;
        private readonly Func<(long RssKb, int Count)> _chromium;
        private readonly Func<(long HeapBytes, int Threads)> _managed;
        private Dictionary<string, long>? _lastVm;

        public ResourceRecorder(string path, Func<DateTime> now, Func<string, string?> read, Func<(long RssKb, int Count)> chromium, Func<(long HeapBytes, int Threads)> managed)
        {
            _path = path; _now = now; _read = read; _chromium = chromium; _managed = managed;
        }

        public static ResourceRecorder ForThisProcess() => new(DefaultPath, () => DateTime.Now, Proc.Read, ChromiumUsage,
            () => (GC.GetTotalMemory(false), Process.GetCurrentProcess().Threads.Count));

        private static (long RssKb, int Count) ChromiumUsage()
        {
            long total = 0; var procs = Process.GetProcessesByName("chromium");
            foreach (var p in procs) { try { total += p.WorkingSet64 / 1024; } catch { } p.Dispose(); }
            return (total, procs.Length);
        }

        /// <summary>The line for right now. The swap and fault figures are changes since the previous sample.</summary>
        public string Sample()
        {
            var mem = Proc.KeyValues(_read("/proc/meminfo"));
            var me = Proc.KeyValues(_read("/proc/self/status"));
            var vm = Proc.Counters(_read("/proc/vmstat"));
            var (chromiumKb, chromiumCount) = _chromium();
            var (heap, threads) = _managed();
            long Delta(string k) => _lastVm != null && vm.TryGetValue(k, out var now) && _lastVm.TryGetValue(k, out var was) ? now - was : 0;
            var load = (_read("/proc/loadavg") ?? "").Split(' ').FirstOrDefault() ?? "?";
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{_now():yyyy-MM-dd HH:mm:ss} baird_rss={me.GetValueOrDefault("VmRSS") / 1024}M baird_anon={me.GetValueOrDefault("RssAnon") / 1024}M baird_swap={me.GetValueOrDefault("VmSwap") / 1024}M " +
                $"gc_heap={heap / 1048576}M threads={threads} chromium_rss={chromiumKb / 1024}M chromium_procs={chromiumCount} " +
                $"mem_avail={mem.GetValueOrDefault("MemAvailable") / 1024}M swap_used={(mem.GetValueOrDefault("SwapTotal") - mem.GetValueOrDefault("SwapFree")) / 1024}M " +
                $"swapin={Delta("pswpin")} swapout={Delta("pswpout")} majfault={Delta("pgmajfault")} load={load}");
            _lastVm = vm;
            return line;
        }

        /// <summary>Every this many samples, a second line lists the biggest memory mappings, to show what is growing.</summary>
        public const int MappingsEvery = 10;
        private int _samples;
        private readonly object _fileLock = new();

        /// <summary>For the first <see cref="StartupProfileFor"/> after start, a "cpu" line every <see cref="StartupProfileEvery"/>: the busiest programs.</summary>
        public static readonly TimeSpan StartupProfileEvery = TimeSpan.FromSeconds(10), StartupProfileFor = TimeSpan.FromMinutes(4);

        public void RecordCpu(CpuSampler sampler)
        {
            var top = sampler.Top(4);
            if (top.Length == 0) return;
            Append($"{_now():yyyy-MM-dd HH:mm:ss} cpu {top}");
        }

        private void Append(string text)
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes) File.Move(_path, _path + ".1", true);
                File.AppendAllText(_path, text + "\n");
            }
        }

        /// <summary>"maps" line: the biggest resident mappings of this process (anonymous ones grouped), from /proc/self/smaps.</summary>
        public string Mappings()
        {
            var top = Proc.TopMappings(_read("/proc/self/smaps"), 6);
            if (top.Count == 0) return "";
            return $"{_now():yyyy-MM-dd HH:mm:ss} maps " + string.Join(" ", top.Select(m => $"{m.Name}={m.RssKb / 1024}M"));
        }

        public void Record()
        {
            var line = Sample();
            if (++_samples % MappingsEvery == 1 && Mappings() is { Length: > 0 } maps) line += "\n" + maps;
            Append(line);
        }

        public void Start()
        {
            // Start-up is when Baird is jerky: show which programs take the CPU then (the busiest since each 10 s ago).
            new Thread(() =>
            {
                var sampler = CpuSampler.ForThisMachine();
                sampler.Top(1);   // the first call only sets the baseline
                for (var waited = TimeSpan.Zero; waited < StartupProfileFor; waited += StartupProfileEvery)
                {
                    Thread.Sleep(StartupProfileEvery);
                    try { RecordCpu(sampler); } catch (Exception ex) { Console.WriteLine($"[Resources] Couldn't record CPU: {ex.Message}"); }
                }
            })
            { IsBackground = true, Name = "Baird start-up CPU profile" }.Start();
            new Thread(() =>
            {
                while (true)
                {
                    try { Record(); } catch (Exception ex) { Console.WriteLine($"[Resources] Couldn't record: {ex.Message}"); }
                    Thread.Sleep(Interval);
                }
            })
            { IsBackground = true, Name = "Baird resource recorder" }.Start();
        }
    }
}
