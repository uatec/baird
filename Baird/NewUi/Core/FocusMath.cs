using System;
using System.Collections.Generic;

namespace Baird.NewUi.Core
{
    public enum Dir { Up, Down, Left, Right }

    /// <summary>Pure focus movement, ported from the prototype (js/focus.js).</summary>
    public static class FocusMath
    {
        /// <summary>
        /// Rows of items (counts per row). Left/right move within a row; up/down restore the column last used on the
        /// row you arrive at (memory), clamped to its length. At an edge the position doesn't change.
        /// </summary>
        public static (int Row, int Col) MoveInRows(IReadOnlyList<int> counts, (int Row, int Col) pos, Dir dir, Dictionary<int, int> memory)
        {
            var (row, col) = pos;
            if (dir == Dir.Left) return (row, Math.Max(0, col - 1));
            if (dir == Dir.Right) return (row, Math.Min(counts[row] - 1, col + 1));
            var next = dir == Dir.Up ? row - 1 : row + 1;
            if (next < 0 || next >= counts.Count) return (row, col);
            memory[row] = col;
            var remembered = memory.TryGetValue(next, out var m) ? m : col;
            return (next, Math.Min(counts[next] - 1, remembered));
        }
    }
}
