using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.Services;

namespace Baird.NewUi.Adapters
{
    /// <summary>One episode as kept between runs: names and pictures, never anything to play or whether it was watched.</summary>
    public sealed record StoredEpisode(int Number, string Label, string? Sublabel, string? ImageUrl, string? Synopsis);

    public sealed record StoredSeason(int Number, string Label, IReadOnlyList<StoredEpisode> Episodes, int? EpisodeCount);

    /// <summary>A show's descriptive data. The title's sources (stream URLs) and availability are not kept: they are asked for afresh.</summary>
    public sealed record StoredShow(CatalogueTitle Title, IReadOnlyList<StoredSeason> Seasons, DateTimeOffset Stamp);

    /// <summary>Metadata that outlives a run, so a show page can open from what was seen last time.</summary>
    public interface IShowStore
    {
        bool TryGet(string key, out StoredShow? show);
        void Put(string key, StoredShow show);
    }

    /// <summary>
    /// One small JSON file per title (file name: SHA-256 of the key, lower-case hex). Saves are debounced to 30 seconds and write
    /// only titles that changed. Past <c>maxTitles</c> the least recently used title (by <c>TryGet</c>/<c>Put</c>) is deleted.
    /// Recency is an in-memory index seeded at startup from the files' last-write times; touched-but-unchanged files get their
    /// last-write time bumped on save so recency survives a restart. Image URLs are tokenized with <see cref="SecretRedactor"/>
    /// before they are written, so no secret reaches disk. Anything unreadable (corrupt, empty, unknown version) is ignored;
    /// an unwritable folder is logged and the store carries on in memory.
    /// </summary>
    public sealed class DiskShowStore : IShowStore
    {
        public const int CurrentVersion = 1;
        public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(30);

        private sealed class Entry
        {
            public string Key = "";
            public StoredShow? Show; // tokenized; null until read from disk
            public string? Json;     // Show serialized, to tell whether a Put changed anything
            public long Used;        // recency: higher is more recent
            public long Rev;         // bumped by every change or use, so a save knows whether it saw the latest
            public bool Dirty;       // needs writing
            public bool Touched;     // its file's last-write time is to be bumped by the next save
            public DateTime Stamped; // when its file's last-write time was last set (as far as is known)
        }

        /// <summary>
        /// A title used again has its file's last-write time bumped at most this often: recency is kept in memory on every use, but a
        /// metadata write per read (Home warming reads every card each visit) is a lot for an SD card. Recency after a restart is then
        /// right to within a day.
        /// </summary>
        public static readonly TimeSpan TouchEvery = TimeSpan.FromDays(1);

        private readonly string _folder;
        private readonly ITimers _timers;
        private readonly int _max;
        private readonly Action<Action> _background;
        private readonly Func<DateTime> _now;
        private readonly Action<string, DateTime> _touch;
        // Guards the in-memory state only. File I/O never happens while it is held.
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _index = new();
        private readonly HashSet<string> _toDelete = new();   // files of forgotten titles, deleted by the next save
        private long _clock;
        private IDisposable? _saveTimer;

        /// <summary>Test seam: runs in <see cref="Save"/> after its snapshot, before it touches any file.</summary>
        internal Action? BeforeSaveIo;

        /// <param name="runInBackground">Where the save runs when the timer fires (default: the thread pool; tests run it inline).</param>
        /// <param name="now">The clock deciding when a used title's file is touched again (tests).</param>
        /// <param name="touch">Sets a file's last-write time (tests count it).</param>
        public DiskShowStore(string folder, ITimers timers, int maxTitles = 5000, Action<Action>? runInBackground = null, Func<DateTime>? now = null, Action<string, DateTime>? touch = null)
        {
            _folder = folder; _timers = timers; _max = maxTitles;
            _background = runInBackground ?? (a => _ = Task.Run(a));
            _now = now ?? (() => DateTime.UtcNow);
            _touch = touch ?? File.SetLastWriteTimeUtc;
            try
            {
                Directory.CreateDirectory(folder);
                var dir = new DirectoryInfo(folder);
                foreach (var stray in dir.EnumerateFiles("*.tmp"))   // left by a save that was cut short
                    try { stray.Delete(); } catch (Exception ex) { Console.WriteLine($"[ShowStore] Can't remove {stray.Name}: {ex.Message}"); }
                var files = dir.EnumerateFiles("*.json")
                    .Where(f => f.Name.Length == 64 + 5 && f.Name[..64].All(Uri.IsHexDigit))
                    .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal);
                foreach (var f in files)
                    _index[f.Name[..64]] = new Entry { Key = "", Used = ++_clock, Stamped = f.LastWriteTimeUtc };
            }
            catch (Exception ex) { Console.WriteLine($"[ShowStore] Can't read {folder}: {ex.Message} - carrying on without it"); }
        }

        public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "show_cache");

        internal static string HashOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

        private string PathOf(string hash) => Path.Combine(_folder, hash + ".json");

        public bool TryGet(string key, out StoredShow? show)
        {
            show = null;
            var hash = HashOf(key);
            Entry? entry;
            lock (_gate)
            {
                if (!_index.TryGetValue(hash, out entry)) return false;
                entry.Key = key;
                if (entry.Show != null) return Use(entry, out show);
            }
            // Not read yet: read the file without holding the lock.
            var loaded = ReadFile(hash, key);
            lock (_gate)
            {
                if (!_index.TryGetValue(hash, out var now) || now != entry) return false;   // forgotten meanwhile
                if (now.Show == null)
                {
                    if (loaded == null) { Forget(hash); ScheduleSave(); return false; }
                    now.Show = loaded.Value.Clean; now.Json = loaded.Value.Json;
                    if (loaded.Value.Stale) { now.Dirty = true; now.Rev++; }
                }
                return Use(now, out show);
            }
        }

        private bool Use(Entry entry, out StoredShow? show)
        {
            entry.Used = ++_clock;   // a read counts for recency too: always in memory, on disk at most once a day
            Touch(entry);
            show = Detokenize(entry.Show);
            return show != null;
        }

        /// <summary>Caller holds the lock. Marks the title's file to have its last-write time bumped, when that was last done a day or more ago.</summary>
        private void Touch(Entry entry)
        {
            if (entry.Touched || _now() - entry.Stamped < TouchEvery) return;
            entry.Touched = true; entry.Rev++;
            ScheduleSave();
        }

        public void Put(string key, StoredShow show)
        {
            var hash = HashOf(key);
            StoredShow clean; string json;
            try { clean = Tokenize(show); json = JsonSerializer.Serialize(clean, ShowStoreJson.Default.StoredShow); }
            catch (Exception ex) { Console.WriteLine($"[ShowStore] Can't store {key}: {ex.Message}"); return; }
            lock (_gate)
            {
                if (!_index.TryGetValue(hash, out var entry)) _index[hash] = entry = new Entry { Key = key };
                entry.Key = key; entry.Used = ++_clock;
                if (entry.Json != json) { entry.Show = clean; entry.Json = json; entry.Dirty = true; entry.Rev++; }
                else Touch(entry);   // the same as kept: no rewrite, and its file touched at most once a day
                while (_index.Count > _max)
                    Forget(_index.MinBy(p => p.Value.Used).Key);
                ScheduleSave();
            }
        }

        /// <summary>Writes changed titles, bumps the recency of those only used and deletes forgotten ones. Normally runs from the debounce timer.</summary>
        internal void Save()
        {
            List<(string Hash, Entry E, long Rev, string? Json, DateTime When)> work;
            List<string> deletes;
            lock (_gate)
            {
                _saveTimer = null;
                // Used titles are stamped from now on, in recency order, so they are newer than every file not touched this time.
                var stamp = _now(); var n = 0;
                work = _index.Where(p => p.Value.Dirty || p.Value.Touched).OrderBy(p => p.Value.Used)
                    .Select(p => (p.Key, p.Value, p.Value.Rev,
                        p.Value.Dirty && p.Value.Show != null
                            ? JsonSerializer.Serialize(new ShowFile { Version = CurrentVersion, Key = p.Value.Key, Show = p.Value.Show }, ShowStoreJson.Default.ShowFile) : null,
                        stamp.AddMilliseconds(n++))).ToList();
                deletes = _toDelete.Where(h => !_index.ContainsKey(h)).ToList();   // a title put back since is not deleted
                _toDelete.Clear();
            }
            BeforeSaveIo?.Invoke();

            var done = new List<(Entry E, long Rev, DateTime When)>();
            foreach (var h in deletes)
                try { File.Delete(PathOf(h)); } catch (Exception ex) { Console.WriteLine($"[ShowStore] Can't delete {h}: {ex.Message}"); }
            foreach (var (hash, e, rev, json, when) in work)
            {
                try
                {
                    Directory.CreateDirectory(_folder);
                    var path = PathOf(hash);
                    if (json != null)
                    {
                        var tmp = path + ".tmp";               // write beside it, then swap in: a cut-short save never leaves half a title
                        File.WriteAllText(tmp, json);
                        File.Move(tmp, path, overwrite: true);
                    }
                    if (File.Exists(path)) _touch(path, when);
                    done.Add((e, rev, when));
                }
                catch (Exception ex) { Console.WriteLine($"[ShowStore] Can't save to {_folder}: {ex.Message}"); }
            }
            lock (_gate)
                foreach (var (e, rev, when) in done)
                {
                    e.Stamped = when;
                    if (e.Rev == rev) { e.Dirty = false; e.Touched = false; }   // changed again meanwhile: the next save does it
                }
        }

        private void ScheduleSave()
        {
            if (_saveTimer != null) return;
            _saveTimer = _timers.After(SaveDelay, () => _background(Save));
        }

        // Caller holds the lock. The file goes in the next save, not here.
        private void Forget(string hash)
        {
            _index.Remove(hash);
            _toDelete.Add(hash);
        }

        private (StoredShow Clean, string Json, bool Stale)? ReadFile(string hash, string key)
        {
            try
            {
                var text = File.ReadAllText(PathOf(hash));
                var dto = JsonSerializer.Deserialize(text, ShowStoreJson.Default.ShowFile);
                if (dto is not { Version: CurrentVersion, Show: { } show } || dto.Key != key) return null;
                // A file written by an older version may still hold a raw token: clean it, and write it back.
                var clean = Tokenize(show);
                var json = JsonSerializer.Serialize(clean, ShowStoreJson.Default.StoredShow);
                return (clean, json, json != JsonSerializer.Serialize(show, ShowStoreJson.Default.StoredShow));
            }
            catch (Exception ex) { Console.WriteLine($"[ShowStore] Ignoring unreadable file for {hash[..8]}: {ex.Message}"); return null; }
        }

        private static StoredShow Tokenize(StoredShow s) => s with
        {
            Title = s.Title with
            {
                Sources = Array.Empty<TitleSource>(),                          // stream URLs and resume state are never stored
                FocusItemId = null,                                            // an item of some provider
                Seerr = s.Title.Seerr is { } r ? r with { Status = 0 } : null,  // availability is asked for afresh
                ImageUrl = Url(s.Title.ImageUrl, JsonMediaDataCache.Scrub),
            },
            Seasons = s.Seasons.Select(se => se with
            {
                Episodes = se.Episodes.Select(e => e with { ImageUrl = Url(e.ImageUrl, JsonMediaDataCache.Scrub) }).ToList(),
            }).ToList(),
        };

        private static StoredShow? Detokenize(StoredShow? s) => s == null ? null : s with
        {
            Title = s.Title with { ImageUrl = Url(s.Title.ImageUrl, SecretRedactor.Detokenize) },
            Seasons = s.Seasons.Select(se => se with
            {
                Episodes = se.Episodes.Select(e => e with { ImageUrl = Url(e.ImageUrl, SecretRedactor.Detokenize) }).ToList(),
            }).ToList(),
        };

        private static string? Url(string? url, Func<string, string> f) => url == null ? null : f(url);
    }

    /// <summary>What a title's file holds. The key is kept beside the show so a hash collision can't serve the wrong title.</summary>
    internal sealed class ShowFile
    {
        public int Version { get; set; }
        public string? Key { get; set; }
        public StoredShow? Show { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(StoredShow))]
    [JsonSerializable(typeof(ShowFile))]
    internal partial class ShowStoreJson : JsonSerializerContext { }
}
