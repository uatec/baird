using Baird.Models;

namespace Baird.Services;

public interface IDataService
{
    Task<IEnumerable<MediaItem>> GetListingAsync();
    Task<IEnumerable<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<IEnumerable<MediaItem>> GetContinueWatchingAsync();
    Task<IEnumerable<MediaItem>> GetHistoryItemsAsync();
    Task<IEnumerable<MediaItem>> GetChildrenAsync(string id);
    Task<MediaItem?> GetItemAsync(string id); // Might be needed if not present in listings
    Task UpsertHistoryAsync(MediaItem item, TimeSpan position, TimeSpan duration);
    HistoryItem? GetHistory(string id);
}

public class DataService : IDataService
{
    private readonly IEnumerable<IMediaProvider> _providers;
    private readonly IHistoryService _historyService;

    public DataService(IEnumerable<IMediaProvider> providers, IHistoryService historyService)
    {
        _providers = providers;
        _historyService = historyService;
    }

    public async Task<IEnumerable<MediaItem>> GetListingAsync()
    {
        IEnumerable<Task<IEnumerable<MediaItem>>> tasks = _providers.Select(p => p.GetListingAsync());
        IEnumerable<MediaItem>[] results = await Task.WhenAll(tasks);
        var items = results.SelectMany(x => x).ToList();
        AttachHistory(items);
        return items;
    }

    public async Task<IEnumerable<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        IEnumerable<Task<IEnumerable<MediaItem>>> tasks = _providers.Select(p => p.SearchAsync(query, cancellationToken));
        IEnumerable<MediaItem>[] results = await Task.WhenAll(tasks);
        var items = results.SelectMany(x => x).ToList();
        AttachHistory(items);
        return items;
    }

    public async Task<IEnumerable<MediaItem>> GetContinueWatchingAsync()
    {
        List<HistoryItem> historyItems = await _historyService.GetHistoryAsync();

        // Only unfinished items
        var unfinished = historyItems.Where(x => !x.IsFinished).ToList();

        IEnumerable<MediaItem> mediaItems = await HydrateHistoryItems(unfinished);
        return mediaItems;
    }

    public async Task<IEnumerable<MediaItem>> GetHistoryItemsAsync()
    {
        List<HistoryItem> historyItems = await _historyService.GetHistoryAsync();

        IEnumerable<MediaItem> mediaItems = await HydrateHistoryItems(historyItems);
        return mediaItems;
    }

    // Helper to hydrate a list of history items
    private async Task<IEnumerable<MediaItem>> HydrateHistoryItems(IEnumerable<HistoryItem> historyItems)
    {
        // This could be slow if we do it sequentially or naive parallel.
        // We should limit concurrency or be smart.
        // For now, simple parallelism.

        IEnumerable<Task<MediaItem>> tasks = historyItems.Select(async h =>
        {
            // Try to find the MediaItem for this history item
            MediaItem? media = await GetItemAsync(h.Id);
            if (media != null)
            {
                media.History = h;
                return media;
            }
            // If we can't find it (provider offline, item deleted), we can't show it easily
            // unless we returned a placeholder.
            // For now, return null and filter out.
            // Or maybe return a placeholder with ID?
            // "Unknown Item"
            return new MediaItem
            {
                Id = h.Id,
                Name = "Unknown Item",
                Details = "Item not found in providers",
                ImageUrl = "",
                IsLive = false,
                Source = "Unknown",
                Type = MediaType.Video,
                StreamUrl = "",
                Subtitle = "",
                Synopsis = "",
                History = h,
                Duration = h.Duration
            };
        });

        MediaItem[] results = await Task.WhenAll(tasks);
        return results.Where(x => x != null && x.Source != "Unknown"); // Filter unknowns? User might want to see them to delete?
        // User requirement: "look at that up afterwards". Implicitly assumes availability.
        // Let's filter out Unknowns for now to avoid ugly UI.
    }

    // ...

    public async Task<IEnumerable<MediaItem>> GetChildrenAsync(string id)
    {
        IEnumerable<Task<IEnumerable<MediaItem>>> tasks = _providers.Select(p => p.GetChildrenAsync(id));
        IEnumerable<MediaItem>[] results = await Task.WhenAll(tasks);
        var items = results.SelectMany(x => x).ToList();
        AttachHistory(items);
        return items;
    }

    public async Task UpsertHistoryAsync(MediaItem item, TimeSpan position, TimeSpan duration)
    {
        await _historyService.UpsertAsync(item, position, duration);
        // Also update the local item's history
        item.History = _historyService.GetProgress(item.Id);
    }

    public HistoryItem? GetHistory(string id)
    {
        return _historyService.GetProgress(id);
    }

    private readonly Dictionary<string, MediaItem> _itemCache = new();

    public async Task<MediaItem?> GetItemAsync(string id)
    {
        // 1. Check cache
        if (_itemCache.TryGetValue(id, out MediaItem? cachedItem))
        {
            // Ensure history is up to date even if item is cached
            cachedItem.History = _historyService.GetProgress(id);
            return cachedItem;
        }

        // 2. Iterate providers to find the item.
        foreach (IMediaProvider provider in _providers)
        {
            MediaItem? item = await provider.GetItemAsync(id);
            if (item != null)
            {
                item.History = _historyService.GetProgress(id);
                // 3. Cache the item
                _itemCache[id] = item;
                return item;
            }
        }
        return null;
    }

    private void AttachHistory(IEnumerable<MediaItem> items)
    {
        foreach (MediaItem item in items)
        {
            item.History = _historyService.GetProgress(item.Id);
            // Cache items found in listings to speed up future GetItemAsync calls
            if (!string.IsNullOrEmpty(item.Id) && !_itemCache.ContainsKey(item.Id))
            {
                _itemCache[item.Id] = item;
            }
        }
    }
}
