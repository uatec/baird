// OK while watching: the info bar. Two rows: the scrubber (◂ ▸ move the playhead, holding speeds up) and the
// buttons (⏯ · Subtitles · Audio). ▾ goes from the scrubber to the buttons; ▴ back (and on live, ▴ from the
// scrubber opens the channel list).
import { h, hhmm, fill } from '../ui.js';
import { icon } from '../icons.js';
import { titleById, channelById, programmeAt, SERVICE_NAMES, bestSource } from '../data.js';
import { actionsFor } from './actions.js';

const fmt = ms => { const s = Math.max(0, Math.round(ms / 1000)); const hh = Math.floor(s / 3600), mm = Math.floor(s / 60) % 60, ss = s % 60; return `${hh ? hh + ':' : ''}${String(mm).padStart(hh ? 2 : 1, '0')}:${String(ss).padStart(2, '0')}`; };

export function create(ctx, params = {}) {
  const P = ctx.player;
  let row = 'scrub';
  let focus = 0;
  let idle = null;
  const el = h('div', { class: 'info-bar glass' });
  const wrap = h('div', { class: 'overlay-info' }, h('div', { class: 'info-scrim' }), el);

  const buttons = () => [
    { id: 'pp', icon: P.paused ? 'play' : null, label: P.paused ? 'Play' : 'Pause', run: () => P.togglePause() },
    { id: 'cc', icon: 'cc', label: P.subtitles ? 'Subtitles on' : 'Subtitles', run: () => P.toggleSubtitles() },
    { id: 'audio', icon: 'audio', label: 'Audio', run: () => ctx.toast('Audio: English (audio description off)') },
  ];

  function track(played, extra = []) {
    return h('div', { class: `info-bar-track${row === 'scrub' ? ' lit focused' : ''}` }, h('i', { class: 'played', style: { width: `${played * 100}%` } }), ...extra,
      h('span', { class: 'knob', style: { left: `${played * 100}%` } }));
  }

  function draw() {
    const cur = P.current;
    if (!cur) return;
    const now = ctx.now();
    const btns = buttons().map((b, i) => h('div', { class: `btn small${row === 'buttons' && i === focus ? ' focused' : ''}` }, b.icon ? icon(b.icon, 26) : h('span', { class: 'pause-glyph', text: '❚❚' }), b.label));
    if (cur.kind === 'live') {
      const ch = channelById(cur.channelId);
      const at = now - cur.behindMs;
      const p = programmeAt(ch.id, at);
      const next = programmeAt(ch.id, p.end);
      const span = p.end - p.start;
      const played = Math.max(0, (at - p.start) / span), live = Math.min(1, (now - p.start) / span);
      fill(el, 
        h('div', { class: 'info-top' },
          h('span', { class: 'info-num', text: ch.number }), h('span', { class: 'info-ch', text: ch.name }), h('span', { class: 'pill live', text: 'Live' }),
          h('span', { class: 'info-clock', text: hhmm(now) })),
        h('div', { class: 'info-title', text: p.noInfo ? ch.name : p.title }),
        p.noInfo ? h('div', { class: 'info-times', text: 'No information for this channel' }) : track(played, [h('i', { class: 'buffered', style: { left: `${played * 100}%`, width: `${Math.max(0, live - played) * 100}%` } }), h('span', { class: 'edge', style: { left: `${live * 100}%` } })]),
        p.noInfo ? null : h('div', { class: 'info-times' }, h('span', { text: hhmm(p.start) }), h('span', { class: 'behind', text: cur.behindMs > 30_000 ? `${hhmm(at)} · ${Math.round(cur.behindMs / 60_000)} min behind live` : 'Live' }), h('span', { text: hhmm(p.end) })),
        h('div', { class: 'info-bottom' }, h('div', { class: 'info-btns' }, btns), next && !p.noInfo ? h('div', { class: 'info-next' }, 'Next  ', h('b', { text: `${hhmm(next.start)} ${next.title}` })) : null));
    } else {
      const t = titleById(cur.titleId), ep = P.episodeOf(cur);
      fill(el, 
        h('div', { class: 'info-top' }, h('span', { class: 'info-ch', text: SERVICE_NAMES[bestSource(t)?.service] ?? '' }), h('span', { class: 'info-clock', text: hhmm(now) })),
        h('div', { class: 'info-title' }, t.name, cur.season ? h('span', { class: 'info-ep', text: `  S${cur.season} E${cur.episode}  ${ep.title}` }) : null),
        track(cur.posMs / cur.durMs),
        h('div', { class: 'info-times' }, h('span', { text: fmt(cur.posMs) }), h('span', { class: 'behind', text: `Ends at ${hhmm(now + cur.durMs - cur.posMs)}` }), h('span', { text: `−${fmt(cur.durMs - cur.posMs)}` })),
        h('div', { class: 'info-bottom' }, h('div', { class: 'info-btns' }, btns)));
    }
  }

  const bump = () => { if (ctx.hold) return; clearTimeout(idle); idle = setTimeout(() => { if (ctx.nav.state().overlay?.name === 'info') ctx.nav.closeOverlay(); }, 5000); };
  draw();
  bump();
  const off = setInterval(draw, 1000);

  return {
    el: wrap,
    update: draw,
    destroy() { clearInterval(off); clearTimeout(idle); },
    onAction(a, opts = {}) {
      bump();
      if (row === 'scrub' && (a === 'left' || a === 'right')) { P.seek((a === 'left' ? -1 : 1) * (opts.repeat ? 120_000 : 30_000)); draw(); return true; }
      if (row === 'buttons' && (a === 'left' || a === 'right')) { focus = Math.max(0, Math.min(2, focus + (a === 'left' ? -1 : 1))); draw(); return true; }
      if (a === 'down' && row === 'scrub') { row = 'buttons'; draw(); return true; }
      if (a === 'up' && row === 'buttons') { row = 'scrub'; draw(); return true; }
      if ((a === 'up' || a === 'down') && P.current?.kind === 'live') { ctx.nav.openOverlay({ name: 'channels', params: { step: a } }); return true; }
      if (a === 'ok') { if (row === 'buttons') buttons()[focus].run(); else P.togglePause(); draw(); return true; }
      if (a === 'hold-ok') { ctx.nav.openOverlay({ name: 'actions', params: { title: 'Now playing', items: actionsFor({ kind: 'playing' }, ctx) } }); return true; }
      if (a === 'playpause') { P.togglePause(); draw(); return true; }
      if (a === 'prev' || a === 'next') { P.seek(a === 'prev' ? -10_000 : 30_000); draw(); return true; }
      return a === 'up' || a === 'down';
    },
  };
}
