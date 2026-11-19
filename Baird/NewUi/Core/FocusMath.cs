using System;
using System.Collections.Generic;

namespace Baird.NewUi.Core
{
    public enum Dir { Up, Down, Left, Right }

    /// <summary>Pure focus movement, ported from the prototype (js/focus.js).</summary>
    public static class FocusMath
    {
        /// <summary>
        /// Where a row of cards starts scrolling: the index of its first card on screen, given the one it showed before. The row stays put
        /// while the lit card is on screen, so it can be lit anywhere across the screen, and only moves when the lit card would run off the
        /// edge (just far enough to bring it back, so going back the other way doesn't scroll until the first card is passed).
        /// </summary>
        /// <param name="first">The first card on screen before.</param>
        /// <param name="lit">The lit card.</param>
        /// <param name="visible">How many whole cards fit on screen.</param>
        /// <param name="count">How many cards the row has.</param>
        public static int ScrollFirst(int first, int lit, int visible, int count)
        {
            visible = Math.Max(1, visible);
            if (lit < first) first = lit;
            else if (lit >= first + visible) first = lit - visible + 1;
            return Math.Clamp(first, 0, Math.Max(0, count - visible));
        }

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
