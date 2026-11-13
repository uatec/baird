using System;
using System.Collections.Generic;

namespace Baird.NewUi.Controls
{
    /// <summary>
    /// Keeps the most recently used values while their total size stays within a budget (the newest value is always kept,
    /// however big). Used for decoded artwork: a bitmap's pixels are native memory the garbage collector can't see, so an
    /// unbounded cache grows until the Pi swaps.
    /// </summary>
    public sealed class ByteBudgetLru<T> where T : class
    {
        private readonly long _budget;
        private readonly Func<T, long> _size;
        private readonly LinkedList<(string Key, T Value, long Size)> _order = new();
        private readonly Dictionary<string, LinkedListNode<(string Key, T Value, long Size)>> _byKey = new();

        public ByteBudgetLru(long budgetBytes, Func<T, long> size) { _budget = budgetBytes; _size = size; }

        public int Count { get { lock (_order) return _byKey.Count; } }
        public long Bytes { get { lock (_order) return _bytes; } }
        private long _bytes;

        public bool TryGet(string key, out T? value)
        {
            lock (_order)
            {
                if (!_byKey.TryGetValue(key, out var node)) { value = null; return false; }
                _order.Remove(node); _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        /// <summary>Adds (or replaces) a value; returns how many older values were dropped to stay within the budget.</summary>
        public int Put(string key, T value)
        {
            lock (_order)
            {
                if (_byKey.TryGetValue(key, out var old)) { _bytes -= old.Value.Size; _order.Remove(old); }
                var size = Math.Max(0, _size(value));
                _byKey[key] = _order.AddFirst((key, value, size));
                _bytes += size;
                var dropped = 0;
                while (_bytes > _budget && _order.Count > 1)
                {
                    var last = _order.Last!;
                    _bytes -= last.Value.Size;
                    _byKey.Remove(last.Value.Key);
                    _order.RemoveLast();
                    dropped++;
                }
                return dropped;
            }
        }
    }
}
