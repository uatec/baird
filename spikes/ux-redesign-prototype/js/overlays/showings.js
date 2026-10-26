// All showings of a programme: catch-up, on now, and the next 7 days across every channel, with repeats marked.
// ◂ ▸ choose between "Watch / Go to it" and "Remind me" on each row.
import { h, hhmm, clamp } from '../ui.js';
import { icon } from '../icons.js';
import { showingsOf, channelById } from '../data.js';

const ROW = 96;
const VISIBLE = 7;

export function create(ctx, { title, from }) {
  const now = ctx.now();
  const rows = showingsOf(title, now).filter(s => s.when !== 'catchup' || s.start > now - 7 * 86_400_000);
  let i = Math.max(0, rows.findIndex(r => (from ? r.id === from : r.when !== 'catchup')));
  let col = 0;
  const track = h('div', { class: 'sh-track' });
  const panel = h('div', { class: 'showings-panel glass' },
    h('div', { class: 'actions-title', text: title }),
    h('div', { class: 'actions-sub', text: `${rows.length} showings across ${new Set(rows.map(r => r.channelId)).size} channels · catch-up and the next 7 days` }),
    h('div', { class: 'sh-window' }, track));
  const el = h('div', { class: 'overlay-actions' }, h('div', { class: 'scrim-dim enter-fade' }), panel);

  const actionsOf = r => r.when === 'later'
    ? [{ label: 'Go to it', run: () => ctx.openPage('guide', { channelId: r.channelId, atMs: r.start }) },
       { label: ctx.hasReminder(r.id) ? 'Cancel reminder' : 'Remind me', run: () => ctx.toggleReminder(r) }]
    : r.when === 'live' ? [{ label: 'Watch now', run: () => ctx.playLive(r.channelId) }]
      : [{ label: 'Watch from the start', run: () => ctx.playCatchup(r) }];

  function draw() {
    let lastDay = '';
    track.replaceChildren(...rows.map((r, j) => {
      const ch = channelById(r.channelId);
      const day = ctx.dayWord(r.start);
      const showDay = day !== lastDay; lastDay = day;
      const acts = actionsOf(r);
      return h('div', { class: `sh-row${j === i ? ' focused' : ''}${r.when === 'catchup' ? ' past' : ''}`, style: { top: `${j * ROW}px` } },
        h('div', { class: 'sh-when' }, h('div', { class: 'sh-day', text: showDay ? day[0].toUpperCase() + day.slice(1) : '' }), h('div', { class: 'sh-time', text: hhmm(r.start) })),
        h('div', { class: 'sh-what' },
          h('div', { class: 'sh-ch' }, h('b', { text: ch.number }), ` ${ch.name}`,
            r.when === 'live' ? h('span', { class: 'pill live', text: 'Live' }) : r.when === 'catchup' ? h('span', { class: 'pill', text: '↺ Catch-up' }) : null,
            r.repeat ? h('span', { class: 'pill quiet', text: 'Repeat' }) : h('span', { class: 'pill quiet new', text: 'New' }),
            ctx.hasReminder(r.id) ? h('span', { class: 'sh-bell' }, icon('bellOn', 22)) : null),
          h('div', { class: 'sh-sub', text: r.subtitle || r.synopsis })),
        h('div', { class: 'sh-acts' }, acts.map((a, k) => h('div', { class: `btn small${j === i && k === col ? ' focused' : ''}`, text: a.label }))));
    }));
    track.style.transform = `translateY(${-clamp(i - 3, 0, Math.max(0, rows.length - VISIBLE)) * ROW}px)`;
  }
  draw();
  return {
    el,
    onAction(a) {
      if (a === 'up' || a === 'down') { i = clamp(i + (a === 'up' ? -1 : 1), 0, rows.length - 1); col = 0; draw(); return true; }
      if (a === 'left' || a === 'right') { col = clamp(col + (a === 'left' ? -1 : 1), 0, actionsOf(rows[i]).length - 1); draw(); return true; }
      if (a === 'ok' && rows[i]) {
        const act = actionsOf(rows[i])[col];
        if (act.label.includes('emind')) { act.run(); draw(); return true; }
        ctx.nav.closeOverlay(); act.run(); return true;
      }
      return a === 'hold-ok';
    },
  };
}
