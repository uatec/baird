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
            if (item.IsLive) 
            {
                 // For live items, we might just want to record that it was watched, 
                 // but we can't really "resume" them. 
                 // The requirement says: "For live video we cannot resume, so we just need to record the fact that it WAS watched"
                 // So we update LastWatched but maybe set IsFinished = true or Progress = 0?
                 // Let's treat them as finished immediately so they don't clutter "Resume" list, 
                 // unless "History" implies "Recently Watched" including live?
                 // User said: "show a grid of each video that is not finished". 
                 // So Live items should probably NOT appear in that grid if they are "finished" by definition.
                 // But we might want to keep them in the DB for other uses.
                 // I'll mark them as IsFinished = true.
                 UpdateItem(item, position, duration, isLive: true);
            }
            else
            {
                UpdateItem(item, position, duration, isLive: false);
            }

            await SaveHistoryAsync();
        }

        private void UpdateItem(MediaItem item, TimeSpan position, TimeSpan duration, bool isLive)
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
            
            // Calculation logic
            // Calculation logic
            if (isLive)
            {
                existing.Progress = 1.0;
                existing.IsFinished = true;
            }
            else if (duration.TotalSeconds <= 0)
            {
                // Invalid duration for VOD, do not update progress/finished state
                // Keep existing state or default?
                // If we don't know duration, we can't calculate progress.
                // Better to just not mark it as finished if it wasn't already.
                // But we already set LastWatched.
                // Let's just return or set progress to 0 if it was new?
                // If new, it defaults to 0 and false.
                // If existing, we don't want to overwrite valid progress with 0/Finished.
                
                // If we are here, we might have a valid position but 0 duration?
                // MPV might give pos but not dur yet? 
                // Let's assume if duration is 0, we can't do anything useful for progress.
                return; 
            }
            else
            {
                double progress = position.TotalSeconds / duration.TotalSeconds;
                existing.Progress = Math.Clamp(progress, 0.0, 1.0);

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
                
                existing.IsFinished = isFinished;
            }
        }

        public async Task<List<MediaItem>> GetHistoryAsync()
        {
            // Requirement: "show a grid of each video that is not finished"
            // And "order the grid by most recent first"
            return _historyCache
                .Where(x => !x.IsFinished)
                .OrderByDescending(x => x.LastWatched)
                .ToList();
        }

        public MediaItem? GetProgress(string id)
        {
            return _historyCache.FirstOrDefault(x => x.Id == id);
        }
    }
}
