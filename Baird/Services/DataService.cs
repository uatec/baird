using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
using Baird.ViewModels;

namespace Baird.Services
{
    public interface IDataService
    {
        Task<IEnumerable<MediaItemViewModel>> GetListingAsync();
        Task<IEnumerable<MediaItemViewModel>> SearchAsync(string query, CancellationToken cancellationToken = default);
        Task<IEnumerable<MediaItemViewModel>> GetContinueWatchingAsync();
        Task<IEnumerable<MediaItemViewModel>> GetHistoryItemsAsync();
        Task<IEnumerable<MediaItemViewModel>> GetChildrenAsync(string id);
        Task<MediaItemViewModel?> GetItemAsync(string id); // Might be needed if not present in listings
        Task UpsertHistoryAsync(MediaItemViewModel item, TimeSpan position, TimeSpan duration);
        HistoryItem? GetHistory(string id);

        // Event to notify when history is updated
        event EventHandler? HistoryUpdated;
        event EventHandler? WatchlistUpdated;
        event EventHandler<MediaItemViewModel>? ItemAddedToWatchlist;

        IEnumerable<IMediaProvider> Providers { get; }
        void AttachHistory(IEnumerable<MediaItemViewModel> items);
        IEnumerable<MediaItemViewModel> UnifyAndHydrate(IEnumerable<MediaItemViewModel> items);

        Task<IEnumerable<MediaItemViewModel>> GetWatchlistItemsAsync();
        Task AddToWatchlistAsync(MediaItemViewModel item);
        Task RemoveFromWatchlistAsync(string id);
        bool IsOnWatchlist(string id);
    }

    public class DataService : IDataService
    {
        private readonly IEnumerable<IMediaProvider> _providers;
        public IEnumerable<IMediaProvider> Providers => _providers;

        private readonly IHistoryService _historyService;
        private readonly IWatchlistService _watchlistService;
        private readonly IMediaItemCache _cache;

        public event EventHandler? HistoryUpdated;
        public event EventHandler? WatchlistUpdated;
        public event EventHandler<MediaItemViewModel>? ItemAddedToWatchlist;

        public DataService(IEnumerable<IMediaProvider> providers, IHistoryService historyService, IWatchlistService watchlistService, IMediaItemCache cache)
        {
            _providers = providers;
            _historyService = historyService;
            _watchlistService = watchlistService;
            _cache = cache;

            _watchlistService.WatchlistUpdated += (s, e) => WatchlistUpdated?.Invoke(this, EventArgs.Empty);
        }

        public async Task<IEnumerable<MediaItemViewModel>> GetListingAsync()
        {
            var tasks = _providers.Select(p => p.GetListingAsync());
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var items = results.SelectMany(x => x).Select(data => new MediaItemViewModel(data)).ToList();
            return UnifyAndHydrate(items);
        }

        public async Task<IEnumerable<MediaItemViewModel>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            var tasks = _providers.Select(p => p.SearchAsync(query, cancellationToken));
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var items = results.SelectMany(x => x).Select(data => new MediaItemViewModel(data)).ToList();
            return UnifyAndHydrate(items);
        }

        public async Task<IEnumerable<MediaItemViewModel>> GetContinueWatchingAsync()
        {
            var historyItems = await _historyService.GetHistoryAsync().ConfigureAwait(false);

            // Only unfinished items
            var unfinished = historyItems.Where(x => !x.IsFinished).ToList();

            var hydrated = await HydrateHistoryItems(unfinished).ConfigureAwait(false);
            return UnifyAndHydrate(hydrated);
        }

        public async Task<IEnumerable<MediaItemViewModel>> GetHistoryItemsAsync()
        {
            var historyItems = await _historyService.GetHistoryAsync().ConfigureAwait(false);

            var mediaItems = await HydrateHistoryItems(historyItems).ConfigureAwait(false);
            return UnifyAndHydrate(mediaItems);
        }

        // Helper to hydrate a list of history items
        private async Task<IEnumerable<MediaItemViewModel>> HydrateHistoryItems(IEnumerable<HistoryItem> historyItems)
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
                var placeholderData = new MediaItemData
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
                    Duration = h.Duration
                };
                var placeholder = new MediaItemViewModel(placeholderData);
                placeholder.History = h;
                return placeholder;
            });

            var results = await Task.WhenAll(tasks);
            return results.Where(x => x != null && x.Source != "Unknown"); // Filter unknowns? User might want to see them to delete?
            // User requirement: "look at that up afterwards". Implicitly assumes availability.
            // Let's filter out Unknowns for now to avoid ugly UI.
        }

        // ...

        public async Task<IEnumerable<MediaItemViewModel>> GetChildrenAsync(string id)
        {
            var tasks = _providers.Select(p => p.GetChildrenAsync(id));
            var results = await Task.WhenAll(tasks);
            var items = results.SelectMany(x => x).Select(data => new MediaItemViewModel(data)).ToList();
            return UnifyAndHydrate(items);
        }

        public async Task UpsertHistoryAsync(MediaItemViewModel item, TimeSpan position, TimeSpan duration)
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

        public async Task<IEnumerable<MediaItemViewModel>> GetWatchlistItemsAsync()
        {
            var watchlistIds = await _watchlistService.GetWatchlistIdsAsync();

            var tasks = watchlistIds.Select(async id =>
            {
                // Try to get item from provider/cache
                var item = await GetItemAsync(id);
                if (item != null)
                {
                    return item;
                }

                // If provider fails, return null and filter out
                return null;
            });

            var results = await Task.WhenAll(tasks);
            return results.Where(item => item != null)!;
        }

        public async Task AddToWatchlistAsync(MediaItemViewModel item)
        {
            await _watchlistService.AddAsync(item.Id);

            // Update the item passed in
            item.IsOnWatchlist = true;

            // Also update cache if different instance exists
            if (_cache.TryGet(item.Id, out var cachedItem) && !ReferenceEquals(cachedItem, item))
            {
                cachedItem.IsOnWatchlist = true;
            }

            ItemAddedToWatchlist?.Invoke(this, item);
        }

        public async Task RemoveFromWatchlistAsync(string id)
        {
            await _watchlistService.RemoveAsync(id);
            if (_cache.TryGet(id, out var item))
            {
                item.IsOnWatchlist = false;
            }
        }

        public bool IsOnWatchlist(string id)
        {
            return _watchlistService.IsOnWatchlist(id);
        }

        public async Task<MediaItemViewModel?> GetItemAsync(string id)
        {
            // 1. Check cache
            if (_cache.TryGet(id, out var cachedItem))
            {
                // Ensure history is up to date even if item is cached
                cachedItem.History = _historyService.GetProgress(id);
                cachedItem.IsOnWatchlist = _watchlistService.IsOnWatchlist(id);
                return cachedItem;
            }

            // 2. Iterate providers to find the item.
            foreach (var provider in _providers)
            {
                var data = await provider.GetItemAsync(id);
                if (data != null)
                {
                    var item = _cache.GetOrCreate(id, () => new MediaItemViewModel(data));
                    item.History = _historyService.GetProgress(id);
                    item.IsOnWatchlist = _watchlistService.IsOnWatchlist(id);
                    return item;
                }
            }
            return null;
        }

        public void AttachHistory(IEnumerable<MediaItemViewModel> items)
        {
            // Legacy or unused? Just calling UnifyAndHydrate but ignoring return likely won't work for unification.
            // This method signature is void, so we can only hydrate.
            // We should probably remove/deprecate this or warn that it doesn't unify.
            foreach (var item in items)
            {
                HydrateSingle(item);
            }
        }

        private void HydrateSingle(MediaItemViewModel item)
        {
            item.History = _historyService.GetProgress(item.Id);
            item.IsOnWatchlist = _watchlistService.IsOnWatchlist(item.Id);
        }

        // Returns a list where items are replaced by cached versions if available
        public IEnumerable<MediaItemViewModel> UnifyAndHydrate(IEnumerable<MediaItemViewModel> items)
        {
            var unifiedList = new List<MediaItemViewModel>();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Id))
                {
                    unifiedList.Add(item);
                    continue;
                }

                // Use cache to get or create the canonical instance
                var cachedItem = _cache.GetOrCreate(item.Id, () => item);

                // Update state on the cached instance
                cachedItem.History = _historyService.GetProgress(item.Id);
                cachedItem.IsOnWatchlist = _watchlistService.IsOnWatchlist(item.Id);

                unifiedList.Add(cachedItem);
            }
            return unifiedList;
        }

        public void HydrateItems(IEnumerable<MediaItemViewModel> items)
        {
            // This method was void, modifying items in place. 
            // If we want unification, we need callers to use the return value of UnifyAndHydrate.
            // But existing callers might rely on void.
            // We've replaced callers to use UnifyAndHydrate's return value.
            // Keeping this for compatibility if needed, but implementation matches Unify logic essentially.
            foreach (var item in items)
            {
                HydrateSingle(item);
            }
        }
    }
}
