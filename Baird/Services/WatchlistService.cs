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
        Task AddAsync(string id);
        Task RemoveAsync(string id);
        Task<HashSet<string>> GetWatchlistIdsAsync();
        bool IsOnWatchlist(string id);
        event EventHandler? WatchlistUpdated;
    }

    public class JsonWatchlistService : IWatchlistService
    {
        private readonly string _filePath;
        private HashSet<string> _watchlistCache;
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

        private HashSet<string> LoadWatchlist()
        {
            if (!File.Exists(_filePath)) return new HashSet<string>();
            try
            {
                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize(json, BairdJsonContext.Default.ListString);
                return list != null ? new HashSet<string>(list) : new HashSet<string>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WatchlistService] Error loading watchlist: {ex.Message} - Starting with empty watchlist");
                return new HashSet<string>();
            }
        }

        private async Task SaveWatchlistAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(_watchlistCache.ToList(), BairdJsonContext.Default.ListString);
                await File.WriteAllTextAsync(_filePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WatchlistService] Error saving watchlist: {ex}");
            }
        }

        public async Task AddAsync(string id)
        {
            if (IsOnWatchlist(id)) return;

            _watchlistCache.Add(id);
            await SaveWatchlistAsync();
            WatchlistUpdated?.Invoke(this, EventArgs.Empty);
        }

        public async Task RemoveAsync(string id)
        {
            if (_watchlistCache.Remove(id))
            {
                await SaveWatchlistAsync();
                WatchlistUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        public Task<HashSet<string>> GetWatchlistIdsAsync()
        {
            return Task.FromResult(new HashSet<string>(_watchlistCache));
        }

        public bool IsOnWatchlist(string id)
        {
            return _watchlistCache.Contains(id);
        }
    }
}
