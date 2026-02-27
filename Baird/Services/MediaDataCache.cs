using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    /// <summary>
    /// Persistent disk cache for MediaItemData (names, image URLs, metadata).
    /// Allows Watchlist and History to display items immediately from cache,
    /// while fresh data is loaded from providers in the background.
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

        public JsonMediaDataCache()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            _filePath = Path.Combine(folder, "media_data_cache.json");
            _cache = new ConcurrentDictionary<string, MediaItemData>(Load());
        }

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
            return _cache.TryGetValue(id, out data);
        }

        public void Put(MediaItemData data)
        {
            _cache[data.Id] = data;
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
                var snapshot = new Dictionary<string, MediaItemData>(_cache);
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
