using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    public interface IWatchlistService
    {
        Task AddAsync(MediaItem item);
        Task RemoveAsync(string id);
        Task<List<MediaItem>> GetWatchlistAsync();
        bool IsOnWatchlist(string id);
        event EventHandler? WatchlistUpdated;
    }

    public class JsonWatchlistService : IWatchlistService
    {
        private readonly string _filePath;
        private List<MediaItem> _watchlistCache;
        public event EventHandler? WatchlistUpdated;

        public JsonWatchlistService()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _filePath = Path.Combine(folder, "watchlist.json");
            _watchlistCache = LoadWatchlist();
        }

        private List<MediaItem> LoadWatchlist()
        {
            if (!File.Exists(_filePath)) return new List<MediaItem>();
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize(json, BairdJsonContext.Default.ListMediaItem) ?? new List<MediaItem>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WatchlistService] Error loading watchlist: {ex}");
                return new List<MediaItem>();
            }
        }

        private async Task SaveWatchlistAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(_watchlistCache, BairdJsonContext.Default.ListMediaItem);
                await File.WriteAllTextAsync(_filePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WatchlistService] Error saving watchlist: {ex}");
            }
        }

        public async Task AddAsync(MediaItem item)
        {
            if (IsOnWatchlist(item.Id)) return;

            _watchlistCache.Add(item);
            await SaveWatchlistAsync();
            WatchlistUpdated?.Invoke(this, EventArgs.Empty);
        }

        public async Task RemoveAsync(string id)
        {
            var item = _watchlistCache.FirstOrDefault(x => x.Id == id);
            if (item != null)
            {
                _watchlistCache.Remove(item);
                await SaveWatchlistAsync();
                WatchlistUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        public Task<List<MediaItem>> GetWatchlistAsync()
        {
            return Task.FromResult(_watchlistCache.ToList());
        }

        public bool IsOnWatchlist(string id)
        {
            return _watchlistCache.Any(x => x.Id == id);
        }
    }
}
