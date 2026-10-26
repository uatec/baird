// One search across on-demand titles, channels and guide listings.
import { catalogue, channels, scheduleFor, SERVICE_NAMES, bestSource } from './data.js';

const words = s => s.toLowerCase().replace(/[’']/g, '').split(/[^a-z0-9]+/).filter(Boolean);
const matches = (queryWords, text) => {
  const w = words(text);
  return queryWords.every(q => w.some(x => x.startsWith(q)));
};

export function searchAll(query, nowMs) {
  const q = words(query);
  if (!q.length) return { top: null, titles: [], channels: [], onTv: [] };

  const titles = catalogue.filter(t => matches(q, t.name)).map(t => ({
    type: 'title', ref: t, label: t.name,
    sublabel: t.availability === 'play' ? `${t.year} · ${SERVICE_NAMES[bestSource(t).service]}` : `${t.year} · ${t.availability === 'requested' ? 'Downloading' : 'Not in your library'}`,
    art: t.art.backdrop, badge: t.availability,
  }));

  const chans = channels.filter(c => matches(q, c.name) || String(c.number).startsWith(q.join(''))).map(c => ({
    type: 'channel', ref: c, label: c.name, sublabel: String(c.number), art: null, badge: 'live',
  }));

  const onTv = [];
  for (const c of channels) {
    for (const p of scheduleFor(c.id, nowMs)) {
      if (!matches(q, p.title)) continue;
      const past = p.end <= nowMs;
      if (past && !c.catchup) continue;
      onTv.push({
        type: 'listing', ref: p, label: p.title, sublabel: `${c.name}`, art: p.art.backdrop,
        badge: past ? 'catchup' : p.start <= nowMs ? 'live' : 'later', channel: c,
      });
    }
  }
  // One listing per channel: what's on now, else the latest catch-up, else the next showing.
  const rank = { live: 0, catchup: 1, later: 2 };
  const best = new Map();
  for (const l of onTv) {
    const b = best.get(l.channel.id);
    const better = !b || rank[l.badge] < rank[b.badge]
      || (l.badge === b.badge && (l.badge === 'catchup' ? l.ref.start > b.ref.start : l.ref.start < b.ref.start));
    if (better) best.set(l.channel.id, l);
  }
  onTv.length = 0;
  onTv.push(...[...best.values()].sort((a, b) => rank[a.badge] - rank[b.badge] || a.ref.start - b.ref.start));

  const top = titles[0] ?? chans[0] ?? onTv.find(l => l.badge === 'live') ?? onTv[0] ?? null;
  return { top, titles, channels: chans, onTv: onTv.slice(0, 16) };
}
