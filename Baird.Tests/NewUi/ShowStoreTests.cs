using System;
using System.IO;
using System.Linq;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public sealed class ShowStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "showstore-" + Guid.NewGuid().ToString("N"));
        private readonly PlaybackControllerTests.FakeTimers _t = new();

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private DiskShowStore Open(int max = 5000) => new(_dir, _t, max, a => a());

        private static StoredShow Show(string name, string? image = null, string? episodeImage = null, int tmdb = 7) => new(
            new CatalogueTitle("Series:" + name, TitleKind.Series, name, 2020, image, "About " + name,
                new[] { new TitleSource("Jellyfin", "jf1", PlayVia.Mpv, "http://jf/stream?api_key=SECRETSTREAM", TimeSpan.FromMinutes(5), 0.5) },
                new SeerrRef(tmdb, "tv", 5), FocusItemId: "ep9"),
            new[] { new StoredSeason(1, "Season 1", new[] { new StoredEpisode(1, "Pilot", "45m", episodeImage, "Starts") }, 8) },
            DateTimeOffset.UnixEpoch.AddDays(3));

        private string[] Files() => Directory.Exists(_dir) ? Directory.GetFiles(_dir) : Array.Empty<string>();

        [Fact]
        public void A_stored_title_survives_a_restart()
        {
            var a = Open();
            a.Put("Series:Loki", Show("Loki", "http://img/loki.jpg", "http://img/e1.jpg"));
            _t.Advance(DiskShowStore.SaveDelay);

            var b = Open();
            Assert.True(b.TryGet("Series:Loki", out var got));
            Assert.Equal("Loki", got!.Title.Name);
            Assert.Equal("http://img/loki.jpg", got.Title.ImageUrl);
            Assert.Equal("http://img/e1.jpg", got.Seasons[0].Episodes[0].ImageUrl);
            Assert.Equal(8, got.Seasons[0].EpisodeCount);
            Assert.Equal("Pilot", got.Seasons[0].Episodes[0].Label);
            Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(3), got.Stamp);
            Assert.Equal(7, got.Title.Seerr!.TmdbId);          // the Seerr reference is kept ...
            Assert.Equal("tv", got.Title.Seerr.MediaType);
            Assert.Empty(got.Title.Sources);                    // ... sources, availability and resume are not
            Assert.Equal(Availability.Get, got.Title.Availability);
            Assert.False(b.TryGet("Series:Other", out _));
            Assert.All(Files(), f => Assert.DoesNotContain("SECRETSTREAM", File.ReadAllText(f)));
            Assert.All(Files(), f => Assert.Matches("^[0-9a-f]{64}\\.json$", Path.GetFileName(f)));
        }

        [Fact]
        public void Image_urls_never_reach_disk_with_a_token()
        {
            const string token = "tok-showstore-test-9f3a";
            SecretRedactor.Register(JellyfinService.AccessTokenSecretName, token);
            var s = Open();
            s.Put("Series:A", Show("A", $"http://jf/Items/1/Images/Primary?api_key={token}", $"http://jf/e.jpg?x=1&api_key={token}"));
            s.Put("Series:B", Show("B", "http://jf/b.jpg?api_key=stale-token-from-before", null));
            _t.Advance(DiskShowStore.SaveDelay);

            Assert.Equal(2, Files().Length);
            foreach (var f in Files())
            {
                var text = File.ReadAllText(f);
                Assert.DoesNotContain(token, text);
                Assert.DoesNotContain("stale-token-from-before", text);
                Assert.DoesNotContain(Uri.EscapeDataString(token), text);
            }

            var b = Open();
            Assert.True(b.TryGet("Series:A", out var got));
            Assert.Equal($"http://jf/Items/1/Images/Primary?api_key={token}", got!.Title.ImageUrl);
            Assert.Equal($"http://jf/e.jpg?x=1&api_key={token}", got.Seasons[0].Episodes[0].ImageUrl);
        }

        [Fact]
        public void A_corrupt_or_unknown_version_file_is_ignored()
        {
            var a = Open();
            foreach (var k in new[] { "empty", "truncated", "future", "zero", "garbage", "good" })
                a.Put(k, Show(k));
            _t.Advance(DiskShowStore.SaveDelay);

            string P(string k) => Path.Combine(_dir, DiskShowStore.HashOf(k) + ".json");
            File.WriteAllText(P("empty"), "");
            File.WriteAllText(P("truncated"), File.ReadAllText(P("truncated"))[..40]);
            File.WriteAllText(P("future"), File.ReadAllText(P("future")).Replace("\"Version\":1", "\"Version\":2"));
            File.WriteAllText(P("zero"), File.ReadAllText(P("zero")).Replace("\"Version\":1", "\"Version\":0"));
            File.WriteAllText(P("garbage"), "{\"Version\":1,\"Show\":\"not a show\"}");
            File.WriteAllText(Path.Combine(_dir, "notes.txt"), "unrelated");
            File.WriteAllText(Path.Combine(_dir, "other.json"), "{}");
            Directory.CreateDirectory(Path.Combine(_dir, "sub"));

            var b = Open();
            foreach (var k in new[] { "empty", "truncated", "future", "zero", "garbage" })
                Assert.False(b.TryGet(k, out _), k);
            Assert.True(b.TryGet("good", out var good));
            Assert.Equal("good", good!.Title.Name);
            Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));   // unrelated files are left alone
            Assert.True(File.Exists(Path.Combine(_dir, "other.json")));

            b.Put("empty", Show("empty-again"));                          // a bad file is replaced by a good one
            _t.Advance(DiskShowStore.SaveDelay);
            Assert.True(Open().TryGet("empty", out var again));
            Assert.Equal("empty-again", again!.Title.Name);
        }

        [Fact]
        public void An_unwritable_folder_never_crashes_a_page()
        {
            var blocker = Path.Combine(Path.GetTempPath(), "showstore-file-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blocker, "a file where the folder should be");
            try
            {
                var s = new DiskShowStore(Path.Combine(blocker, "show_cache"), _t, 5000, a => a());
                s.Put("k", Show("K"));
                _t.Advance(DiskShowStore.SaveDelay);
                Assert.True(s.TryGet("k", out var got));   // still served from memory
                Assert.Equal("K", got!.Title.Name);
            }
            finally { File.Delete(blocker); }
        }

        [Fact]
        public void Saves_are_debounced_to_30_seconds_and_write_only_changed_titles()
        {
            var s = Open();
            s.Put("a", Show("A"));
            _t.Advance(TimeSpan.FromSeconds(29));
            Assert.Empty(Files());                                   // not yet
            s.Put("b", Show("B"));                                   // joins the same save, doesn't restart the wait
            _t.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(2, Files().Length);

            var fa = Path.Combine(_dir, DiskShowStore.HashOf("a") + ".json");
            var fb = Path.Combine(_dir, DiskShowStore.HashOf("b") + ".json");
            var old = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(fa, old); File.SetLastWriteTimeUtc(fb, old);
            File.WriteAllText(fa, File.ReadAllText(fa) + " ");       // a mark to see whether it is rewritten
            var markA = File.ReadAllText(fa);

            s.Put("a", Show("A"));                                   // unchanged: no rewrite
            s.Put("b", Show("B2"));                                  // changed
            _t.Advance(DiskShowStore.SaveDelay);
            Assert.Equal(markA, File.ReadAllText(fa));
            Assert.Contains("B2", File.ReadAllText(fb));
        }

        [Fact]
        public void The_5001st_title_drops_the_least_recently_used()
        {
            var s = Open();
            for (var i = 1; i <= 5000; i++) s.Put($"k{i}", Show($"T{i}"));
            Assert.True(s.TryGet("k1", out _));                       // k1 is used again: k2 is now the oldest
            s.Put("k5001", Show("T5001"));
            _t.Advance(DiskShowStore.SaveDelay);

            Assert.False(s.TryGet("k2", out _));
            Assert.True(s.TryGet("k1", out _));
            Assert.True(s.TryGet("k5001", out _));
            Assert.Equal(5000, Files().Length);

            var b = Open();                                           // the order survives a restart
            Assert.False(b.TryGet("k2", out _));
            b.Put("k5002", Show("T5002"));
            Assert.False(b.TryGet("k3", out _));
            Assert.True(b.TryGet("k1", out _));
        }

        [Fact]
        public void A_failed_write_leaves_the_previous_file_intact_and_stray_temp_files_are_not_titles()
        {
            var s = Open();
            s.Put("a", Show("A"));
            _t.Advance(DiskShowStore.SaveDelay);
            var fa = Path.Combine(_dir, DiskShowStore.HashOf("a") + ".json");
            var before = File.ReadAllText(fa);
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

            Directory.CreateDirectory(fa + ".tmp");                  // the temp file can't be written: the write fails part way
            s.Put("a", Show("A changed"));
            _t.Advance(DiskShowStore.SaveDelay);
            Assert.Equal(before, File.ReadAllText(fa));               // the old title is still whole
            Directory.Delete(fa + ".tmp");

            File.WriteAllText(Path.Combine(_dir, DiskShowStore.HashOf("zz") + ".json.tmp"), "{\"Version\":1,");
            var b = Open(1);                                          // a stray temp file is cleaned up and does not count as a title
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
            Assert.True(b.TryGet("a", out _));
            b.Put("a", Show("A again"));
            Assert.True(b.TryGet("a", out _));
        }

        [Fact]
        public void After_a_restart_the_few_titles_used_recently_outlive_older_untouched_ones()
        {
            var a = Open(20);
            for (var i = 1; i <= 20; i++) a.Put($"k{i}", Show($"T{i}"));
            _t.Advance(DiskShowStore.SaveDelay);
            var now = DateTime.UtcNow;
            for (var i = 1; i <= 20; i++)                             // all last saved two days ago (so a use touches them again), k1 oldest
                File.SetLastWriteTimeUtc(Path.Combine(_dir, DiskShowStore.HashOf($"k{i}") + ".json"), now.AddDays(-2).AddMilliseconds(i * 10));

            var b = Open(20);
            Assert.True(b.TryGet("k1", out _));                       // only two are used, so only two are touched
            Assert.True(b.TryGet("k2", out _));
            _t.Advance(DiskShowStore.SaveDelay);

            var c = Open(20);
            c.Put("n1", Show("N1"));
            c.Put("n2", Show("N2"));
            Assert.True(c.TryGet("k1", out _));
            Assert.True(c.TryGet("k2", out _));
            Assert.False(c.TryGet("k3", out _));
            Assert.False(c.TryGet("k4", out _));
        }

        [Fact]
        public void Reads_within_a_day_touch_no_file_and_a_read_a_day_later_does()
        {
            var clock = DateTime.UtcNow;
            var a = Open();
            a.Put("a", Show("A")); a.Put("b", Show("B"));
            _t.Advance(DiskShowStore.SaveDelay);                     // written

            var touches = new System.Collections.Generic.List<string>();
            var s = new DiskShowStore(_dir, _t, 5000, x => x(), () => clock, (path, _) => touches.Add(Path.GetFileName(path)));
            for (var i = 0; i < 50; i++) { Assert.True(s.TryGet("a", out _)); Assert.True(s.TryGet("b", out _)); }
            s.Put("a", Show("A"));                                    // the same as kept
            _t.Advance(DiskShowStore.SaveDelay);
            clock += TimeSpan.FromHours(23);
            Assert.True(s.TryGet("a", out _));
            _t.Advance(DiskShowStore.SaveDelay);
            Assert.Empty(touches);                                    // a hundred reads within a day: no metadata write at all

            clock += TimeSpan.FromHours(2);                           // past a day since its file was written
            Assert.True(s.TryGet("a", out _)); Assert.True(s.TryGet("a", out _));
            _t.Advance(DiskShowStore.SaveDelay);
            Assert.Equal(new[] { DiskShowStore.HashOf("a") + ".json" }, touches); // touched once; b, unread since, not at all

            Assert.True(s.TryGet("a", out _));                       // just touched: not again
            _t.Advance(DiskShowStore.SaveDelay);
            Assert.Single(touches);
        }

        [Fact]
        public void Eviction_follows_reads_kept_in_memory_even_when_no_file_is_touched()
        {
            var clock = DateTime.UtcNow;
            var touches = 0;
            var s = new DiskShowStore(_dir, _t, 3, x => x(), () => clock, (_, _) => touches++);
            s.Put("k1", Show("T1")); s.Put("k2", Show("T2")); s.Put("k3", Show("T3"));
            _t.Advance(DiskShowStore.SaveDelay);
            var afterWrite = touches;
            Assert.True(s.TryGet("k1", out _));                       // k1 used again: k2 is now the least recently used
            s.Put("k4", Show("T4"));
            _t.Advance(DiskShowStore.SaveDelay);

            Assert.False(s.TryGet("k2", out _));
            Assert.True(s.TryGet("k1", out _));
            Assert.True(s.TryGet("k3", out _));
            Assert.True(s.TryGet("k4", out _));
            Assert.Equal(afterWrite + 1, touches);                    // only k4's own write: the reads touched nothing
        }

        [Fact]
        public void Reads_and_puts_are_not_blocked_by_a_save_in_progress()
        {
            using var entered = new System.Threading.ManualResetEventSlim();
            using var release = new System.Threading.ManualResetEventSlim();
            var s = new DiskShowStore(_dir, _t, 5000, a => _ = System.Threading.Tasks.Task.Run(a));
            s.BeforeSaveIo = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(20)); };
            s.Put("a", Show("A"));
            _t.Advance(DiskShowStore.SaveDelay);                       // the save starts on another thread and stops at the hook
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            try
            {
                var work = System.Threading.Tasks.Task.Run(() =>
                {
                    Assert.True(s.TryGet("a", out _));
                    s.Put("b", Show("B"));
                    Assert.True(s.TryGet("b", out _));
                });
                Assert.True(work.Wait(TimeSpan.FromSeconds(5)), "TryGet/Put waited for the save's file I/O");
            }
            finally { release.Set(); }
        }
    }
}
