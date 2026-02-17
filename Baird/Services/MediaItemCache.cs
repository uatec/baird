using System;
using System.Collections.Generic;

namespace Baird.Services
{
    /// <summary>
    /// Cache service for MediaItem instances to ensure reference equality across the application.
    /// This is important for reactive UI updates - ViewModels need to share the same MediaItem instance.
    /// </summary>
    public interface IMediaItemCache
    {
        /// <summary>
        /// Gets a cached item by ID, or creates and caches it using the factory function.
        /// </summary>
        MediaItem GetOrCreate(string id, Func<MediaItem> factory);
        
        /// <summary>
        /// Tries to get a cached item by ID.
        /// </summary>
        bool TryGet(string id, out MediaItem? item);
        
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
        private readonly Dictionary<string, MediaItem> _cache = new();
        private readonly object _lock = new();

        public MediaItem GetOrCreate(string id, Func<MediaItem> factory)
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

        public bool TryGet(string id, out MediaItem? item)
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
