using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    /// <summary>
    /// Persistent disk cache for MediaItemData (names, image URLs, metadata).
    /// Allows Watchlist and History to display items immediately from cache,
    /// while fresh data is loaded from providers in the background.
    /// Secrets (TvHeadend password, Jellyfin token) embedded in URLs are never written to disk:
    /// entries are stored with SecretRedactor placeholders and expanded again on read.
    /// </summary>
    public interface IMediaDataCache
    {
        /// <summary>
        /// Tries to get cached media data by ID. Returns immediately without I/O.
        /// </summary>
        bool TryGet(string id, out MediaItemData? data);

        /// <summary>
        /// Stores media data in the cache and schedules a background save to disk.
        /// </summary>
        void Put(MediaItemData data);
    }

    public class JsonMediaDataCache : IMediaDataCache
    {
        private readonly string _filePath;
        private readonly ConcurrentDictionary<string, MediaItemData> _cache;
        private readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);

        // Debounce save: only write to disk once per 500ms window
        private int _saveScheduled = 0;

        // Catches api_key values that aren't registered secrets (e.g. a stale Jellyfin token
        // left in a cache file written by an older version).
        private static readonly Regex ApiKeyParam = new(@"([?&]api_key=)(?!\{\{secret:)[^&#]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public JsonMediaDataCache()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            _filePath = Path.Combine(folder, "media_data_cache.json");
            _cache = new ConcurrentDictionary<string, MediaItemData>(Load());

            // Rewrite files from older versions that still contain raw credentials
            if (_cache.Values.Any(NeedsScrub))
                ScheduleSave();
        }

        private static MediaItemData Tokenize(MediaItemData data)
        {
            return data with
            {
                ImageUrl = Scrub(data.ImageUrl),
                StreamUrl = data.StreamUrl == null ? null : Scrub(data.StreamUrl),
            };
        }

        private static string Scrub(string url)
        {
            var tokenized = SecretRedactor.Tokenize(url);
            return ApiKeyParam.Replace(tokenized, $"$1{{{{secret:{JellyfinService.AccessTokenSecretName}}}}}");
        }

        private static bool NeedsScrub(MediaItemData data) =>
            Tokenize(data) != data;

        private Dictionary<string, MediaItemData> Load()
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, MediaItemData>();
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize(json, BairdJsonContext.Default.DictionaryStringMediaItemData)
                    ?? new Dictionary<string, MediaItemData>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MediaDataCache] Error loading cache: {ex.Message} - Starting empty");
                return new Dictionary<string, MediaItemData>();
            }
        }

        public bool TryGet(string id, out MediaItemData? data)
        {
            if (!_cache.TryGetValue(id, out data)) return false;

            // Expand placeholders with the current secret values (picks up refreshed tokens)
            if (SecretRedactor.ContainsPlaceholder(data.ImageUrl) || SecretRedactor.ContainsPlaceholder(data.StreamUrl))
            {
                data = data with
                {
                    ImageUrl = SecretRedactor.Detokenize(data.ImageUrl),
                    StreamUrl = data.StreamUrl == null ? null : SecretRedactor.Detokenize(data.StreamUrl),
                };
            }
            return true;
        }

        public void Put(MediaItemData data)
        {
            // Hold the tokenized form in memory so the saved snapshot is always secret-free
            _cache[data.Id] = Tokenize(data);
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            // Only schedule one save at a time; additional Puts during the delay are included
            if (Interlocked.CompareExchange(ref _saveScheduled, 1, 0) == 0)
            {
                _ = Task.Delay(500).ContinueWith(_ =>
                {
                    Interlocked.Exchange(ref _saveScheduled, 0);
                    return SaveAsync();
                }).Unwrap();
            }
        }

        private async Task SaveAsync()
        {
            await _saveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Tokenize again at save time: entries loaded from an old file before a provider
                // registered its secrets are only cleaned once the secret is known.
                var snapshot = new Dictionary<string, MediaItemData>();
                foreach (var (id, data) in _cache)
                    snapshot[id] = Tokenize(data);
                var json = JsonSerializer.Serialize(snapshot, BairdJsonContext.Default.DictionaryStringMediaItemData);
                await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
                Console.WriteLine($"[MediaDataCache] Saved {snapshot.Count} items to disk.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MediaDataCache] Error saving cache: {ex.Message}");
            }
            finally
            {
                _saveLock.Release();
            }
        }
    }
}
