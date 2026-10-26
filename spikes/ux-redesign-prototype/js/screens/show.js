// One page per show or film, wherever it lives. Says where each season streams, and lets you request the rest.
// Season tabs behave like tabs: moving across them switches the episodes straight away.
import { h, art, cardEl, hhmm, keepInView } from '../ui.js';
import { icon } from '../icons.js';
import { moveInRows } from '../focus.js';
import { titleById, bestSource, SERVICE_NAMES, channelById, seasonState, seasonSource, availabilitySummary, showingsOf, episodeState, missingSeasons } from '../data.js';
import { actionsFor, openRequestSheet } from '../overlays/actions.js';

const STEP = 384 + 36;

export function create(ctx, { titleId }) {
  const t = titleById(titleId);
  const el = h('div', { class: 'page show' });
  const back = h('div', { class: 'show-art' }, art(t.art.backdrop, t.id, 'kb', { title: t.name }));
  const body = h('div', { class: 'show-body' });
  const lower = h('div', { class: 'show-lower' });
  const scroll = h('div', { class: 'show-scroll' }, body, lower);
  el.append(back, h('div', { class: 'show-scrim' }), scroll);
  ctx.setAmbient(t.art.ambient);

  let pos = { row: 0, col: 0 };
  const memory = new Map();
  let season = t.progress?.season ?? t.seasons?.find(s => seasonState(t, s.n) === 'available')?.n ?? t.seasons?.[0].n ?? null;
  let rows = [];
  let btnEls = [], seasonEls = [], epEls = [], epTrack = null, epRow = null;

  const missing = () => missingSeasons(t);

  function buttons() {
    const b = [];
    if (t.availability === 'play' || t.availability === 'partial') {
      if (t.seasons) {
        const p = t.progress ?? { season: t.seasons.find(s => seasonState(t, s.n) === 'available').n, episode: 1 };
        b.push({ id: 'play', primary: true, icon: 'play', label: t.progress ? `Resume S${p.season} E${p.episode}` : `Play S${p.season} E${p.episode}`, run: () => ctx.playVod(t.id, p.season, p.episode, (p.positionMins ?? 0) * 60_000, btnEls[0]) });
      } else {
        b.push({ id: 'play', primary: true, icon: 'play', label: t.progress?.positionMins ? 'Resume' : 'Play', run: () => ctx.playTitle(t.id, btnEls[0]) });
      }
    }
    if (!t.seasons && t.availability === 'get') b.push({ id: 'get', primary: true, icon: 'get', label: 'Request…', run: () => openRequestSheet(t, ctx) });
    if (!t.seasons && t.availability === 'requested') b.push({ id: 'req', primary: true, ring: t.requestProgress, label: `Requested · ${Math.round(t.requestProgress * 100)}%`, run: () => ctx.toast(`${t.name} will be ready in about ${Math.max(2, Math.round((1 - t.requestProgress) * 40))} minutes`) });
    const miss = missing();
    // One way in to requesting, however many seasons are missing: the sheet offers the choices.
    if (t.seasons && miss.length) b.push({ id: 'get', primary: b.length === 0, icon: 'get', label: 'Request…', run: () => openRequestSheet(t, ctx, { season: miss.includes(season) ? season : null }) });
    b.push({ id: 'wl', icon: ctx.inWatchlist(t.id) ? 'check' : 'plus', label: ctx.inWatchlist(t.id) ? 'In watchlist' : 'Watchlist', run: () => ctx.toggleWatchlist(t.id) });
    const air = t.airsAs ? showingsOf(t.airsAs, ctx.now()).find(s => s.when !== 'catchup') : null;
    if (air) b.push({ id: 'tv', icon: 'grid', label: `On TV ${ctx.dayWord(air.start)} ${hhmm(air.start)}`, run: () => ctx.nav.openOverlay({ name: 'showings', params: { title: t.airsAs } }) });
    return b;
  }

  function episodes() {
    const s = t.seasons.find(x => x.n === season);
    return s.episodes.map(ep => {
      const st = episodeState(t, season, ep.n);
      const watched = t.progress && (season < t.progress.season || (season === t.progress.season && ep.n < t.progress.episode));
      const isNext = t.progress && season === t.progress.season && ep.n === t.progress.episode;
      const dl = st === 'requested' ? (t.requests[`${season}.${ep.n}`] ?? t.requests[season]) : null;
      return { type: 'episode', titleId: t.id, season, episode: ep.n, download: dl, label: `${ep.n}. ${ep.title}`, sublabel: `${ep.mins} min${watched ? '  ·  Watched' : isNext ? '  ·  Up next' : ''}`, art: ep.still, progress: watched ? 1 : null, watched, unavailable: st !== 'available', epState: st };
    });
  }

  // A season's state, never a call to action (requesting is done from the Request… button or an episode).
  function seasonLabel(n) {
    const st = seasonState(t, n);
    if (st === 'available') return SERVICE_NAMES[seasonSource(t, n).service];
    if (st === 'requested') return `Downloading ${Math.round(t.requests[n] * 100)}%`;
    const total = t.seasons.find(x => x.n === n).episodes.length;
    const some = [...(t.episodeSources ?? [])].filter(k => +k.split('.')[0] === n).length;
    return some ? `${some} of ${total} on Jellyfin` : 'Not streaming';
  }

  function computeRows() {
    rows = [{ kind: 'buttons', items: buttons() }];
    if (t.seasons) {
      rows.push({ kind: 'seasons', items: t.seasons.map(x => x.n) });
      rows.push({ kind: 'episodes', items: episodes() });
    }
    pos.row = Math.min(pos.row, rows.length - 1);
    pos.col = Math.min(pos.col, rows[pos.row].items.length - 1);
  }

  // The page is built once (and again only when its content changes); moving focus just toggles classes.
  function renderBody() {
    btnEls = rows[0].items.map(b => h('div', { class: `btn${b.primary ? ' primary' : ''}` },
      b.ring != null ? h('span', { class: 'ring tiny', style: { '--p': Math.round(b.ring * 100) } }) : b.icon ? icon(b.icon, 28) : null, b.label));
    body.replaceChildren(...[
      t.art.logo ? h('img', { class: 'show-logo', src: t.art.logo, alt: t.name }) : h('h1', { class: 'show-title', text: t.name }),
      h('div', { class: 'meta' }, h('b', { text: String(t.year) }), h('span', { text: t.kind === 'film' ? `Film${t.mins ? `, ${Math.floor(t.mins / 60)} h ${t.mins % 60} min` : ''}` : `${t.seasons.length} season${t.seasons.length === 1 ? '' : 's'}` }),
        h('span', { class: 'pill', text: t.rating })),
      h('div', { class: 'show-where' }, icon('series', 26), h('span', { text: availabilitySummary(t) })),
      h('p', { class: 'show-syn', text: t.synopsis }),
      h('div', { class: 'show-btns' }, btnEls),
    ]);
  }

  function renderLower() {
    if (!t.seasons) { lower.replaceChildren(); epEls = []; seasonEls = []; epTrack = null; return; }
    seasonEls = rows[1].items.map(n => h('div', { class: `tab season-tab ${seasonState(t, n)}${n === season ? ' current' : ''}` },
      h('span', { class: 'st-name', text: `Season ${n}` }), h('span', { class: 'st-src', text: seasonLabel(n) })));
    epEls = rows[2].items.map(c => {
      const e = cardEl(c, 'wide');
      if (c.watched) e.querySelector('.card-art').append(h('span', { class: 'card-corner tick' }, icon('check', 22)));
      if (c.unavailable) {
        e.classList.add('unavailable');
        const a = e.querySelector('.card-art');
        if (c.epState === 'requested') {
          // Same as films: a ring with the percentage, updated in place as it downloads.
          a.append(h('span', { class: 'card-badge pill', text: 'Downloading' }),
            h('div', { class: 'card-corner ring', 'data-dl': `${c.season}.${c.episode}`, style: { '--p': Math.round(c.download * 100) } }, h('span', { text: `${Math.round(c.download * 100)}%` })));
        } else a.append(h('span', { class: 'card-badge pill', text: 'Not streaming' }));
      }
      return e;
    });
    epTrack = h('div', { class: 'row-track' }, epEls);
    epRow = h('div', { class: 'row show-eps' }, epTrack);
    lower.replaceChildren(h('div', { class: 'show-seasons' }, seasonEls), epRow);
  }

  function rebuild() { computeRows(); renderBody(); renderLower(); draw(); }

  function draw() {
    btnEls.forEach((e, i) => e.classList.toggle('focused', pos.row === 0 && pos.col === i));
    seasonEls.forEach((e, i) => e.classList.toggle('focused', pos.row === 1 && pos.col === i));
    epEls.forEach((e, i) => e.classList.toggle('focused', pos.row === 2 && pos.col === i));
    if (epTrack) {
      const col = pos.row === 2 ? pos.col : (memory.get(2) ?? Math.max(0, (t.progress?.season === season ? t.progress.episode : 1) - 1));
      epTrack.style.transform = `translateX(${-Math.max(0, col - 1) * STEP}px)`;
      epRow.classList.toggle('active', pos.row === 2);
    }
    // Whatever is lit is always fully on screen.
    const lit = pos.row === 0 ? btnEls[pos.col] : pos.row === 1 ? seasonEls[pos.col] : epEls[pos.col];
    keepInView(scroll, lit, { lift: pos.row === 2 ? 0.07 : 0.04 });
  }

  if (t.seasons && t.progress) memory.set(2, t.progress.episode - 1);
  if (t.seasons) memory.set(1, t.seasons.findIndex(s => s.n === season));
  rebuild();
  const sig = () => `${t.availability}|${Object.keys(t.requests).sort().join(',')}|${[...(t.episodeSources ?? [])].join(',')}|${ctx.state.watchlist.length}|${t.progress?.season}.${t.progress?.episode}`;
  function updateProgress() {
    for (const ring of el.querySelectorAll('.ring[data-dl]')) {
      const [sn, en] = ring.dataset.dl.split('.');
      const v = t.requests[`${sn}.${en}`] ?? t.requests[sn];
      if (v == null) continue;
      ring.style.setProperty('--p', Math.round(v * 100));
      ring.firstChild.textContent = `${Math.round(v * 100)}%`;
    }
    seasonEls.forEach((e, i) => { const src = e.querySelector('.st-src'); const txt = seasonLabel(rows[1].items[i]); if (src && src.textContent !== txt) src.textContent = txt; });
    const req = btnEls.find(b => b.textContent.startsWith('Requested'));
    if (req && t.requestProgress != null) { req.lastChild.textContent = `Requested · ${Math.round(t.requestProgress * 100)}%`; req.querySelector('.ring')?.style.setProperty('--p', Math.round(t.requestProgress * 100)); }
  }
  let lastSig = sig();
  // A request progressing or finishing while the page is open updates it in place.
  const watch = setInterval(() => { if (sig() !== lastSig) { lastSig = sig(); rebuild(); } else updateProgress(); }, 1000);

  return {
    el,
    onShow() { ctx.setAmbient(t.art.ambient); lastSig = sig(); rebuild(); },
    destroy() { clearInterval(watch); },
    onAction(a) {
      if (['up', 'down', 'left', 'right'].includes(a)) {
        const before = pos.row;
        pos = moveInRows(rows.map(r => r.items.length), pos, a, memory);
        // Season tabs are tabs: lighting one shows its episodes.
        if (pos.row === 1 && (a === 'left' || a === 'right' || before !== 1)) {
          const n = rows[1].items[pos.col];
          if (n !== season) { season = n; memory.delete(2); rebuild(); return true; }
        }
        draw();
        return true;
      }
      const r = rows[pos.row];
      if (a === 'ok' || a === 'playpause') {
        if (r.kind === 'buttons') { r.items[pos.col].run(); lastSig = sig(); rebuild(); }
        else if (r.kind === 'seasons') { pos = { row: 2, col: memory.get(2) ?? 0 }; draw(); }
        else {
          const c = r.items[pos.col];
          if (c.epState === 'requested') ctx.toast(`S${c.season} E${c.episode} is downloading. It appears in Up next when it's ready.`);
          else if (c.epState === 'missing') openRequestSheet(t, ctx, { season: c.season, episode: c.episode });
          else ctx.playVod(t.id, c.season, c.episode, 0, epEls[pos.col]);
        }
        return true;
      }
      if (a === 'hold-ok') {
        if (r.kind === 'episodes') ctx.openActions(r.items[pos.col], epEls[pos.col]);
        else if (r.kind === 'seasons') {
          const n = r.items[pos.col];
          ctx.nav.openOverlay({ name: 'actions', params: { title: `${t.name}, season ${n}`, subtitle: seasonLabel(n), items: actionsFor({ kind: 'card', card: { type: 'title', titleId: t.id, season: n } }, ctx), anchor: null } });
        } else ctx.openActions({ type: 'title', titleId: t.id, label: t.name, sublabel: '' }, btnEls[pos.col]);
        return true;
      }
      return false;
    },
  };
}
