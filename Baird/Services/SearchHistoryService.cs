using System.Text.Json;

namespace Baird.Services;

internal class SearchTermItem
{
    public string Term { get; set; } = "";
    public DateTime LastUsed { get; set; }
    public int UseCount { get; set; }
}

public class SearchHistoryService : ISearchHistoryService
{
    private readonly string _filePath;
    private readonly List<SearchTermItem> _cache;

    public SearchHistoryService()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird");
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
        _filePath = Path.Combine(folder, "search_history.json");
        _cache = LoadHistory();
    }

    private List<SearchTermItem> LoadHistory()
    {
        if (!File.Exists(_filePath))
        {
            return new List<SearchTermItem>();
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize(json, BairdJsonContext.Default.ListSearchTermItem) ?? new List<SearchTermItem>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SearchHistoryService] Error loading search history: {ex}");
            return new List<SearchTermItem>();
        }
    }

    private async Task SaveHistoryAsync()
    {
        try
        {
            string json = JsonSerializer.Serialize(_cache, BairdJsonContext.Default.ListSearchTermItem);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SearchHistoryService] Error saving search history: {ex}");
        }
    }

    public async Task AddSearchTermAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }

        term = term.Trim();

        // Normalize term? Case insensitive check?
        // Let's keep original casing but search case-insensitively
        SearchTermItem? existing = _cache.FirstOrDefault(x => x.Term.Equals(term, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.UseCount++;
            existing.LastUsed = DateTime.Now;
            // Update casing to most recent usage?
            existing.Term = term;
        }
        else
        {
            _cache.Add(new SearchTermItem
            {
                Term = term,
                LastUsed = DateTime.Now,
                UseCount = 1
            });
        }

        await SaveHistoryAsync();
    }

    public Task<IEnumerable<string>> GetSuggestedTermsAsync(int maxCount)
    {
        DateTime now = DateTime.Now;

        // Simple scoring: Count / (DaysSince + 1)
        // This favors high frequency but decays old items.

        IEnumerable<string> sorted = _cache
            .Select(item => new
            {
                Item = item,
                Score = (double)item.UseCount / ((now - item.LastUsed).TotalDays + 1)
            })
            .OrderByDescending(x => x.Score)
            .Take(maxCount)
            .Select(x => x.Item.Term);

        return Task.FromResult(sorted);
    }
}
