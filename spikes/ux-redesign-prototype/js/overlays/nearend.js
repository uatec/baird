// Live TV, last few minutes of a programme: what's next on this channel, without being asked.
import { h, hhmm, minsLeft } from '../ui.js';
import { channelById, programmeAt, titleById } from '../data.js';

export function create(ctx) {
  const P = ctx.player;
  const ch = channelById(P.current.channelId);
  const now = ctx.now();
  const p = P.programme();
  const next = programmeAt(ch.id, p.end);
  const after = programmeAt(ch.id, next.end);
  let focus = 0;
  const btns = h('div', { class: 'near-btns' });
  const el = h('div', { class: 'overlay-near' }, h('div', { class: 'near-card glass' },
    h('div', { class: 'near-eyebrow', text: `Up next on ${ch.name} · in ${minsLeft(p.end, now)} min` }),
    h('div', { class: 'near-title', text: `${hhmm(next.start)}  ${next.title}` }),
    h('div', { class: 'near-then' }, `then ${hhmm(after.start)} ${after.title}`,
      after.titleId && titleById(after.titleId)?.youWatch ? h('span', { class: 'pill mine', text: 'You watch' }) : null),
    btns));
  function draw() {
    btns.replaceChildren(h('div', { class: `btn small${focus === 0 ? ' focused' : ''}`, text: 'Stay' }), h('div', { class: `btn small${focus === 1 ? ' focused' : ''}`, text: 'Find something else' }));
  }
  draw();
  const auto = ctx.hold ? null : setTimeout(() => { if (ctx.nav.state().overlay?.name === 'nearend') ctx.nav.closeOverlay(); }, 15000);
  return {
    el,
    destroy() { clearTimeout(auto); },
    onAction(a) {
      if (a === 'left' || a === 'right') { focus = a === 'left' ? 0 : 1; draw(); return true; }
      if (a === 'ok') { if (focus === 0) ctx.nav.closeOverlay(); else ctx.nav.openOverlay({ name: 'channels', params: {} }); return true; }
      if (a === 'up' || a === 'down') { ctx.nav.openOverlay({ name: 'channels', params: { step: a } }); return true; }
      return false;
    },
  };
}
