using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
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

        // Event to notify when history is updated
        event EventHandler? HistoryUpdated;
        event EventHandler? WatchlistUpdated;

        IEnumerable<IMediaProvider> Providers { get; }
        void AttachHistory(IEnumerable<MediaItem> items);

        Task<IEnumerable<MediaItem>> GetWatchlistItemsAsync();
        Task AddToWatchlistAsync(MediaItem item);
        Task RemoveFromWatchlistAsync(string id);
        bool IsOnWatchlist(string id);
    }

    public class DataService : IDataService
    {
        private readonly IEnumerable<IMediaProvider> _providers;
        public IEnumerable<IMediaProvider> Providers => _providers;

        private readonly IHistoryService _historyService;
        private readonly IWatchlistService _watchlistService;

        public event EventHandler? HistoryUpdated;
        public event EventHandler? WatchlistUpdated;

        public DataService(IEnumerable<IMediaProvider> providers, IHistoryService historyService, IWatchlistService watchlistService)
        {
            _providers = providers;
            _historyService = historyService;
            _watchlistService = watchlistService;

            _watchlistService.WatchlistUpdated += (s, e) => WatchlistUpdated?.Invoke(this, EventArgs.Empty);
        }

        public async Task<IEnumerable<MediaItem>> GetListingAsync()
        {
            var tasks = _providers.Select(p => p.GetListingAsync());
            var results = await Task.WhenAll(tasks);
            var items = results.SelectMany(x => x).ToList();
            AttachHistory(items);
            return items;
        }

        public async Task<IEnumerable<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            var tasks = _providers.Select(p => p.SearchAsync(query, cancellationToken));
            var results = await Task.WhenAll(tasks);
            var items = results.SelectMany(x => x).ToList();
            AttachHistory(items);
            return items;
        }

        public async Task<IEnumerable<MediaItem>> GetContinueWatchingAsync()
        {
            var historyItems = await _historyService.GetHistoryAsync();

            // Only unfinished items
            var unfinished = historyItems.Where(x => !x.IsFinished).ToList();

            var mediaItems = await HydrateHistoryItems(unfinished);
            return mediaItems;
        }

        public async Task<IEnumerable<MediaItem>> GetHistoryItemsAsync()
        {
            var historyItems = await _historyService.GetHistoryAsync();

            var mediaItems = await HydrateHistoryItems(historyItems);
            return mediaItems;
        }

        // Helper to hydrate a list of history items
        private async Task<IEnumerable<MediaItem>> HydrateHistoryItems(IEnumerable<HistoryItem> historyItems)
        {
            // This could be slow if we do it sequentially or naive parallel.
            // We should limit concurrency or be smart.
            // For now, simple parallelism.

            var tasks = historyItems.Select(async h =>
            {
                // Try to find the MediaItem for this history item
                var media = await GetItemAsync(h.Id);
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

            var results = await Task.WhenAll(tasks);
            return results.Where(x => x != null && x.Source != "Unknown"); // Filter unknowns? User might want to see them to delete?
            // User requirement: "look at that up afterwards". Implicitly assumes availability.
            // Let's filter out Unknowns for now to avoid ugly UI.
        }

        // ...

        public async Task<IEnumerable<MediaItem>> GetChildrenAsync(string id)
        {
            var tasks = _providers.Select(p => p.GetChildrenAsync(id));
            var results = await Task.WhenAll(tasks);
            var items = results.SelectMany(x => x).ToList();
            AttachHistory(items);
            return items;
        }

        public async Task UpsertHistoryAsync(MediaItem item, TimeSpan position, TimeSpan duration)
        {
            await _historyService.UpsertAsync(item, position, duration);
            // Also update the local item's history
            item.History = _historyService.GetProgress(item.Id);

            // Notify that history was updated
            HistoryUpdated?.Invoke(this, EventArgs.Empty);
        }

        public HistoryItem? GetHistory(string id)
        {
            return _historyService.GetProgress(id);
        }

        public async Task<IEnumerable<MediaItem>> GetWatchlistItemsAsync()
        {
            var savedItems = await _watchlistService.GetWatchlistAsync();

            var tasks = savedItems.Select(async item =>
            {
                // Try to get fresh item from provider/cache
                var freshItem = await GetItemAsync(item.Id);
                if (freshItem != null)
                {
                    return freshItem;
                }

                // If provider fails, return the saved item but attach history manually
                item.History = _historyService.GetProgress(item.Id);
                return item;
            });

            var results = await Task.WhenAll(tasks);
            return results;
        }

        public async Task AddToWatchlistAsync(MediaItem item)
        {
            await _watchlistService.AddAsync(item);
        }

        public async Task RemoveFromWatchlistAsync(string id)
        {
            await _watchlistService.RemoveAsync(id);
        }

        public bool IsOnWatchlist(string id)
        {
            return _watchlistService.IsOnWatchlist(id);
        }

        private readonly Dictionary<string, MediaItem> _itemCache = new();

        public async Task<MediaItem?> GetItemAsync(string id)
        {
            // 1. Check cache
            if (_itemCache.TryGetValue(id, out var cachedItem))
            {
                // Ensure history is up to date even if item is cached
                cachedItem.History = _historyService.GetProgress(id);
                return cachedItem;
            }

            // 2. Iterate providers to find the item.
            foreach (var provider in _providers)
            {
                var item = await provider.GetItemAsync(id);
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

        public void AttachHistory(IEnumerable<MediaItem> items)
        {
            foreach (var item in items)
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
}
