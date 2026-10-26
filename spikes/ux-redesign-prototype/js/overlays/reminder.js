// A reminder, a minute before: what, where, a countdown, and the choice. It switches at the start unless dismissed.
import { h, art, hhmm } from '../ui.js';
import { channelById } from '../data.js';

export function create(ctx, { programme: p }) {
  const ch = channelById(p.channelId);
  let focus = 0;
  const count = h('span', { class: 'rem-count' });
  const btns = h('div', { class: 'near-btns' });
  const el = h('div', { class: 'overlay-near' }, h('div', { class: 'reminder-card glass' },
    h('div', { class: 'rem-thumb' }, art(p.art.backdrop, p.art.key, '', { title: p.title, channel: ch })),
    h('div', { class: 'rem-text' },
      h('div', { class: 'near-eyebrow' }, 'Reminder · starts in ', count),
      h('div', { class: 'near-title', text: p.title }),
      h('div', { class: 'near-then', text: `${hhmm(p.start)} on ${ch.number} ${ch.name}. We'll switch over when it starts.` }),
      btns)));
  const draw = () => {
    const s = Math.max(0, Math.round((p.start - ctx.now()) / 1000));
    count.textContent = `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
    btns.replaceChildren(h('div', { class: `btn small${focus === 0 ? ' focused' : ''}`, text: 'Switch now' }), h('div', { class: `btn small${focus === 1 ? ' focused' : ''}`, text: 'Dismiss' }));
  };
  const switchNow = () => { ctx.state.reminders.delete(p.id); ctx.nav.closeOverlay(); ctx.playLive(p.channelId); };
  draw();
  const t = setInterval(() => { draw(); if (!ctx.hold && ctx.now() >= p.start) { clearInterval(t); switchNow(); } }, 500);
  return {
    el,
    destroy() { clearInterval(t); },
    onAction(a) {
      if (a === 'left' || a === 'right') { focus = a === 'left' ? 0 : 1; draw(); return true; }
      if (a === 'ok') { if (focus === 0) switchNow(); else { ctx.state.reminders.delete(p.id); ctx.nav.closeOverlay(); ctx.toast(`Dismissed: you'll stay where you are`); } return true; }
      if (a === 'back') { ctx.state.reminders.delete(p.id); ctx.nav.closeOverlay(); ctx.toast(`Dismissed: you'll stay where you are`); return true; }
      return a === 'up' || a === 'down';
    },
  };
}
