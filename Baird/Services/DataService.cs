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
        private readonly IMediaDataCache _mediaDataCache;

        public event EventHandler? HistoryUpdated;
        public event EventHandler? WatchlistUpdated;
        public event EventHandler<MediaItemViewModel>? ItemAddedToWatchlist;

        public DataService(IEnumerable<IMediaProvider> providers, IHistoryService historyService, IWatchlistService watchlistService, IMediaItemCache cache, IMediaDataCache mediaDataCache)
        {
            _providers = providers;
            _historyService = historyService;
            _watchlistService = watchlistService;
            _cache = cache;
            _mediaDataCache = mediaDataCache;

            _watchlistService.WatchlistUpdated += (s, e) => WatchlistUpdated?.Invoke(this, EventArgs.Empty);
        }

        public async Task<IEnumerable<MediaItemViewModel>> GetListingAsync()
        {
            var tasks = _providers.Select(p => p.GetListingAsync());
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var allData = results.SelectMany(x => x).ToList();
            foreach (var data in allData)
                _mediaDataCache.Put(data);
            var items = allData.Select(data => new MediaItemViewModel(data)).ToList();
            return UnifyAndHydrate(items);
        }

        public async Task<IEnumerable<MediaItemViewModel>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            var tasks = _providers.Select(p => p.SearchAsync(query, cancellationToken));
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var allData = results.SelectMany(x => x).ToList();
            foreach (var data in allData)
                _mediaDataCache.Put(data);
            var items = allData.Select(data => new MediaItemViewModel(data)).ToList();
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

        // Hydrate history items using persistent cache for immediate display,
        // then refresh from providers in the background.
        private async Task<IEnumerable<MediaItemViewModel>> HydrateHistoryItems(IEnumerable<HistoryItem> historyItems)
        {
            var tasks = historyItems.Select(async h =>
            {
                var media = await GetItemAsync(h.Id);
                if (media != null)
                {
                    media.History = h;
                    return media;
                }
                // Item not in cache and no provider could supply it
                return null;
            });

            var results = await Task.WhenAll(tasks);
            return results.Where(x => x != null)!;
        }

        public async Task<IEnumerable<MediaItemViewModel>> GetChildrenAsync(string id)
        {
            var tasks = _providers.Select(p => p.GetChildrenAsync(id));
            var results = await Task.WhenAll(tasks);
            var allData = results.SelectMany(x => x).ToList();
            foreach (var data in allData)
                _mediaDataCache.Put(data);
            var items = allData.Select(data => new MediaItemViewModel(data)).ToList();
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
                var item = await GetItemAsync(id);
                return item;
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
            // 1. Check in-memory ViewModel cache (fastest)
            if (_cache.TryGet(id, out var cachedItem))
            {
                cachedItem!.History = _historyService.GetProgress(id);
                cachedItem!.IsOnWatchlist = _watchlistService.IsOnWatchlist(id);
                return cachedItem;
            }

            // 2. Check persistent data cache (fast - no network)
            if (_mediaDataCache.TryGet(id, out var cachedData))
            {
                var item = _cache.GetOrCreate(id, () => new MediaItemViewModel(cachedData!));
                item.History = _historyService.GetProgress(id);
                item.IsOnWatchlist = _watchlistService.IsOnWatchlist(id);

                // Refresh from provider in the background so data stays current
                _ = RefreshItemFromProvidersAsync(id, item);

                return item;
            }

            // 3. Load from providers (slow - network request)
            return await LoadItemFromProvidersAsync(id).ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches fresh data from providers and updates the ViewModel in place.
        /// Called in the background after serving from the persistent cache.
        /// </summary>
        private async Task RefreshItemFromProvidersAsync(string id, MediaItemViewModel item)
        {
            foreach (var provider in _providers)
            {
                try
                {
                    var data = await provider.GetItemAsync(id).ConfigureAwait(false);
                    if (data != null)
                    {
                        _mediaDataCache.Put(data);
                        item.UpdateData(data);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DataService] Background refresh failed for {id} via {provider.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Loads an item from providers (network), stores it in both caches, and returns the ViewModel.
        /// </summary>
        private async Task<MediaItemViewModel?> LoadItemFromProvidersAsync(string id)
        {
            foreach (var provider in _providers)
            {
                try
                {
                    var data = await provider.GetItemAsync(id).ConfigureAwait(false);
                    if (data != null)
                    {
                        _mediaDataCache.Put(data);
                        var item = _cache.GetOrCreate(id, () => new MediaItemViewModel(data));
                        item.History = _historyService.GetProgress(id);
                        item.IsOnWatchlist = _watchlistService.IsOnWatchlist(id);
                        return item;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DataService] Provider lookup failed for {id} via {provider.GetType().Name}: {ex.Message}");
                }
            }
            return null;
        }

        public void AttachHistory(IEnumerable<MediaItemViewModel> items)
        {
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
    }
}
