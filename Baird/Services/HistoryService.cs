using System.Text.Json;
using Baird.Models;

namespace Baird.Services;

public interface IHistoryService
{
    Task UpsertAsync(MediaItem media, TimeSpan position, TimeSpan duration);
    Task<List<HistoryItem>> GetHistoryAsync();
    HistoryItem? GetProgress(string id);
}

public class JsonHistoryService : IHistoryService
{
    private readonly string _filePath;
    private readonly List<HistoryItem> _historyCache;

    public JsonHistoryService()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird");
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
        _filePath = Path.Combine(folder, "history_v4.json"); // Bump version for new format
        _historyCache = LoadHistory();
    }

    private List<HistoryItem> LoadHistory()
    {
        if (!File.Exists(_filePath))
        {
            return new List<HistoryItem>();
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize(json, BairdJsonContext.Default.ListHistoryItem) ?? new List<HistoryItem>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HistoryService] Error loading history (v4): {ex}");
            return new List<HistoryItem>();
        }
    }

    private async Task SaveHistoryAsync()
    {
        try
        {
            string json = JsonSerializer.Serialize(_historyCache, BairdJsonContext.Default.ListHistoryItem);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HistoryService] Error saving history: {ex}");
        }
    }

    public async Task UpsertAsync(MediaItem media, TimeSpan position, TimeSpan duration)
    {
        if (media == null || string.IsNullOrEmpty(media.Id))
        {
            return;
        }

        UpdateItem(media.Id, position, duration);
        await SaveHistoryAsync();
    }

    private void UpdateItem(string id, TimeSpan position, TimeSpan duration)
    {
        HistoryItem? existing = _historyCache.FirstOrDefault(x => x.Id == id);
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
            if (remainingSeconds < 600)
            {
                isFinished = true;
            }
        }
        else
        {
            if (remainingSeconds < (duration.TotalSeconds * 0.05))
            {
                isFinished = true;
            }
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
