// ▴ ▾ while watching live: the channel list. It opens on the channel you're watching, then visibly moves one step.
// Each row shows now and next on a to-scale timeline (so a 5-minute bulletin looks like one). Resting ~1 s tunes.
import { h, art, hhmm, clamp } from '../ui.js';
import { channels, programmeAt } from '../data.js';

const ROW = 118;
const REST_MS = 1000;
const IDLE_MS = 5000;
const SPAN = 3 * 3600_000; // the timeline covers the next 3 hours

export function create(ctx, params = {}) {
  const P = ctx.player;
  let i = Math.max(0, P.channelIndex());
  let tuned = i;
  let restTimer = null, idleTimer = null, stepTimer = null;
  const list = h('div', { class: 'ch-track' });
  const lit = h('div', { class: 'ch-lit' });
  const panel = h('div', { class: 'ch-panel glass' }, h('div', { class: 'ch-window' }, list));
  const el = h('div', { class: 'overlay-channels' }, panel);
  const rowEls = [];

  function timeline(c, now) {
    const segs = [];
    let at = now;
    for (let k = 0; k < 4 && at < now + SPAN; k++) {
      const p = programmeAt(c.id, at);
      if (!p) break;
      const a = Math.max(p.start, now), b = Math.min(p.end, now + SPAN);
      segs.push(h('div', { class: `seg${k === 0 ? ' now' : ''}${p.noInfo ? ' none' : ''}`, style: { width: `${((b - a) / SPAN) * 100}%` } },
        k > 0 ? h('span', { class: 'seg-label', text: `${hhmm(p.start)} ${p.title}` }) : null));
      at = p.end;
      if (p.noInfo) break;
    }
    return h('div', { class: 'ch-timeline' }, segs);
  }

  function build() {
    const now = ctx.now();
    list.replaceChildren(lit, ...channels.map((c, j) => {
      const p = programmeAt(c.id, now);
      const row = h('div', { class: 'ch-row', style: { top: `${j * ROW}px` } },
        h('div', { class: 'ch-thumb' }, art(p.art.backdrop, p.art.key, '', { title: p.noInfo ? null : p.title, channel: c })),
        h('div', { class: 'ch-text' },
          h('div', { class: 'ch-name' }, h('b', { text: c.number }), ` ${c.name}`, h('span', { class: 'ch-times', text: p.noInfo ? '' : `${hhmm(p.start)}–${hhmm(p.end)}` })),
          h('div', { class: 'ch-prog', text: p.noInfo ? 'No programme information' : p.title }),
          timeline(c, now)));
      rowEls[j] = row;
      return row;
    }));
  }

  function draw() {
    rowEls.forEach((r, j) => { r.classList.toggle('focused', j === i); r.classList.toggle('tuned', j === tuned); });
    lit.style.transform = `translateY(${i * ROW}px)`;
    list.style.transform = `translateY(${-clamp(i - 3, 0, channels.length - 7) * ROW}px)`;
  }

  function tune() {
    if (i === tuned) return;
    tuned = i;
    ctx.playLive(channels[i].id, null, { keepOverlay: true }); // the list stays up so flicking can continue
    draw();
  }
  const bump = () => {
    if (ctx.hold) return;
    clearTimeout(idleTimer);
    idleTimer = setTimeout(() => { if (ctx.nav.state().overlay?.name === 'channels') ctx.nav.closeOverlay(); }, IDLE_MS);
  };

  function move(d) {
    i = clamp(i + d, 0, channels.length - 1);
    draw();
    clearTimeout(restTimer);
    if (!ctx.hold) restTimer = setTimeout(tune, REST_MS);
    bump();
  }

  build();
  draw();
  bump();
  // Open on the current channel, then take the first step so the move is visible.
  if (params.step) stepTimer = setTimeout(() => move(params.step === 'up' ? -1 : 1), 260);
  return {
    el,
    destroy() { clearTimeout(restTimer); clearTimeout(idleTimer); clearTimeout(stepTimer); },
    onAction(a) {
      if (a === 'up' || a === 'down') { clearTimeout(stepTimer); move(a === 'up' ? -1 : 1); return true; }
      if (a === 'ok') { clearTimeout(restTimer); tune(); ctx.nav.closeOverlay(); return true; }
      return a === 'left' || a === 'right';
    },
  };
}
