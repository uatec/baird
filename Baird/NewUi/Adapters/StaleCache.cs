using System;
using System.Collections.Generic;

namespace Baird.NewUi.Adapters
{
    /// <summary>
    /// A small in-memory cache that never expires an entry: age only says whether it is still fresh. A stale value is still
    /// served (by the caller, who also refreshes it in the background), so a slow or failing source never leaves a page empty.
    /// Thread-safe.
    /// </summary>
    public sealed class StaleCache<T>
    {
        private readonly TimeSpan _fresh;
        private readonly Func<DateTimeOffset> _now;
        private readonly Dictionary<string, (T Value, DateTimeOffset At)> _items = new();

        public StaleCache(TimeSpan fresh, Func<DateTimeOffset> now) { _fresh = fresh; _now = now; }

        /// <summary>The kept value for the key, and whether it is older than the fresh period. False when nothing is kept.</summary>
        public bool TryGet(string key, out T value, out bool stale)
        {
            lock (_items)
            {
                if (_items.TryGetValue(key, out var e)) { value = e.Value; stale = _now() - e.At >= _fresh; return true; }
            }
            value = default!; stale = false;
            return false;
        }

        /// <summary>The kept value for the key and how long ago it was kept (whatever the fresh period). False when nothing is kept.</summary>
        public bool TryGetAge(string key, out T value, out TimeSpan age)
        {
            lock (_items)
            {
                if (_items.TryGetValue(key, out var e)) { value = e.Value; age = _now() - e.At; return true; }
            }
            value = default!; age = default;
            return false;
        }

        /// <summary>Keep the value (replacing any), as fresh as of now.</summary>
        public void Put(string key, T value) { lock (_items) _items[key] = (value, _now()); }

        /// <summary>Forget everything.</summary>
        public void Clear() { lock (_items) _items.Clear(); }
    }
}
