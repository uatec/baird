using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    public interface IHistoryService
    {
        Task UpsertAsync(MediaItem media, TimeSpan position, TimeSpan duration);
        Task<List<HistoryItem>> GetHistoryAsync();
        HistoryItem? GetProgress(string id);
    }

    public class JsonHistoryService : IHistoryService
    {
        private readonly string _filePath;
        private List<HistoryItem> _historyCache;

        public JsonHistoryService()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _filePath = Path.Combine(folder, "history_v4.json"); // Bump version for new format
            _historyCache = LoadHistory();
        }

        private List<HistoryItem> LoadHistory()
        {
            if (!File.Exists(_filePath)) return new List<HistoryItem>();
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<HistoryItem>>(json) ?? new List<HistoryItem>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading history (v4): {ex}");
                return new List<HistoryItem>();
            }
        }

        private async Task SaveHistoryAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(_historyCache, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_filePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving history: {ex}");
            }
        }

        public async Task UpsertAsync(MediaItem media, TimeSpan position, TimeSpan duration)
        {
            if (media == null || string.IsNullOrEmpty(media.Id)) return;

            UpdateItem(media.Id, position, duration);
            await SaveHistoryAsync();
        }

        private void UpdateItem(string id, TimeSpan position, TimeSpan duration)
        {
            var existing = _historyCache.FirstOrDefault(x => x.Id == id);
            if (existing == null)
            {
                existing = new HistoryItem
                {
                    Id = id,
                    Duration = duration
                };
                _historyCache.Add(existing);
            }

            existing.LastWatched = DateTime.Now;
            existing.LastPosition = position;
            existing.Duration = duration;

            double remainingSeconds = duration.TotalSeconds - position.TotalSeconds;
            bool isFinished = false;

            if (duration.TotalMinutes > 90)
            {
                if (remainingSeconds < 600) isFinished = true;
            }
            else
            {
                if (remainingSeconds < (duration.TotalSeconds * 0.05)) isFinished = true;
            }

            existing.IsFinished = isFinished;
        }

        public async Task<List<HistoryItem>> GetHistoryAsync()
        {
            return _historyCache
                .OrderByDescending(x => x.LastWatched)
                .ToList();
        }

        public HistoryItem? GetProgress(string id)
        {
            return _historyCache.FirstOrDefault(x => x.Id == id);
        }
    }
}
