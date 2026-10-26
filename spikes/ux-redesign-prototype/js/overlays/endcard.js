// End of an episode: "Up next" with a countdown. ◂ ▸ choose, OK acts, ⏭ plays now, Back dismisses.
import { h, art } from '../ui.js';
import { icon } from '../icons.js';
import { titleById } from '../data.js';

const COUNTDOWN = 8;

export function create(ctx, { titleId, season, episode }) {
  const t = titleById(titleId);
  const ep = t.seasons.find(s => s.n === season).episodes[episode - 1];
  let left = COUNTDOWN;
  let focus = 0;
  const ring = h('div', { class: 'ring big', style: { '--p': 0 } }, h('span', { text: String(left) }));
  const btns = h('div', { class: 'end-btns' });
  const card = h('div', { class: 'end-card glass' },
    h('div', { class: 'end-thumb' }, art(ep.still, t.id), h('div', { class: 'end-ring' }, ring)),
    h('div', { class: 'end-text' },
      h('div', { class: 'end-eyebrow', text: `Up next in ${t.name}` }),
      h('div', { class: 'end-title', text: ep.title }),
      h('div', { class: 'end-meta', text: `Season ${season}, episode ${episode} · ${ep.mins} min` }),
      btns));
  const el = h('div', { class: 'overlay-end' }, h('div', { class: 'end-scrim' }), card);

  const playNow = () => ctx.playVod(titleId, season, episode);
  const back = () => { ctx.nav.closeOverlay(); ctx.stopAndOpenShow(titleId); };
  function draw() {
    btns.replaceChildren(
      h('div', { class: `btn${focus === 0 ? ' focused' : ''}` }, icon('play', 26), 'Play now'),
      h('div', { class: `btn${focus === 1 ? ' focused' : ''}`, text: `Back to ${t.name}` }));
    ring.style.setProperty('--p', Math.round(((COUNTDOWN - left) / COUNTDOWN) * 100));
    ring.firstChild.textContent = String(left);
  }
  draw();
  const timer = ctx.hold ? null : setInterval(() => { left -= 1; if (left <= 0) { clearInterval(timer); playNow(); } else draw(); }, 1000);

  return {
    el,
    destroy() { clearInterval(timer); },
    onAction(a) {
      if (a === 'left' || a === 'right') { focus = a === 'left' ? 0 : 1; draw(); return true; }
      if (a === 'ok') { clearInterval(timer); (focus === 0 ? playNow : back)(); return true; }
      if (a === 'next') { clearInterval(timer); playNow(); return true; }
      return a === 'up' || a === 'down';
    },
  };
}
