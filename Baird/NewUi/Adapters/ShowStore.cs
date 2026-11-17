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
            public bool Dirty;       // needs writing
            public bool Touched;     // used since the last save (last-write time to bump)
        }

        private readonly string _folder;
        private readonly ITimers _timers;
        private readonly int _max;
        private readonly Action<Action> _background;
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _index = new();
        private long _clock;
        private IDisposable? _saveTimer;

        /// <param name="runInBackground">Where the save runs when the timer fires (default: the thread pool; tests run it inline).</param>
        public DiskShowStore(string folder, ITimers timers, int maxTitles = 5000, Action<Action>? runInBackground = null)
        {
            _folder = folder; _timers = timers; _max = maxTitles;
            _background = runInBackground ?? (a => _ = Task.Run(a));
            try
            {
                Directory.CreateDirectory(folder);
                var files = new DirectoryInfo(folder).EnumerateFiles("*.json")
                    .Where(f => f.Name.Length == 64 + 5 && f.Name[..64].All(Uri.IsHexDigit))
                    .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal);
                foreach (var f in files)
                    _index[f.Name[..64]] = new Entry { Key = "", Used = ++_clock };
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
            lock (_gate)
            {
                if (!_index.TryGetValue(hash, out var entry)) return false;
                entry.Key = key;
                if (entry.Show == null && !LoadFromDisk(hash, key, entry)) { Forget(hash); return false; }
                entry.Used = ++_clock; entry.Touched = true;
                show = Detokenize(entry.Show);
                return show != null;
            }
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
                entry.Key = key; entry.Used = ++_clock; entry.Touched = true;
                if (entry.Json != json) { entry.Show = clean; entry.Json = json; entry.Dirty = true; }
                while (_index.Count > _max)
                    Forget(_index.MinBy(p => p.Value.Used).Key, deleteFile: true);
                ScheduleSave();
            }
        }

        /// <summary>Writes changed titles and bumps the recency of those only used. Normally runs from the debounce timer.</summary>
        internal void Save()
        {
            lock (_gate)
            {
                _saveTimer = null;
                var stamp = DateTime.UtcNow.AddSeconds(-_index.Count);
                var n = 0;
                foreach (var (hash, e) in _index.Where(p => p.Value.Dirty || p.Value.Touched).OrderBy(p => p.Value.Used).ToList())
                {
                    var when = stamp.AddSeconds(n++);   // ascending, so last-write order matches recency after a restart
                    try
                    {
                        Directory.CreateDirectory(_folder);
                        var path = PathOf(hash);
                        if (e.Dirty && e.Show != null)
                            File.WriteAllText(path, JsonSerializer.Serialize(new ShowFile { Version = CurrentVersion, Key = e.Key, Show = e.Show }, ShowStoreJson.Default.ShowFile));
                        if (File.Exists(path)) File.SetLastWriteTimeUtc(path, when);
                        e.Dirty = false; e.Touched = false;
                    }
                    catch (Exception ex) { Console.WriteLine($"[ShowStore] Can't save to {_folder}: {ex.Message}"); }
                }
            }
        }

        private void ScheduleSave()
        {
            if (_saveTimer != null) return;
            _saveTimer = _timers.After(SaveDelay, () => _background(Save));
        }

        private void Forget(string hash, bool deleteFile = true)
        {
            _index.Remove(hash);
            if (!deleteFile) return;
            try { File.Delete(PathOf(hash)); } catch (Exception ex) { Console.WriteLine($"[ShowStore] Can't delete {hash}: {ex.Message}"); }
        }

        private bool LoadFromDisk(string hash, string key, Entry entry)
        {
            try
            {
                var text = File.ReadAllText(PathOf(hash));
                var dto = JsonSerializer.Deserialize(text, ShowStoreJson.Default.ShowFile);
                if (dto is not { Version: CurrentVersion, Show: { } show } || dto.Key != key) return false;
                // A file written by an older version may still hold a raw token: clean it, and write it back.
                entry.Show = Tokenize(show);
                entry.Json = JsonSerializer.Serialize(entry.Show, ShowStoreJson.Default.StoredShow);
                entry.Dirty = entry.Json != JsonSerializer.Serialize(show, ShowStoreJson.Default.StoredShow);
                return true;
            }
            catch (Exception ex) { Console.WriteLine($"[ShowStore] Ignoring unreadable file for {hash[..8]}: {ex.Message}"); return false; }
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
