// Digits while watching: "1 0 4" shows Channel 4, then tunes. DEL corrects. Unknown numbers say so and don't tune.
import { h, art } from '../ui.js';
import { channels, programmeAt } from '../data.js';
import { numberEntry } from '../keys.js';

export function create(ctx, { first }) {
  const entry = numberEntry(channels);
  let timer = null;
  const box = h('div', { class: 'num-box glass' });
  const el = h('div', { class: 'overlay-number' }, box);

  function draw(r) {
    const digits = (r.text + '___').slice(0, Math.max(3, r.text.length)).split('').map(d => h('span', { class: d === '_' ? 'slot' : 'digit', text: d === '_' ? '' : d }));
    const p = r.match ? programmeAt(r.match.id, ctx.now()) : null;
    box.replaceChildren(...[h('div', { class: 'num-digits' }, digits),
      r.match ? h('div', { class: 'num-match' }, h('div', { class: 'num-thumb' }, art(p.art.backdrop, p.art.key)), h('div', {}, h('b', { text: r.match.name }), h('div', { class: 'num-prog', text: p.title })))
        : r.error ? h('div', { class: 'num-error', text: r.error }) : null].filter(Boolean));
    clearTimeout(timer);
    const exact = r.match && !channels.some(c => String(c.number) !== r.text && String(c.number).startsWith(r.text));
    if (ctx.hold) return;
    timer = setTimeout(() => {
      ctx.nav.closeOverlay();
      if (r.match) ctx.playLive(r.match.id);
    }, r.error ? 1500 : exact ? 700 : 1600);
  }
  draw(entry.push(first));
  return {
    el,
    destroy() { clearTimeout(timer); },
    onAction(a) {
      if (a.startsWith('digit:')) { draw(entry.push(+a.slice(6))); return true; }
      if (a === 'del') { const r = entry.del(); if (!r.text) ctx.nav.closeOverlay(); else draw(r); return true; }
      if (a === 'ok') { const s = entry.state(); ctx.nav.closeOverlay(); if (s.match) ctx.playLive(s.match.id); return true; }
      return a === 'up' || a === 'down' || a === 'left' || a === 'right';
    },
  };
}
