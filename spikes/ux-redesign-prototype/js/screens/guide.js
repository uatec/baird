// Live TV: the guide. Two layouts — channels down the side (time across), or channels across the top (time down).
// A highlight slides from cell to cell (so you can follow where focus went); the grid only scrolls when focus nears
// an edge; the header crossfades. The lit cell keeps its size.
import { h, art, hhmm, clamp } from '../ui.js';
import { icon } from '../icons.js';
import { channels, scheduleFor, titleById, channelById } from '../data.js';
import { moveInGuide, jumpGuideToChannel, jumpGuideToTime } from '../focus.js';
import { numberEntry } from '../keys.js';
import { actionsFor } from '../overlays/actions.js';

const MIN = 60_000;
const HOUR = 60 * MIN;

const GEOM = {
  // channels down, time across
  rows: { chStart: 548, chSize: 84, visibleCh: 6, timeStart: 376, timeExtent: 1544, px: 12, span: 120 },
  // channels across, time down
  columns: { chStart: 200, chSize: 344, visibleCh: 5, timeStart: 560, timeExtent: 520, px: 4.4, span: 118 },
};

export function create(ctx, params = {}) {
  const layout = ctx.state.settings.guideLayout === 'columns' ? 'columns' : 'rows';
  const G = GEOM[layout];
  const now0 = ctx.now();
  const origin = Math.floor((now0 - 3 * HOUR) / (30 * MIN)) * 30 * MIN;
  const grid = { windowStart: origin, now: now0, channels: channels.map(c => scheduleFor(c.id, now0).filter(p => p.end > origin)) };

  let pos = jumpGuideToChannel(grid, Math.max(0, channels.findIndex(c => c.id === params.channelId)), params.atMs ?? now0);
  let winStart = null;      // ms at the start of the visible time window
  let firstCh = 0;          // first visible channel
  let stripIndex = null;    // non-null when the day strip has focus
  let lit = true;           // false while the tab bar is lit
  const entry = numberEntry(channels);
  let entryTimer = null;

  // ---- DOM
  const el = h('div', { class: `page guide guide--${layout}` });
  const amb = h('div', { class: 'guide-ambient' });
  const info = h('div', { class: 'guide-info' });
  const preview = h('div', { class: 'guide-preview' });
  const strip = h('div', { class: 'guide-strip' });
  const entryPill = h('div', { class: 'guide-entry glass' });
  const ruler = h('div', { class: 'guide-ruler' });
  const rulerTrack = h('div', { class: 'guide-ruler-track' });
  ruler.append(rulerTrack);
  const chCol = h('div', { class: 'guide-channels' });
  const chTrack = h('div', { class: 'guide-ch-track' });
  chCol.append(chTrack);
  const body = h('div', { class: 'guide-body' });
  const track = h('div', { class: 'guide-track' });
  const hl = h('div', { class: 'guide-hl' });
  const nowLine = h('div', { class: 'guide-now' });
  track.append(hl);
  body.append(track);
  el.append(amb, h('div', { class: 'guide-scrim' }), info, preview, strip, entryPill, ruler, chCol, body, nowLine, h('div', { class: 'guide-fade' }));

  const t = ms => (ms - origin) / MIN * G.px;                       // time → px along the time axis
  const c = i => i * G.chSize;                                       // channel index → px along the channel axis
  const place = (elm, chIdx, a, b, inset = 8) => {
    const tp = t(a), tl = Math.max(4, (b - a) / MIN * G.px - inset);
    if (layout === 'rows') Object.assign(elm.style, { top: `${c(chIdx)}px`, left: `${tp}px`, width: `${tl}px`, height: `${G.chSize - 8}px` });
    else Object.assign(elm.style, { left: `${c(chIdx)}px`, top: `${tp}px`, height: `${tl}px`, width: `${G.chSize - 10}px` });
  };
  const cellEls = new Map();

  // Static content: channel names, every programme cell, the time ruler.
  channels.forEach((ch, i) => {
    const chEl = h('div', { class: 'guide-ch' }, h('span', { class: 'num', text: ch.number }), h('span', { class: 'name', text: ch.name }));
    if (layout === 'rows') chEl.style.top = `${c(i)}px`; else chEl.style.left = `${c(i)}px`;
    chTrack.append(chEl);
    for (const p of grid.channels[i]) {
      const tt = p.titleId ? titleById(p.titleId) : null;
      const cell = h('div', { class: `guide-cell${tt?.youWatch ? ' mine' : ''}${p.noInfo ? ' noinfo' : ''}` },
        h('div', { class: 'cell-title' }, h('span', { class: 'bell' }), p.end <= now0 && ch.catchup && !p.noInfo ? h('span', { class: 'cu', text: '↺' }) : null, p.title),
        p.noInfo ? null : h('div', { class: 'cell-time', text: hhmm(p.start) }));
      place(cell, i, p.noInfo ? Math.max(p.start, origin) : p.start, p.noInfo ? origin + 27 * HOUR : p.end);
      cell.dataset.end = p.end;
      cellEls.set(p.id, cell);
      track.append(cell);
    }
  });
  for (let m = origin; m < origin + 27 * HOUR; m += 30 * MIN) {
    const s = h('span', { text: hhmm(m) });
    if (layout === 'rows') s.style.left = `${t(m)}px`; else s.style.top = `${t(m)}px`;
    rulerTrack.append(s);
  }

  const days = (() => {
    const n = new Date(now0);
    const at = (d, hh) => new Date(n.getFullYear(), n.getMonth(), n.getDate() + d, hh).getTime();
    return [['Now', now0], ['This evening', at(n.getHours() >= 21 ? 1 : 0, 19)], ['Tomorrow morning', at(1, 8)], ['Tomorrow evening', at(1, 19)]]
      .filter(([, ms]) => ms < origin + 27 * HOUR);
  })();
  days.forEach(([label]) => strip.append(h('div', { class: 'tab', text: label })));
  strip.append(h('div', { class: 'tab layout-tab' }, icon('layout', 24), layout === 'rows' ? 'Channels across' : 'Channels down'));

  const prog = () => grid.channels[pos.ch].find(p => p.id === pos.progId);

  function refreshBells() {
    for (const [id, cell] of cellEls) {
      const on = ctx.hasReminder(id);
      cell.classList.toggle('reminded', on);
      const b = cell.querySelector('.bell');
      if (on && !b.firstChild) b.append(icon('bellOn', 20)); else if (!on && b.firstChild) b.replaceChildren();
    }
  }

  function draw() {
    const now = ctx.now();
    grid.now = now;
    const p = prog();
    // Time window: move only when focus nears an edge, keeping half an hour of context.
    const live = p.start <= now && now < p.end;
    const focusT = live ? Math.max(p.start, now - 30 * MIN) : Math.max(p.start, origin);
    if (winStart === null || focusT < winStart + 10 * MIN || focusT > winStart + (G.span - 40) * MIN) {
      winStart = Math.max(origin, Math.floor((focusT - 30 * MIN) / (30 * MIN)) * 30 * MIN);
    }
    // Channel window: move only when focus is within one channel of the edge.
    if (pos.ch < firstCh + 1) firstCh = Math.max(0, pos.ch - 1);
    if (pos.ch > firstCh + G.visibleCh - 2) firstCh = Math.min(channels.length - G.visibleCh, pos.ch - (G.visibleCh - 2));
    firstCh = clamp(firstCh, 0, channels.length - G.visibleCh);

    const tOff = -t(winStart), cOff = -c(firstCh);
    if (layout === 'rows') {
      track.style.transform = `translate(${tOff}px, ${cOff}px)`;
      rulerTrack.style.transform = `translateX(${tOff}px)`;
      chTrack.style.transform = `translateY(${cOff}px)`;
      nowLine.style.transform = `translateX(${G.timeStart + t(now) + tOff}px)`;
    } else {
      track.style.transform = `translate(${cOff}px, ${tOff}px)`;
      rulerTrack.style.transform = `translateY(${tOff}px)`;
      chTrack.style.transform = `translateX(${cOff}px)`;
      nowLine.style.transform = `translateY(${G.timeStart + t(now) + tOff}px)`;
    }
    const nowVisible = now > winStart && now < winStart + G.span * MIN;
    nowLine.style.opacity = nowVisible ? 1 : 0;

    // The highlight slides to the lit programme (clipped to the window so long programmes still read).
    const a = Math.max(p.start, winStart), b = p.noInfo ? winStart + G.span * MIN : Math.min(p.end, winStart + G.span * MIN);
    place(hl, pos.ch, a, Math.max(b, a + 15 * MIN));
    hl.classList.toggle('off', stripIndex !== null || !lit);
    for (const [id, cell] of cellEls) cell.classList.toggle('focused', stripIndex === null && lit && id === p.id);
    for (const cell of track.children) if (cell.dataset.end) cell.classList.toggle('past', +cell.dataset.end <= now);
    // Programmes that began before the window keep their title in view.
    for (let i = firstCh; i < Math.min(channels.length, firstCh + G.visibleCh + 1); i++) {
      for (const prg of grid.channels[i]) {
        const inset = prg.start < winStart && prg.end > winStart ? (winStart - prg.start) / MIN * G.px : 0;
        const cell = cellEls.get(prg.id);
        if (layout === 'rows') cell.style.paddingLeft = `${18 + inset}px`; else cell.style.paddingTop = `${10 + inset}px`;
      }
    }
    [...chTrack.children].forEach((e, i) => e.classList.toggle('sel', i === pos.ch));
    [...strip.children].forEach((s, i) => s.classList.toggle('focused', i === stripIndex));
    renderInfo(p, now);
  }

  let lastInfo = null;
  function renderInfo(p, now) {
    if (lastInfo === p.id) return;
    lastInfo = p.id;
    const ch = channelById(p.channelId);
    const tt = p.titleId ? titleById(p.titleId) : null;
    const live = p.start <= now && now < p.end, past = p.end <= now;
    const layer = h('div', { class: 'gi-layer in' }, ...[
      h('div', { class: 'eyebrow' }, h('b', { text: `${ch.number}  ${ch.name}` }), p.noInfo ? null : h('span', { text: `${ctx.dayWord(p.start)[0].toUpperCase()}${ctx.dayWord(p.start).slice(1)} ${hhmm(p.start)}–${hhmm(p.end)}` }),
        live ? h('span', { class: 'pill live', text: 'Live' }) : null,
        past && ch.catchup && !p.noInfo ? h('span', { class: 'pill', text: '↺ On catch-up' }) : null,
        tt?.youWatch ? h('span', { class: 'pill mine', text: 'You watch' }) : null,
        ctx.hasReminder(p.id) ? h('span', { class: 'pill' }, icon('bellOn', 18), ' Reminder set') : null),
      h('h1', { class: 'title', text: p.noInfo ? ch.name : p.title }),
      p.subtitle ? h('div', { class: 'sub', text: p.subtitle }) : null,
      h('p', { class: 'syn', text: p.noInfo ? 'No information for this channel.' : p.synopsis }),
      h('div', { class: 'keys' }, ...keyHints(p, now, live, past, ch)),
    ].filter(Boolean));
    [...info.children].forEach(o => { o.classList.remove('in'); o.classList.add('out'); setTimeout(() => o.remove(), 320); });
    info.append(layer);
    requestAnimationFrame(() => requestAnimationFrame(() => layer.classList.remove('in')));
    // ambient light and the preview crossfade too
    const ambEl = p.art.ambient ? h('img', { src: p.art.ambient, alt: '' }) : h('div', { class: 'amb-colour', style: { background: `radial-gradient(70% 80% at 70% 30%, ${ch.colour}, transparent 70%)` } });
    amb.append(ambEl);
    requestAnimationFrame(() => requestAnimationFrame(() => { ambEl.classList.add('on'); [...amb.children].slice(0, -1).forEach(o => { o.classList.remove('on'); setTimeout(() => o.remove(), 700); }); }));
    const cur = ctx.player.current;
    const pv = cur?.kind === 'live' ? ctx.player.programme() : cur ? null : grid.channels[pos.ch].find(x => x.start <= now && now < x.end);
    const pvCh = cur?.kind === 'live' ? channelById(cur.channelId) : ch;
    const src = cur && cur.kind === 'vod' ? ctx.player.episodeOf(cur).still : pv?.art.backdrop;
    const pvImg = art(src, 'pv', 'kb', { title: pv?.noInfo ? null : pv?.title, channel: pvCh });
    preview.append(h('div', { class: 'pv-layer' }, pvImg, h('div', { class: 'pv-label' },
      cur ? h('span', { class: 'pill live', text: cur.kind === 'live' ? 'Live' : 'Playing' }) : h('span', { class: 'pill', text: 'Preview' }),
      h('span', { text: cur?.kind === 'live' ? pvCh.name : cur ? titleById(cur.titleId).name : ch.name }))));
    while (preview.children.length > 2) preview.firstChild.remove();
    requestAnimationFrame(() => requestAnimationFrame(() => preview.lastChild?.classList.add('on')));
  }

  function keyHints(p, now, live, past, ch) {
    const k = (key, label) => h('span', {}, h('kbd', { text: key }), label);
    const ok = live || p.noInfo ? 'Watch' : past && ch.catchup ? 'Watch from the start' : past ? '' : ctx.hasReminder(p.id) ? 'Cancel reminder' : 'Remind me';
    const page = layout === 'rows' ? k('Pg', 'Page channels') : k('Pg', 'An hour');
    return [ok ? k('OK', ok) : null, k('Hold OK', 'More'), page, k('0–9', 'Channel')].filter(Boolean);
  }

  function setPos(next) { pos = next; draw(); }

  function digit(d) {
    const r = entry.push(d);
    entryPill.textContent = `${r.text}${'_'.repeat(Math.max(0, 3 - r.text.length))}${r.error ? `   ${r.error}` : r.match ? `   ${r.match.name}` : ''}`;
    entryPill.classList.add('on');
    clearTimeout(entryTimer);
    const commit = () => {
      const s = entry.state();
      if (s.match) setPos(jumpGuideToChannel(grid, channels.indexOf(s.match), pos.anchorMs));
      entry.clear();
      entryPill.classList.remove('on');
    };
    if (r.match && !channels.some(x => String(x.number).startsWith(r.text) && String(x.number) !== r.text)) entryTimer = setTimeout(commit, 400);
    else entryTimer = setTimeout(commit, r.error ? 1400 : 1500);
  }

  function openActions(p) {
    const items = actionsFor({ kind: 'programme', programme: p, guide: api }, ctx);
    ctx.nav.openOverlay({ name: 'actions', params: { title: p.noInfo ? channelById(p.channelId).name : p.title, subtitle: p.noInfo ? 'No information' : `${channelById(p.channelId).name}, ${hhmm(p.start)}–${hhmm(p.end)}`, items, anchor: rectOf(hl) } });
  }
  const rectOf = elm => { const s = document.getElementById('stage').getBoundingClientRect(), r = elm.getBoundingClientRect(), k = s.width / 1920; return { left: (r.left - s.left) / k, top: (r.top - s.top) / k, width: r.width / k, height: r.height / k }; };

  // Map the remote onto the layout: in columns, ◂ ▸ change channel and ▴ ▾ move through time.
  const mapDir = a => (layout === 'rows' ? a : { up: 'left', down: 'right', left: 'up', right: 'down' }[a] ?? a);

  const api = {
    el,
    refresh() { lastInfo = null; refreshBells(); draw(); },
    focus({ channelId, atMs } = {}) {
      const i = channels.findIndex(x => x.id === channelId);
      if (i >= 0 || atMs) setPos(jumpGuideToTime(grid, { ch: i >= 0 ? i : pos.ch, progId: pos.progId, anchorMs: pos.anchorMs }, atMs ?? ctx.now()));
    },
    jumpTo(p) {
      if (!p) return;
      const ch = channels.findIndex(x => x.id === p.channelId);
      if (!grid.channels[ch].some(x => x.id === p.id)) return;
      setPos({ ch, progId: p.id, anchorMs: p.start });
    },
    onShow() { lastInfo = null; refreshBells(); draw(); },
    onFocusOut() { lit = false; draw(); },
    onFocusIn() { lit = true; draw(); },
    onAction(a) {
      lit = true;
      const now = ctx.now();
      if (stripIndex !== null) {
        if (a === 'left') stripIndex = Math.max(0, stripIndex - 1);
        else if (a === 'right') stripIndex = Math.min(strip.children.length - 1, stripIndex + 1);
        else if (a === 'down') stripIndex = null;
        else if (a === 'up') { stripIndex = null; draw(); return false; } // the shell lights the tab bar
        else if (a === 'ok') {
          if (stripIndex === strip.children.length - 1) {
            ctx.state.settings.guideLayout = layout === 'rows' ? 'columns' : 'rows';
            ctx.toast(layout === 'rows' ? 'Guide: channels across the top, time down' : 'Guide: channels down the side, time across');
            ctx.resetPlace('guide');
            if (params.place) ctx.goPlace('guide', { channelId: channels[pos.ch].id, atMs: pos.anchorMs });
            else { ctx.nav.back(); ctx.openPage('guide', { channelId: channels[pos.ch].id, atMs: pos.anchorMs }); }
            return true;
          }
          const ms = days[stripIndex][1]; stripIndex = null; setPos(jumpGuideToTime(grid, pos, ms)); return true;
        } else return false;
        draw();
        return true;
      }
      const atTop = layout === 'rows' ? pos.ch === 0 : grid.channels[pos.ch][0].id === pos.progId;
      if (a === 'up' && atTop) { stripIndex = 0; draw(); return true; }
      if (['up', 'down', 'left', 'right'].includes(a)) { setPos(moveInGuide(grid, pos, mapDir(a), G.visibleCh)); return true; }
      if (a === 'pageup' || a === 'pagedown') {
        if (layout === 'rows') setPos(moveInGuide(grid, pos, a, G.visibleCh));
        else setPos(jumpGuideToTime(grid, pos, Math.max(origin, pos.anchorMs + (a === 'pageup' ? -HOUR : HOUR))));
        return true;
      }
      if (a === 'guide') { stripIndex = 0; draw(); return true; }
      if (a.startsWith('digit:')) { digit(+a.slice(6)); return true; }
      if (a === 'del') { const r = entry.del(); entryPill.textContent = r.text; if (!r.text) entryPill.classList.remove('on'); return true; }
      const p = prog();
      if (a === 'ok' || a === 'playpause') {
        if (p.noInfo || (p.start <= now && now < p.end)) ctx.playLive(p.channelId);
        else if (p.end <= now && channelById(p.channelId).catchup) ctx.playCatchup(p);
        else if (p.start > now) { ctx.toggleReminder(p); refreshBells(); lastInfo = null; draw(); }
        return true;
      }
      if (a === 'hold-ok') { openActions(p); return true; }
      return false;
    },
  };

  refreshBells();
  draw();
  const timer = setInterval(() => { refreshBells(); draw(); }, 30_000);
  api.destroy = () => clearInterval(timer);
  return api;
}
