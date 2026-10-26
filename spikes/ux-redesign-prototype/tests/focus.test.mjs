import { test } from 'node:test';
import assert from 'node:assert/strict';
import { moveInRows, moveInGuide, jumpGuideToChannel, jumpGuideToTime } from '../js/focus.js';

test('rows: up/down remember the column used on each row, and clamp', () => {
  const rows = [5, 8, 4];
  const mem = new Map();
  let p = { row: 0, col: 3 };
  p = moveInRows(rows, p, 'down', mem);
  assert.deepEqual(p, { row: 1, col: 3 });
  for (let i = 0; i < 3; i++) p = moveInRows(rows, p, 'right', mem);
  assert.deepEqual(p, { row: 1, col: 6 });
  p = moveInRows(rows, p, 'up', mem);
  assert.deepEqual(p, { row: 0, col: 3 });
  p = moveInRows(rows, p, 'down', mem);
  assert.deepEqual(p, { row: 1, col: 6 });
  p = moveInRows(rows, p, 'down', mem);
  assert.deepEqual(p, { row: 2, col: 3 });
});

test('rows: edges stay put', () => {
  const mem = new Map();
  assert.deepEqual(moveInRows([3], { row: 0, col: 0 }, 'left', mem), { row: 0, col: 0 });
  assert.deepEqual(moveInRows([3], { row: 0, col: 2 }, 'right', mem), { row: 0, col: 2 });
  assert.deepEqual(moveInRows([3], { row: 0, col: 1 }, 'up', mem), { row: 0, col: 1 });
});

// Guide fixtures: minutes from 18:00 → ms
const M = 60_000, T0 = 1_000_000_000_000;
const prog = (id, startMin, endMin) => ({ id, start: T0 + startMin * M, end: T0 + endMin * M });
const grid = {
  windowStart: T0 - 60 * M,
  channels: [
    [prog('a0', -60, 60), prog('a1', 60, 90), prog('a2', 90, 120)],          // EastEnders a1 at 19:00
    [prog('b0', -60, 0), prog('b1', 0, 120)],                                 // film 18:00–20:00
    [prog('c0', -60, 60), prog('c1', 60, 90), prog('c2', 90, 150)],          // 19:00 and 19:30
  ],
};

test('guide: down keeps the anchor time across a long programme', () => {
  let p = { ch: 0, progId: 'a1', anchorMs: T0 + 60 * M };
  p = moveInGuide(grid, p, 'down');
  assert.equal(p.progId, 'b1');
  assert.equal(p.anchorMs, T0 + 60 * M);
  p = moveInGuide(grid, p, 'down');
  assert.equal(p.progId, 'c1');
});

test('guide: right/left move programmes and reset the anchor to the start', () => {
  let p = { ch: 0, progId: 'a1', anchorMs: T0 + 60 * M };
  p = moveInGuide(grid, p, 'right');
  assert.equal(p.progId, 'a2');
  assert.equal(p.anchorMs, T0 + 90 * M);
  p = moveInGuide(grid, p, 'left');
  p = moveInGuide(grid, p, 'left');
  assert.equal(p.progId, 'a0');
  assert.equal(p.anchorMs, grid.windowStart, 'anchor clamps to the window start');
  assert.equal(moveInGuide(grid, p, 'left').progId, 'a0', 'left at the first programme stays');
});

test('guide: page down moves pageSize channels and clamps', () => {
  const many = { windowStart: T0, channels: Array.from({ length: 15 }, (_, i) => [prog(`x${i}`, 0, 60)]) };
  assert.equal(moveInGuide(many, { ch: 2, progId: 'x2', anchorMs: T0 }, 'pagedown', 6).ch, 8);
  assert.equal(moveInGuide(many, { ch: 12, progId: 'x12', anchorMs: T0 }, 'pagedown', 6).ch, 14);
  assert.equal(moveInGuide(many, { ch: 3, progId: 'x3', anchorMs: T0 }, 'pageup', 6).ch, 0);
});

test('guide: jump to a channel and to a time', () => {
  const p = jumpGuideToChannel(grid, 2, T0 + 100 * M);
  assert.deepEqual([p.ch, p.progId], [2, 'c2']);
  const q = jumpGuideToTime(grid, { ch: 1, progId: 'b0', anchorMs: T0 - 30 * M }, T0 + 30 * M);
  assert.deepEqual([q.ch, q.progId, q.anchorMs], [1, 'b1', T0 + 30 * M]);
});

test('guide: returning to a live programme anchors on now, so ↓ lands on what is on now', () => {
  // now = 20:00 (T0 + 120 min). Row 0: film 19:00–21:00 then 21:00–22:00. Row 1: 19:00–19:30 (past), 19:30–20:30 (live).
  const g = {
    windowStart: T0 - 60 * M, now: T0 + 120 * M,
    channels: [[prog('f', 60, 180), prog('f2', 180, 240)], [prog('p0', 60, 90), prog('p1', 90, 150)]],
  };
  let p = { ch: 0, progId: 'f', anchorMs: T0 + 120 * M };
  p = moveInGuide(g, p, 'right');
  p = moveInGuide(g, p, 'left');
  assert.equal(p.anchorMs, T0 + 120 * M, 'anchor is now, not the film start');
  assert.equal(moveInGuide(g, p, 'down').progId, 'p1');
});
