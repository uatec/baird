// Home: a hero that describes the lit card (no buttons of its own), then rows.
import { h, art, cardEl, hhmm, minsLeft } from '../ui.js';
import { moveInRows } from '../focus.js';
import { upNext, onNow, watchlistCards, discover, titleById, SERVICE_NAMES, bestSource, availabilitySummary } from '../data.js';

const ROW_TOP = 590;
const ROW_H = { wide: 412, poster: 520 };
const STEP = { wide: 384 + 36, poster: 216 + 36 };

export function createHome(ctx) {
  const el = h('div', { class: 'home layer' });
  const heroArt = h('div', { class: 'hero-art' });
  const hero = h('div', { class: 'hero' });
  const rowsEl = h('div', { class: 'rows' });
  el.append(heroArt, h('div', { class: 'hero-scrim' }), hero, rowsEl);

  let rows = [];
  let pos = { row: 0, col: 0 };
  let navIndex = null; // -1 while the tab bar (in the shell) is lit
  const memory = new Map();
  let lastHeroKey = null;
  let showPlaying = false; // hero shows the playing video until focus moves
  let lastSig = '', prevRows = [];
  function updateCard(el, c) {
    if (!el) return;
    const bar = el.querySelector('.card-progress > i');
    if (bar && c.progress != null) bar.style.width = `${Math.round(c.progress * 100)}%`;
    const ring = el.querySelector('.ring');
    if (ring && c.badge === 'requested') { ring.style.setProperty('--p', Math.round(c.progress * 100)); ring.firstChild.textContent = `${Math.round(c.progress * 100)}%`; }
    const sub = el.querySelector('.card-sub');
    const text = c.type === 'channel' ? c.timeText ?? '' : c.sublabel;
    if (sub && sub.textContent !== text) sub.textContent = text;
  }

  function build() {
    const now = ctx.now();
    rows = [
      { title: 'Up next', shape: 'wide', cards: upNext(now), eyebrow: 'Up next' },
      { title: 'On now', shape: 'wide', cards: onNow(now).map(c => ({ ...c, timeText: c.programme.noInfo ? c.label : `${c.label}  ·  ${minsLeft(c.programme.end, now)} min left` })), eyebrow: 'On now' },
      { title: 'Your watchlist', shape: 'poster', cards: watchlistCards(ctx.state.watchlist), eyebrow: 'In your watchlist' },
      { title: 'Discover', shape: 'poster', cards: discover(), eyebrow: 'Discover' },
    ];
    if (ctx.extraUpNext) rows[0].cards = ctx.extraUpNext(rows[0].cards);
    // Same cards as last time? Update their text and progress in place instead of rebuilding (keeps animations, cheap on the Pi).
    const sig = rows.map(r => r.cards.map(c => `${c.type}:${c.titleId ?? c.channelId}:${c.season ?? ''}:${c.episode ?? ''}:${c.badge ?? ''}:${c.programme?.id ?? ''}`).join(',')).join('|');
    if (sig === lastSig && rows.every((r, i) => prevRows[i])) {
      rows.forEach((r, i) => { r.el = prevRows[i].el; r.track = prevRows[i].track; r.cards.forEach((c, j) => updateCard(r.track.children[j], c)); });
      prevRows = rows;
      pos.col = Math.min(pos.col, rows[pos.row].cards.length - 1);
      layout();
      return;
    }
    lastSig = sig;
    prevRows = rows;
    rowsEl.replaceChildren(...rows.map(r => {
      const track = h('div', { class: 'row-track' }, r.cards.map(c => cardEl(c, r.shape)));
      r.el = h('div', { class: 'row' }, h('div', { class: 'row-title', text: r.title }), track);
      r.track = track;
      return r.el;
    }));
    pos.col = Math.min(pos.col, rows[pos.row].cards.length - 1);
    layout();
  }

  function layout() {
    // Taller poster rows sit higher, so a lit poster (lift, ring and label) is always fully on screen.
    let y = rows[pos.row]?.shape === 'poster' ? ROW_TOP - 90 : ROW_TOP;
    rows.forEach((r, i) => {
      const off = i < pos.row ? -1 : 0;
      r.el.classList.toggle('gone', i < pos.row);
      r.el.classList.toggle('active', i === pos.row && navIndex === null);
      if (i >= pos.row) { r.el.style.transform = `translateY(${y}px)`; y += ROW_H[r.shape]; }
      else r.el.style.transform = `translateY(${ROW_TOP - 200}px)`;
      const col = i === pos.row ? pos.col : (memory.get(i) ?? 0);
      r.track.style.transform = `translateX(${-Math.max(0, col - 1) * STEP[r.shape]}px)`;
      [...r.track.children].forEach((c, j) => c.classList.toggle('focused', navIndex === null && i === pos.row && j === pos.col));
      void off;
    });
    renderHero();
  }

  function heroFor(card, eyebrow) {
    const now = ctx.now();
    if (card.type === 'channel') {
      const p = card.programme;
      if (p.noInfo) {
        return { key: card.channelId, backdrop: null, ambient: null, colour: card.colour, logo: null, eyebrow: [h('span', { class: 'pill live', text: 'Live' }), `${card.label}`],
          title: card.channelName, meta: [h('span', { text: 'No information for this channel' })], synopsis: '', progress: null };
      }
      const t = p.titleId ? titleById(p.titleId) : null;
      return {
        key: card.channelId, backdrop: p.art.backdrop, ambient: p.art.ambient, colour: card.colour, fbChannel: { name: card.channelName, colour: card.colour }, logo: t?.art.logo ?? p.art.logo,
        eyebrow: [h('span', { class: 'pill live', text: 'Live' }), `${card.label}`],
        title: p.title, meta: [h('b', { text: `${hhmm(p.start)}–${hhmm(p.end)}` }), `${minsLeft(p.end, now)} min left`, t?.youWatch ? h('span', { class: 'pill mine', text: 'You watch' }) : null],
        synopsis: p.synopsis, progress: card.progress,
      };
    }
    const t = titleById(card.titleId);
    const src = bestSource(t);
    const meta = [];
    if (card.type === 'episode') {
      const ep = t.seasons.find(s => s.n === card.season).episodes[card.episode - 1];
      meta.push(h('b', { text: `S${card.season} E${card.episode}` }), ep.title, `${ep.mins} min`);
      return { key: t.id + card.episode, backdrop: t.art.backdrop, ambient: t.art.ambient, logo: t.art.logo, eyebrow: [eyebrow, src ? `on ${SERVICE_NAMES[src.service]}` : ''].join(' '), title: t.name, meta, synopsis: ep.synopsis || t.synopsis, progress: card.progress || null };
    }
    meta.push(h('b', { text: String(t.year) }), t.kind === 'film' ? 'Film' : 'Series', h('span', { class: 'pill', text: t.rating }));
    if (card.scope) meta.push(h('span', { class: 'pill', text: card.scope }));
    meta.push(h('span', { text: availabilitySummary(t) }));
    if (card.progress && t.availability === 'play') meta.push(card.sublabel);
    return { key: t.id, backdrop: t.art.backdrop, ambient: t.art.ambient, logo: t.art.logo, eyebrow: [eyebrow, src && t.availability === 'play' ? `on ${SERVICE_NAMES[src.service]}` : ''].join(' '), title: t.name, meta, synopsis: t.synopsis, progress: card.progress && t.availability === 'play' ? card.progress : null };
  }

  function renderHero() {
    let info;
    if (showPlaying && ctx.playingHero) info = ctx.playingHero();
    else {
      const r = rows[pos.row];
      info = heroFor(r.cards[pos.col], r.eyebrow);
    }
    el.classList.toggle('over-video', !!(showPlaying && ctx.playingHero));
    if (info.key !== lastHeroKey) {
      lastHeroKey = info.key;
      const img = art(info.backdrop, info.key, '', { title: info.backdrop ? null : info.title, channel: info.fbChannel });
      heroArt.append(img);
      requestAnimationFrame(() => requestAnimationFrame(() => {
        img.classList.add('on');
        [...heroArt.children].slice(0, -1).forEach(o => { o.classList.remove('on'); setTimeout(() => o.remove(), 700); });
      }));
      ctx.setAmbient(info.ambient, info.colour);
    }
    const spans = xs => [xs].flat().filter(x => x !== null && x !== undefined && x !== '').map(x => (x instanceof Node ? x : h('span', { text: String(x) })));
    hero.replaceChildren(...[
      h('div', { class: 'eyebrow' }, spans(info.eyebrow)),
      info.logo ? h('img', { class: 'logo', src: info.logo, alt: info.title }) : h('h1', { class: 'title', text: info.title }),
      h('div', { class: 'meta' }, spans(info.meta)),
      h('div', { class: 'synopsis', text: info.synopsis ?? '' }),
      info.progress != null ? h('div', { class: 'bar' }, h('i', { style: { width: `${Math.round(info.progress * 100)}%` } })) : null,
    ].filter(Boolean));
  }

  const current = () => rows[pos.row].cards[pos.col];

  const focusedEl = () => rows[pos.row]?.track.children[pos.col];

  function onAction(a) {
    if (['up', 'down', 'left', 'right'].includes(a)) {
      if (a === 'up' && pos.row === 0) return false; // the shell lights the tab bar
      pos = moveInRows(rows.map(r => r.cards.length), pos, a, memory);
      showPlaying = false;
      layout();
      return true;
    }
    if (a === 'pagedown' || a === 'pageup') {
      for (let i = 0; i < 4; i++) pos = moveInRows(rows.map(r => r.cards.length), pos, a === 'pagedown' ? 'right' : 'left', memory);
      layout();
      return true;
    }
    if (a === 'ok' || a === 'playpause') { ctx.activate(current(), a, focusedEl()); return true; }
    if (a === 'hold-ok') { ctx.openActions(current(), focusedEl()); return true; }
    return false;
  }

  build();
  return {
    el, onAction,
    onShow({ playing } = {}) { showPlaying = !!playing; build(); },
    onFocusOut() { navIndex = -1; layout(); },   // the tab bar is lit: nothing in Home is
    onFocusIn() { navIndex = null; layout(); },
    refresh: build,
    /** The Home key: back to the first card of Up next. */
    reset() { pos = { row: 0, col: 0 }; memory.clear(); navIndex = null; showPlaying = false; layout(); },
  };
}
