// Pure focus movement: Home-style rows, and the guide grid (channels down, programmes across).

/** rows: item count per row. memory: Map<row, col> of the column last used on each row. */
export function moveInRows(rows, pos, dir, memory) {
  let { row, col } = pos;
  if (dir === 'left') return { row, col: Math.max(0, col - 1) };
  if (dir === 'right') return { row, col: Math.min(rows[row] - 1, col + 1) };
  const next = dir === 'up' ? row - 1 : dir === 'down' ? row + 1 : row;
  if (next === row || next < 0 || next >= rows.length) return { row, col };
  memory.set(row, col);
  const remembered = memory.has(next) ? memory.get(next) : col;
  return { row: next, col: Math.min(rows[next] - 1, remembered) };
}

const programmeAt = (list, ms) =>
  list.find(p => p.start <= ms && ms < p.end) ?? (ms < list[0].start ? list[0] : list.at(-1));

/**
 * grid: { windowStart: ms, now?: ms, channels: Programme[][] }.  pos: { ch, progId, anchorMs }.
 * ←/→ step programmes and move the anchor to the new start; ↑/↓ keep the anchor so long programmes don't drag time.
 */
export function moveInGuide(grid, pos, dir, pageSize = 6) {
  const list = grid.channels[pos.ch];
  const idx = list.findIndex(p => p.id === pos.progId);
  if (dir === 'left' || dir === 'right') {
    const j = dir === 'left' ? idx - 1 : idx + 1;
    if (j < 0 || j >= list.length || list[j].end <= grid.windowStart) return pos;
    const p = list[j];
    // A programme that is on now anchors on now; otherwise on its start (never before the window).
    const live = grid.now !== undefined && p.start <= grid.now && grid.now < p.end;
    return { ch: pos.ch, progId: p.id, anchorMs: live ? grid.now : Math.max(p.start, grid.windowStart) };
  }
  const delta = { up: -1, down: 1, pageup: -pageSize, pagedown: pageSize }[dir];
  if (delta === undefined) return pos;
  const ch = Math.max(0, Math.min(grid.channels.length - 1, pos.ch + delta));
  if (ch === pos.ch) return pos;
  return { ch, progId: programmeAt(grid.channels[ch], pos.anchorMs).id, anchorMs: pos.anchorMs };
}

export function jumpGuideToChannel(grid, ch, anchorMs) {
  return { ch, progId: programmeAt(grid.channels[ch], anchorMs).id, anchorMs };
}

export function jumpGuideToTime(grid, pos, ms) {
  return { ch: pos.ch, progId: programmeAt(grid.channels[pos.ch], ms).id, anchorMs: ms };
}
