using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Baird.NewUi.Diagnostics;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ResourceRecorderTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "baird-res-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

        private static ResourceRecorder Recorder(string path, Dictionary<string, string> files, DateTime? at = null) =>
            new(path, () => at ?? new DateTime(2026, 10, 28, 5, 0, 0), p => files.GetValueOrDefault(p), () => (1302 * 1024, 6), () => (77L << 20, 28));

        [Fact]
        public void A_line_says_how_much_memory_Baird_and_Chromium_hold_and_what_is_left()
        {
            var files = new Dictionary<string, string>
            {
                ["/proc/meminfo"] = "MemTotal:  8252000 kB\nMemAvailable:  5340000 kB\nSwapTotal:  2097136 kB\nSwapFree:  2097136 kB\n",
                ["/proc/self/status"] = "VmRSS:\t 1372288 kB\nRssAnon:\t 1100000 kB\nVmSwap:\t 0 kB\n",
                ["/proc/vmstat"] = "pswpin 0\npswpout 0\npgmajfault 100\n",
                ["/proc/loadavg"] = "0.13 0.11 0.15 1/300 2000\n",
            };
            var line = Recorder(Path.Combine(_dir, "r.log"), files).Sample();
            Assert.Contains("2026-10-28 05:00:00", line);
            Assert.Contains("baird_rss=1340M", line);
            Assert.Contains("gc_heap=77M", line);
            Assert.Contains("chromium_rss=1302M chromium_procs=6", line);
            Assert.Contains("mem_avail=5214M", line);
            Assert.Contains("swap_used=0M", line);
            Assert.Contains("load=0.13", line);
        }

        [Fact]
        public void Swapping_shows_as_the_change_since_the_last_line()
        {
            var files = new Dictionary<string, string> { ["/proc/vmstat"] = "pswpin 10\npswpout 20\npgmajfault 100\n" };
            var rec = Recorder(Path.Combine(_dir, "r.log"), files);
            Assert.Contains("swapin=0 swapout=0 majfault=0", rec.Sample());    // nothing to compare with yet
            files["/proc/vmstat"] = "pswpin 510\npswpout 2020\npgmajfault 160\n";
            Assert.Contains("swapin=500 swapout=2000 majfault=60", rec.Sample());
        }

        [Fact]
        public void A_system_without_proc_still_gets_a_line()
        {
            Assert.Contains("baird_rss=0M", Recorder(Path.Combine(_dir, "r.log"), new()).Sample());
        }

        [Fact]
        public void Lines_are_appended_and_a_big_log_rolls_over_to_a_second_file()
        {
            var path = Path.Combine(_dir, "r.log");
            var rec = Recorder(path, new());
            rec.Record(); rec.Record();
            Assert.Equal(2, File.ReadAllLines(path).Length);
            File.WriteAllText(path, new string('x', (int)ResourceRecorder.MaxBytes + 1));
            rec.Record();
            Assert.Single(File.ReadAllLines(path));
            Assert.True(File.Exists(path + ".1"));
        }

        [Fact]
        public void Every_tenth_line_is_followed_by_the_biggest_mappings()
        {
            var smaps = "aaa000-bbb000 rw-p 00000000 00:00 0 \nRss:     3000 kB\neee000-fff000 r-xp 00000000 08:01 12 /usr/lib/libmpv.so\nRss:     2048 kB\n";
            var path = Path.Combine(_dir, "r.log");
            var rec = Recorder(path, new Dictionary<string, string> { ["/proc/self/smaps"] = smaps });
            for (var i = 0; i < ResourceRecorder.MappingsEvery + 1; i++) rec.Record();
            var lines = File.ReadAllLines(path);
            Assert.Equal(ResourceRecorder.MappingsEvery + 1 + 2, lines.Length);   // two "maps" lines: the first sample and the eleventh
            Assert.Contains(lines, l => l.Contains(" maps [anon]=2M /usr/lib/libmpv.so=2M") || l.Contains(" maps /usr/lib/libmpv.so=2M [anon]=2M"));
        }

        [Fact]
        public void The_biggest_mappings_are_summed_by_name_with_anonymous_ones_grouped()
        {
            const string smaps =
                "aaa000-bbb000 rw-p 00000000 00:00 0 \nRss:     1000 kB\nPss:  1000 kB\n" +
                "ccc000-ddd000 rw-p 00000000 00:00 0\nRss:     3000 kB\n" +
                "eee000-fff000 r-xp 00000000 08:01 12 /usr/lib/libskia.so\nRss:     500 kB\n" +
                "111000-222000 r--p 00000000 08:01 12 /usr/lib/libskia.so\nRss:     700 kB\n";
            var top = Proc.TopMappings(smaps, 2);
            Assert.Equal(("[anon]", 4000L), top[0]);
            Assert.Equal(("/usr/lib/libskia.so", 1200L), top[1]);
        }

        [Fact]
        public void A_freeze_report_includes_the_memory_section()
        {
            var t = DateTime.UnixEpoch; var posted = new List<Action>();
            var dog = new FreezeWatchdog(posted.Add, () => t, _dir, new Breadcrumbs(), () => "");
            dog.Check(); t = t.AddSeconds(8); dog.Check();
            Assert.Contains("Memory (the GC heap above can't see native memory):", File.ReadAllText(Directory.GetFiles(_dir).Single()));
        }
    }
}
