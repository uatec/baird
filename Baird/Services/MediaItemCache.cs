using System;
using System.Collections.Generic;
using Baird.ViewModels;

namespace Baird.Services
{
    /// <summary>
    /// Cache service for MediaItemViewModel instances to ensure reference equality across the application.
    /// This is important for reactive UI updates - ViewModels need to share the same MediaItemViewModel instance.
    /// </summary>
    public interface IMediaItemCache
    {
        /// <summary>
        /// Gets a cached item by ID, or creates and caches it using the factory function.
        /// </summary>
        MediaItemViewModel GetOrCreate(string id, Func<MediaItemViewModel> factory);
        
        /// <summary>
        /// Tries to get a cached item by ID.
        /// </summary>
        bool TryGet(string id, out MediaItemViewModel? item);
        
        /// <summary>
        /// Clears all cached items.
        /// </summary>
        void Clear();
        
        /// <summary>
        /// Clears cached items from a specific source (e.g., "Live TV", "Jellyfin: home").
        /// </summary>
        void ClearSource(string source);
    }

    public class MediaItemCache : IMediaItemCache
    {
        private readonly Dictionary<string, MediaItemViewModel> _cache = new();
        private readonly object _lock = new();

        public MediaItemViewModel GetOrCreate(string id, Func<MediaItemViewModel> factory)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("ID cannot be null or empty", nameof(id));
            }

            lock (_lock)
            {
                if (_cache.TryGetValue(id, out var cachedItem))
                {
                    return cachedItem;
                }

                var newItem = factory();
                _cache[id] = newItem;
                return newItem;
            }
        }

        public bool TryGet(string id, out MediaItemViewModel? item)
        {
            if (string.IsNullOrEmpty(id))
            {
                item = null;
                return false;
            }

            lock (_lock)
            {
                return _cache.TryGetValue(id, out item);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
            }
        }

        public void ClearSource(string source)
        {
            lock (_lock)
            {
                var keysToRemove = new List<string>();
                foreach (var kvp in _cache)
                {
                    if (kvp.Value.Source == source)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _cache.Remove(key);
                }
            }
        }
    }
}
