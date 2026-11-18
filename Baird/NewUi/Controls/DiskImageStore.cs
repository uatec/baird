using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Baird.NewUi.Controls
{
    /// <summary>Where downloaded artwork bytes are kept between runs.</summary>
    public interface IDiskImageStore
    {
        bool TryRead(string url, out byte[] bytes);
        /// <summary>Is the picture stored? (No read, no use recorded: warming asks before it downloads.)</summary>
        bool Contains(string url);
        void Write(string url, byte[] bytes);
        void Remove(string url);
    }

    /// <summary>
    /// Raw artwork bytes on disk so a restart does not download every poster and still again. One file per link, named by the
    /// SHA-256 of the link (so tokens in a link never reach the disk), written beside then swapped in (a cut-short write never
    /// leaves half a picture), and trimmed oldest-use-first once the folder passes its budget. Nothing here throws: a folder
    /// that cannot be used just means every picture comes from the network, as before.
    /// </summary>
    public sealed class DiskImageStore : IDiskImageStore
    {
        public const long DefaultBudgetBytes = 1024L * 1024 * 1024;
        public const int MinImageBytes = 64;                 // a smaller "image" is an error body, not a picture
        public const int MaxImageBytes = 20 * 1024 * 1024;   // a larger one is a video or something else by mistake

        public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "image_cache");

        private readonly string _folder;
        private readonly long _budget;
        private readonly object _gate = new();
        private Dictionary<string, (long Size, DateTime Used, DateTime Touched)>? _index;   // built from file metadata on first need, never from contents
        private long _total;
        private bool _unusable;

        public static readonly TimeSpan TouchEvery = TimeSpan.FromHours(1);

        private readonly Func<DateTime> _now;
        private readonly Action<string, DateTime> _touch;

        public DiskImageStore(string? folder = null, long budgetBytes = DefaultBudgetBytes, Func<DateTime>? now = null, Action<string, DateTime>? touch = null)
        {
            _now = now ?? (() => DateTime.UtcNow);
            _touch = touch ?? File.SetLastWriteTimeUtc;
            _folder = folder ?? DefaultFolder;
            _budget = budgetBytes;
        }

        /// <summary>Hash of the link with its access token scrubbed, so a new login (new token) still finds the pictures already stored.</summary>
        public static string HashOf(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Baird.Services.JsonMediaDataCache.Scrub(url)))).ToLowerInvariant();

        private string PathOf(string hash) => Path.Combine(_folder, hash);

        private static bool IsHash(string name) => name.Length == 64 && name.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        public bool TryRead(string url, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (_unusable || string.IsNullOrEmpty(url)) return false;
            var hash = HashOf(url);
            var path = PathOf(hash);
            try
            {
                if (!File.Exists(path)) return false;
                var data = File.ReadAllBytes(path);
                if (data.Length < MinImageBytes || data.Length > MaxImageBytes) { Remove(url); return false; }   // foreign or damaged
                var now = _now();
                EnsureIndex();
                var touch = false;
                lock (_gate)
                {
                    if (_index != null && _index.TryGetValue(hash, out var e))
                    {
                        touch = now - e.Touched >= TouchEvery;   // a metadata write per hit is a lot for an SD card
                        _index[hash] = (e.Size, now, touch ? now : e.Touched);   // use is always remembered here
                    }
                }
                if (touch)
                    try { _touch(path, now); } catch (Exception) { /* use time is best effort */ }
                bytes = data;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImageDisk] Can't read {hash[..8]}: {ex.Message}");
                return false;
            }
        }

        public bool Contains(string url)
        {
            if (_unusable || string.IsNullOrEmpty(url)) return false;
            try { return File.Exists(PathOf(HashOf(url))); }
            catch (Exception) { return false; }
        }

        public void Write(string url, byte[] bytes)
        {
            if (_unusable || string.IsNullOrEmpty(url) || bytes == null || bytes.Length < MinImageBytes || bytes.Length > MaxImageBytes) return;
            var hash = HashOf(url);
            var path = PathOf(hash);
            try
            {
                Directory.CreateDirectory(_folder);
                var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
                var now = _now();
                EnsureIndex();
                lock (_gate)
                {
                    if (_index!.TryGetValue(hash, out var old)) _total -= old.Size;
                    _index[hash] = (bytes.Length, now, now);
                    _total += bytes.Length;
                }
                Trim();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImageDisk] Can't store {hash[..8]}: {ex.Message} - pictures come from the network");
            }
        }

        public void Remove(string url)
        {
            var hash = HashOf(url);
            try { File.Delete(PathOf(hash)); } catch (Exception ex) { Console.WriteLine($"[ImageDisk] Can't delete {hash[..8]}: {ex.Message}"); }
            lock (_gate)
            {
                if (_index != null && _index.Remove(hash, out var e)) _total -= e.Size;
            }
        }

        /// <summary>Drop the least recently used pictures until the folder is within budget. Reads file metadata only.</summary>
        public void Trim()
        {
            try
            {
                EnsureIndex();
                List<string> doomed = new();
                lock (_gate)
                {
                    if (_index == null || _total <= _budget) return;
                    foreach (var kv in _index.OrderBy(k => k.Value.Used))
                    {
                        if (_total <= _budget) break;
                        _total -= kv.Value.Size;
                        doomed.Add(kv.Key);
                    }
                    foreach (var h in doomed) _index.Remove(h);
                }
                foreach (var h in doomed)
                    try { File.Delete(PathOf(h)); } catch (Exception ex) { Console.WriteLine($"[ImageDisk] Can't delete {h[..8]}: {ex.Message}"); }
            }
            catch (Exception ex) { Console.WriteLine($"[ImageDisk] Can't trim {_folder}: {ex.Message}"); }
        }

        private void EnsureIndex()
        {
            lock (_gate) { if (_index != null) return; }
            var index = new Dictionary<string, (long, DateTime, DateTime)>();
            long total = 0;
            try
            {
                var dir = new DirectoryInfo(_folder);
                if (!dir.Exists) dir.Create();
                foreach (var stray in dir.EnumerateFiles("*.tmp"))   // left by a write that was cut short
                    try { stray.Delete(); } catch (Exception ex) { Console.WriteLine($"[ImageDisk] Can't remove {stray.Name}: {ex.Message}"); }
                foreach (var f in dir.EnumerateFiles().Where(f => IsHash(f.Name)))
                {
                    index[f.Name] = (f.Length, f.LastWriteTimeUtc, f.LastWriteTimeUtc);
                    total += f.Length;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImageDisk] Can't use {_folder}: {ex.Message} - carrying on without it");
                _unusable = true;
            }
            lock (_gate) { if (_index == null) { _index = index; _total = total; } }
        }
    }
}
