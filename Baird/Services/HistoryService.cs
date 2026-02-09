using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Baird.Services
{
    public interface IHistoryService
    {
        Task UpsertAsync(MediaItem item, TimeSpan position, TimeSpan duration);
        Task<List<MediaItem>> GetHistoryAsync();
        MediaItem? GetProgress(string id);
    }

    public class JsonHistoryService : IHistoryService
    {
        private readonly string _filePath;
        private List<MediaItem> _historyCache;
        private bool _isDirty;

        public JsonHistoryService()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _filePath = Path.Combine(folder, "history.json");
            _historyCache = LoadHistory();
        }

        private List<MediaItem> LoadHistory()
        {
            if (!File.Exists(_filePath)) return new List<MediaItem>();
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<MediaItem>>(json) ?? new List<MediaItem>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading history: {ex}");
                return new List<MediaItem>();
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

        public async Task UpsertAsync(MediaItem item, TimeSpan position, TimeSpan duration)
        {
            if (item == null || string.IsNullOrEmpty(item.Id)) return;
    
            UpdateItem(item, position, duration);

            await SaveHistoryAsync();
        }

        private void UpdateItem(MediaItem item, TimeSpan position, TimeSpan duration)
        {
            var existing = _historyCache.FirstOrDefault(x => x.Id == item.Id);
            if (existing == null)
            {
                existing = new MediaItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    Details = item.Details,
                    ImageUrl = item.ImageUrl,
                    IsLive = item.IsLive,
                    StreamUrl = item.StreamUrl,
                    Source = item.Source,
                    ChannelNumber = item.ChannelNumber,
                    Type = item.Type,
                    Synopsis = item.Synopsis,
                    Subtitle = item.Subtitle
                };
                _historyCache.Add(existing);
            }

            existing.LastWatched = DateTime.Now;
            existing.LastPosition = position;
            
            double progress = position.TotalSeconds / duration.TotalSeconds;
            existing.Progress = existing.IsLive ? 0 : Math.Clamp(progress, 0.0, 1.0);

            // Finished Logic
            // Came within 5% of end for short videos
            // Came within 10 minutes of end for longer videos (e.g. Movies > 90 mins)
            
            double remainingSeconds = duration.TotalSeconds - position.TotalSeconds;
            bool isFinished = false;

            if (duration.TotalMinutes > 90)
            {
                // Long video: 10 minute threshold
                if (remainingSeconds < 600) isFinished = true; 
            }
            else
            {
                // Short/Medium video: 5% threshold
                if (remainingSeconds < (duration.TotalSeconds * 0.05)) isFinished = true;
            }
            
            existing.IsFinished = existing.IsLive || isFinished;
        }

        public async Task<List<MediaItem>> GetHistoryAsync()
        {
            // Requirement: "show a grid of each video that is not finished"
            // And "order the grid by most recent first"
            return _historyCache
                .Where(x => !x.IsFinished || x.IsLive)
                .OrderByDescending(x => x.LastWatched)
                .ToList();
        }

        public MediaItem? GetProgress(string id)
        {
            return _historyCache.FirstOrDefault(x => x.Id == id);
        }
    }
}
