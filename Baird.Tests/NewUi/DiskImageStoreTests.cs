using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Baird.NewUi.Controls;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>Artwork bytes survive a restart on disk, within a budget, and a bad folder or file never costs a picture.</summary>
    public sealed class DiskImageStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "baird-img-" + Guid.NewGuid().ToString("N"));
        private const string Url = "https://img.example/poster.jpg";

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { /* temp */ } }

        private static byte[] Bytes(int n, byte fill = 7) => Enumerable.Repeat(fill, n).ToArray();

        [Fact]
        public void An_image_warmed_before_a_restart_loads_from_disk()
        {
            new DiskImageStore(_dir).Write(Url, Bytes(500));
            var network = 0;
            var after = new DiskImageStore(_dir);   // the restart
            var found = after.TryRead(Url, out var bytes);
            if (!found) { network++; }
            Assert.True(found);
            Assert.Equal(Bytes(500), bytes);
            Assert.Equal(0, network);
        }

        [Fact]
        public void Eviction_drops_the_least_recently_used_when_over_budget()
        {
            var store = new DiskImageStore(_dir, budgetBytes: 1000);
            store.Write("a", Bytes(400));
            store.Write("b", Bytes(400));
            Assert.True(store.TryRead("a", out _));   // reading "a" counts as use, so "b" is now the oldest
            store.Write("c", Bytes(400));
            Assert.True(store.TryRead("a", out _));
            Assert.False(store.TryRead("b", out _));
            Assert.True(store.TryRead("c", out _));
        }

        [Fact]
        public void Eviction_also_runs_at_startup_over_files_already_there()
        {
            var first = new DiskImageStore(_dir);
            first.Write("old", Bytes(400));
            File.SetLastWriteTimeUtc(Path.Combine(_dir, DiskImageStore.HashOf("old")), DateTime.UtcNow.AddDays(-2));
            first.Write("new", Bytes(400));
            new DiskImageStore(_dir, budgetBytes: 500).Trim();
            var check = new DiskImageStore(_dir);
            Assert.False(check.TryRead("old", out _));
            Assert.True(check.TryRead("new", out _));
        }

        [Fact]
        public void A_corrupt_or_foreign_file_never_throws_and_falls_back_to_the_network()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not mine");
            File.WriteAllBytes(Path.Combine(_dir, DiskImageStore.HashOf(Url)), Array.Empty<byte>());           // empty = damaged
            Directory.CreateDirectory(Path.Combine(_dir, DiskImageStore.HashOf("dir")));                       // unreadable as a file
            var store = new DiskImageStore(_dir, budgetBytes: 10);
            Assert.False(store.TryRead(Url, out _));
            Assert.False(store.TryRead("dir", out _));
            store.Trim();
            Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));   // not ours, left alone
        }

        [Fact]
        public void Writes_are_atomic_and_stray_temp_files_are_removed()
        {
            Directory.CreateDirectory(_dir);
            var stray = Path.Combine(_dir, "half.tmp");
            File.WriteAllBytes(stray, Bytes(100));
            var store = new DiskImageStore(_dir);
            store.Write(Url, Bytes(300));
            Assert.False(File.Exists(stray));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
            store.Write(Url, Bytes(300, 9));   // overwriting swaps in whole
            Assert.True(store.TryRead(Url, out var b));
            Assert.Equal(Bytes(300, 9), b);
        }

        [Fact]
        public void Urls_with_tokens_never_appear_in_file_names_or_contents_other_than_as_a_hash()
        {
            var url = "https://jelly/Items/1/Images/Primary?api_key=SECRET123";
            new DiskImageStore(_dir).Write(url, Bytes(200));
            var file = Assert.Single(Directory.GetFiles(_dir));
            Assert.Equal(DiskImageStore.HashOf(url), Path.GetFileName(file));
            Assert.Matches("^[0-9a-f]{64}$", Path.GetFileName(file));
            Assert.DoesNotContain("SECRET123", Path.GetFileName(file));
            Assert.DoesNotContain("SECRET123", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file)));
        }

        [Fact]
        public void An_unwritable_folder_falls_back_to_the_network()
        {
            var blocker = Path.Combine(Path.GetTempPath(), "baird-img-file-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blocker, "a file where the folder should be");
            try
            {
                var store = new DiskImageStore(Path.Combine(blocker, "cache"));
                store.Write(Url, Bytes(200));
                Assert.False(store.TryRead(Url, out _));
                store.Trim();
            }
            finally { File.Delete(blocker); }
        }

        [Fact]
        public void Oversized_and_empty_downloads_are_not_stored()
        {
            var store = new DiskImageStore(_dir);
            store.Write("empty", Array.Empty<byte>());
            store.Write("tiny", Bytes(DiskImageStore.MinImageBytes - 1));
            store.Write("huge", new byte[DiskImageStore.MaxImageBytes + 1]);
            Assert.False(store.TryRead("empty", out _));
            Assert.False(store.TryRead("tiny", out _));
            Assert.False(store.TryRead("huge", out _));
            Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : Array.Empty<string>());
        }

        private static byte[] Png()
        {
            using var bmp = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var ms = new MemoryStream();
            bmp.Save(ms);
            return ms.ToArray();
        }

        [AvaloniaFact]
        public async Task A_second_loader_over_the_same_folder_loads_the_image_from_disk_without_downloading()
        {
            var png = Png();
            var downloads = 0;
            var first = new BoundedImageLoader(disk: new DiskImageStore(_dir), download: _ => { downloads++; return Task.FromResult<byte[]?>(png); });
            Assert.NotNull(await first.ProvideImageAsync(Url));
            Assert.Equal(1, downloads);
            await first.WritesDoneAsync();
            Assert.True(File.Exists(Path.Combine(_dir, DiskImageStore.HashOf(Url))));

            var second = new BoundedImageLoader(disk: new DiskImageStore(_dir), download: _ => { downloads++; return Task.FromResult<byte[]?>(null); });
            Assert.NotNull(await second.ProvideImageAsync(Url));
            Assert.Equal(1, downloads);
        }

        [AvaloniaFact]
        public async Task A_cached_file_that_will_not_decode_is_dropped_and_fetched_again()
        {
            var png = Png();
            new DiskImageStore(_dir).Write(Url, Bytes(300));   // garbage under a real name
            var downloads = 0;
            var loader = new BoundedImageLoader(disk: new DiskImageStore(_dir), download: _ => { downloads++; return Task.FromResult<byte[]?>(png); });
            Assert.NotNull(await loader.ProvideImageAsync(Url));
            Assert.Equal(1, downloads);
        }

        [Fact]
        public void A_hit_touches_the_file_at_most_once_an_hour_but_always_counts_as_recent_use()
        {
            var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
            var touches = 0;
            var store = new DiskImageStore(_dir, budgetBytes: 1000, now: () => now, touch: (_, _) => touches++);
            store.Write("a", Bytes(400));
            now = now.AddMinutes(10);
            store.Write("b", Bytes(400));
            now = now.AddMinutes(10);
            Assert.True(store.TryRead("a", out _));
            now = now.AddMinutes(10);
            Assert.True(store.TryRead("a", out _));
            Assert.Equal(0, touches);                       // written 30 minutes ago: not worth an SD-card metadata write
            now = now.AddMinutes(40);
            Assert.True(store.TryRead("a", out _));
            Assert.Equal(1, touches);                       // over an hour since written
            Assert.True(store.TryRead("a", out _));
            Assert.Equal(1, touches);                       // and not again straight away
            now = now.AddMinutes(61);
            Assert.True(store.TryRead("a", out _));
            Assert.Equal(2, touches);
        }

        [Fact]
        public void A_recent_hit_that_was_not_touched_is_still_not_evicted_before_an_older_untouched_file()
        {
            var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
            var store = new DiskImageStore(_dir, budgetBytes: 1000, now: () => now, touch: (_, _) => { });
            store.Write("a", Bytes(400));
            now = now.AddMinutes(5);
            store.Write("b", Bytes(400));
            now = now.AddMinutes(5);
            Assert.True(store.TryRead("a", out _));         // no file touch (under an hour), but "a" is now newer than "b"
            now = now.AddMinutes(5);
            store.Write("c", Bytes(400));
            Assert.True(store.TryRead("a", out _));
            Assert.False(store.TryRead("b", out _));
        }

        [Fact]
        public void Links_differing_only_in_the_access_token_share_one_file_but_different_pictures_do_not()
        {
            var one = "https://jelly/Items/1/Images/Primary?api_key=TOKEN-ONE";
            var two = "https://jelly/Items/1/Images/Primary?api_key=TOKEN-TWO";
            var other = "https://jelly/Items/2/Images/Primary?api_key=TOKEN-ONE";
            Assert.Equal(DiskImageStore.HashOf(one), DiskImageStore.HashOf(two));
            Assert.NotEqual(DiskImageStore.HashOf(one), DiskImageStore.HashOf(other));
            new DiskImageStore(_dir).Write(one, Bytes(200));
            Assert.True(new DiskImageStore(_dir).TryRead(two, out _));   // the next login's token still hits
        }

        [Fact]
        public void A_link_with_no_token_is_hashed_as_it_is()
        {
            const string plain = "https://img.example/poster.jpg";
            Assert.Equal(plain, Baird.Services.JsonMediaDataCache.Scrub(plain));
            Assert.Equal(DiskImageStore.HashOf(plain), DiskImageStore.HashOf(Baird.Services.JsonMediaDataCache.Scrub(plain)));
        }

        [AvaloniaFact]
        public async Task Finished_background_writes_do_not_pile_up_in_the_loader()
        {
            var png = Png();
            var loader = new BoundedImageLoader(disk: new DiskImageStore(_dir), download: _ => Task.FromResult<byte[]?>(png));
            for (var i = 0; i < 5; i++) Assert.NotNull(await loader.ProvideImageAsync($"https://img.example/{i}.png"));
            await loader.WritesDoneAsync();
            Assert.Equal(0, loader.PendingWrites);
        }
    }
}
