using System;
using System.Collections.Generic;
using System.Linq;
using Baird.NewUi.Model;

namespace Baird.NewUi.Core
{
    /// <summary>
    /// Where focus is in the guide: a channel row, the lit programme, and the time you're "in" (the anchor), so moving
    /// up and down keeps your place in time rather than jumping to whatever the neighbouring row starts with.
    /// </summary>
    public sealed record GuidePos(int Ch, string ProgId, DateTimeOffset Anchor);

    /// <summary>Each channel's schedule (gap-free, see GuideData), the earliest time shown, and now.</summary>
    public sealed record GuideGrid(IReadOnlyList<IReadOnlyList<Programme>> Channels, DateTimeOffset WindowStart, DateTimeOffset Now);

    public enum GuideMove { Up, Down, Left, Right, PageUp, PageDown }

    /// <summary>Pure focus movement in the guide grid, ported from the prototype (js/focus.js: moveInGuide).</summary>
    public static class GuideFocus
    {
        /// <summary>The programme on at <paramref name="t"/>, else the nearest one (schedules are gap-free, so only at the ends).</summary>
        public static Programme At(IReadOnlyList<Programme> list, DateTimeOffset t) =>
            list.FirstOrDefault(p => p.IsOnAt(t)) ?? (t < list[0].Start ? list[0] : list[^1]);

        public static Programme Lit(GuideGrid grid, GuidePos pos) =>
            grid.Channels[pos.Ch].FirstOrDefault(p => p.Id == pos.ProgId) ?? At(grid.Channels[pos.Ch], pos.Anchor);

        public static GuidePos ToChannel(GuideGrid grid, int ch, DateTimeOffset anchor) => new(ch, At(grid.Channels[ch], anchor).Id, anchor);

        public static GuidePos ToTime(GuideGrid grid, GuidePos pos, DateTimeOffset t) => new(pos.Ch, At(grid.Channels[pos.Ch], t).Id, t);

        public static GuidePos Move(GuideGrid grid, GuidePos pos, GuideMove move, int pageSize = 6)
        {
            var list = grid.Channels[pos.Ch];
            if (move is GuideMove.Left or GuideMove.Right)
            {
                var idx = IndexOf(list, pos.ProgId);
                var j = move == GuideMove.Left ? idx - 1 : idx + 1;
                if (j < 0 || j >= list.Count || list[j].End <= grid.WindowStart) return pos;
                var p = list[j];
                // A programme on now anchors on now; otherwise on its start (never before the window).
                return new GuidePos(pos.Ch, p.Id, p.IsOnAt(grid.Now) ? grid.Now : Max(p.Start, grid.WindowStart));
            }
            var delta = move switch { GuideMove.Up => -1, GuideMove.Down => 1, GuideMove.PageUp => -pageSize, _ => pageSize };
            var ch = Math.Clamp(pos.Ch + delta, 0, grid.Channels.Count - 1);
            return ch == pos.Ch ? pos : ToChannel(grid, ch, pos.Anchor);
        }

        private static int IndexOf(IReadOnlyList<Programme> list, string id)
        {
            for (var i = 0; i < list.Count; i++) if (list[i].Id == id) return i;
            return 0;
        }

        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    }
}
