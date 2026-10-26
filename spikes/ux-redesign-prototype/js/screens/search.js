// One search for everything: shows and films (wherever they stream, or to request), channels, and what's on TV.
// Results live in their own column as a grid that scrolls vertically — they never slide under the keyboard.
import { h, art, cardEl, hhmm } from '../ui.js';
import { icon } from '../icons.js';
import { searchAll } from '../search.js';
import { moveInRows } from '../focus.js';
import { createKeyboard } from '../overlays/keyboard.js';
import { channelById, programmeAt, discover, availabilitySummary } from '../data.js';

const RECENT = ['doctor who', 'blade runner', 'bbc news', 'paddington'];
const PER_ROW = { poster: 4, wide: 3, top: 1, chip: 99 };

export function create(ctx, params = {}) {
  let query = params.query ?? '';
  let zone = 'kbd';               // 'kbd' | 'results' | 'none' (the tab bar is lit)
  let pos = { row: 0, col: 0 };
  const memory = new Map();
  let sections = [];
  let rows = [];                  // [{ section, items, els }]
  let debounce = null;

  const el = h('div', { class: 'page search' });
  const field = h('div', { class: 'search-field' });
  const kb = createKeyboard({ onKey: key, onExit: dir => { if (dir === 'right' && rows.length) { zone = 'results'; draw(); return true; } return dir !== 'up'; } });
  const left = h('div', { class: 'search-left' }, field, kb.el);
  const inner = h('div', { class: 'search-inner' });
  const results = h('div', { class: 'search-results' }, inner);
  el.append(h('div', { class: 'search-bg' }), left, results);

  function key(k) {
    if (k === 'space') query += ' ';
    else if (k === 'del') query = query.slice(0, -1);
    else if (k === 'voice') return voice();
    else query += k;
    changed();
  }
  function changed() { drawField(); clearTimeout(debounce); debounce = setTimeout(() => { pos = { row: 0, col: 0 }; memory.clear(); build(); buildDom(); draw(); }, 150); }

  function voice() {
    const phrase = 'simpsons';
    query = '';
    field.classList.add('listening');
    let i = 0;
    const t = setInterval(() => { query += phrase[i++]; drawField(); if (i >= phrase.length) { clearInterval(t); field.classList.remove('listening'); changed(); } }, 90);
  }

  function build() {
    const now = ctx.now();
    if (!query.trim()) {
      sections = [
        { title: 'Recent searches', shape: 'chip', items: RECENT.map(q => ({ type: 'recent', q })) },
        { title: 'Popular to request', shape: 'poster', items: discover().filter(c => c.badge).slice(0, 8) },
      ];
    } else {
      const r = searchAll(query, now);
      sections = [];
      if (r.top) sections.push({ title: 'Top result', shape: 'top', items: [r.top] });
      const titles = r.titles.filter(x => x !== r.top);
      if (titles.length) sections.push({ title: 'Shows and films', shape: 'poster', items: titles });
      const chans = r.channels.filter(x => x !== r.top);
      if (chans.length) sections.push({ title: 'Channels', shape: 'wide', items: chans });
      if (r.onTv.length) sections.push({ title: 'On TV', shape: 'wide', items: r.onTv.filter(x => x !== r.top).slice(0, 9) });
    }
    rows = [];
    for (const s of sections) for (let k = 0; k < s.items.length; k += PER_ROW[s.shape]) rows.push({ section: s, first: k === 0, items: s.items.slice(k, k + PER_ROW[s.shape]) });
    if (!rows.length && zone === 'results') zone = 'kbd'; // nothing to the right: focus stays on the keyboard
  }

  const badgeLabel = b => ({ get: '⤓ Request', requested: 'Requested', partial: 'Partly available' })[b];
  function resultCard(item, shape) {
    const now = ctx.now();
    if (item.type === 'recent') return h('div', { class: 'chip', text: item.q });
    if (item.type === 'title' && !item.ref) return cardEl(item, 'poster'); // discover() cards
    if (item.type === 'title' && shape === 'poster') {
      const t = item.ref;
      return cardEl({ type: 'title', titleId: t.id, label: item.label, sublabel: availabilitySummary(t), art: t.art.poster ?? item.art, badge: item.badge === 'play' ? null : item.badge, progress: item.badge === 'requested' ? t.requestProgress : null }, 'poster');
    }
    if (shape === 'top') {
      const t = item.type === 'title' ? item.ref : null;
      const isChan = item.type === 'channel';
      const p = isChan ? programmeAt(item.ref.id, now) : item.type === 'listing' ? item.ref : null;
      const ch = isChan ? item.ref : item.channel;
      return h('div', { class: 'top-card' },
        h('div', { class: 'top-art' }, art(t ? t.art.backdrop : p.art.backdrop, t?.id ?? p.art.key, '', { title: t ? null : p.title, channel: t ? null : ch }), h('div', { class: 'card-shade' }),
          t?.art.logo ? h('img', { class: 'top-logo', src: t.art.logo, alt: t.name }) : h('div', { class: 'top-name', text: item.label })),
        h('div', { class: 'top-text' },
          h('div', { class: 'top-sub' }, ...(t ? [t.availability !== 'play' ? h('span', { class: `pill${t.availability === 'get' ? ' get' : ''}`, text: badgeLabel(t.availability) }) : h('span', { class: 'pill', text: '▶ Play' }), h('span', { text: availabilitySummary(t) })]
            : [h('span', { class: 'pill live', text: 'Live' }), h('span', { text: isChan ? `${ch.number} ${ch.name} · ${p.noInfo ? 'No information' : p.title}` : `${ch.name} · ${hhmm(p.start)}` })])),
          t ? h('div', { class: 'top-syn', text: t.synopsis }) : null));
    }
    if (item.type === 'channel') {
      const c = item.ref, p = programmeAt(c.id, now);
      return cardEl({ type: 'channel', channelId: c.id, label: `${c.number}  ${c.name}`, sublabel: p.noInfo ? 'No programme information' : p.title, timeText: p.noInfo ? '' : `until ${hhmm(p.end)}`, art: p.art.backdrop,
        progress: p.noInfo ? null : (now - p.start) / (p.end - p.start), badge: 'live', programme: p, colour: c.colour, channelName: c.name }, 'wide');
    }
    // listing
    const p = item.ref;
    const c = cardEl({ type: 'title', titleId: p.titleId, label: p.title, sublabel: `${item.channel.name} · ${ctx.dayWord(p.start)} ${hhmm(p.start)}`, art: p.art.backdrop, progress: null, badge: null }, 'wide');
    const tag = item.badge === 'live' ? h('span', { class: 'card-badge pill live', text: 'Live' }) : item.badge === 'catchup' ? h('span', { class: 'card-badge pill', text: '↺ Catch-up' }) : h('span', { class: 'card-badge pill', text: hhmm(p.start) });
    c.querySelector('.card-art').append(tag);
    return c;
  }

  function drawField() {
    field.replaceChildren(icon('search', 34), h('span', { class: `q${query ? '' : ' empty'}`, text: query || 'Shows, films, channels, what’s on' }), h('span', { class: 'caret' }));
  }

  // Build the grid once per query; moving focus only toggles classes and one transform (cheap on the Pi).
  function buildDom() {
    if (!rows.length) {
      inner.replaceChildren(h('div', { class: 'search-empty', text: `Nothing called “${query.trim()}” yet. Try fewer letters, or a channel number.` }));
      return;
    }
    inner.replaceChildren(...rows.map(r => {
      r.els = r.items.map(it => resultCard(it, r.section.shape));
      r.el = h('div', { class: `srow srow--${r.section.shape}${r.first ? ' first' : ''}` }, r.first ? h('div', { class: 'row-title', text: r.section.title }) : null,
        h('div', { class: `row-track grid${r.section.shape === 'chip' ? ' chips' : ''}` }, r.els));
      return r.el;
    }));
    rows.forEach(r => { r.top = r.el.offsetTop; r.height = r.el.offsetHeight; });
  }

  function draw() {
    kb.draw(zone === 'kbd');
    rows.forEach((r, i) => {
      r.el?.classList.toggle('active', zone === 'results' && i === pos.row);
      r.els?.forEach((c, j) => c.classList.toggle('focused', zone === 'results' && i === pos.row && j === pos.col));
    });
    // Scroll the column so the lit row is in view (with its section title when it's the first row).
    const r = rows[zone === 'results' ? pos.row : 0];
    const VIEW = 940;
    const cur = parseFloat(inner.dataset.y ?? '0');
    let y = cur;
    if (r) { if (r.top + y < 0) y = -r.top; if (r.top + r.height + y > VIEW) y = VIEW - r.top - r.height; }
    if (zone !== 'results') y = 0;
    inner.dataset.y = String(y);
    inner.style.transform = `translateY(${y}px)`;
  }

  function activate(item, el) {
    if (item.type === 'recent') { query = item.q; changed(); zone = 'kbd'; return; }
    if (item.type === 'title') return ctx.openShow(item.ref ? item.ref.id : item.titleId, el);
    if (item.type === 'channel') return ctx.playLive(item.ref.id, el);
    const p = item.ref, now = ctx.now();
    if (p.start <= now && now < p.end) return ctx.playLive(p.channelId, el);
    if (p.end <= now) return ctx.playCatchup(p);
    ctx.openPage('guide', { channelId: p.channelId, atMs: p.start });
  }

  build();
  drawField();
  buildDom();
  draw();
  if (params.voice) setTimeout(voice, 300);

  return {
    el,
    setQuery(q) { query = q; drawField(); build(); buildDom(); draw(); },
    onShow() { ctx.setAmbient(null); },
    onFocusOut() { zone = 'none'; draw(); },
    onFocusIn() { zone = 'kbd'; draw(); },
    onAction(a) {
      if (zone === 'none') zone = 'kbd';
      if (a.startsWith('digit:')) { query += a.slice(6); changed(); return true; }
      if (a === 'del') { query = query.slice(0, -1); changed(); return true; }
      if (a === 'voice') { voice(); return true; }
      if (zone === 'kbd') return kb.onAction(a) || a === 'hold-ok';
      if (!rows.length) { zone = 'kbd'; draw(); return true; }
      if (a === 'left' && pos.col === 0) { memory.set(pos.row, 0); zone = 'kbd'; draw(); return true; }
      if (a === 'up' && pos.row === 0) return false; // the shell lights the tab bar
      if (['up', 'down', 'left', 'right'].includes(a)) { pos = moveInRows(rows.map(r => r.items.length), pos, a, memory); draw(); return true; }
      const item = rows[pos.row]?.items[pos.col];
      const itemEl = rows[pos.row]?.els?.[pos.col];
      if (!item) return false;
      if (a === 'ok') { activate(item, itemEl); return true; }
      if (a === 'hold-ok') {
        if (item.type === 'title') { const id = item.ref?.id ?? item.titleId; ctx.openActions({ type: 'title', titleId: id, label: item.label, sublabel: item.sublabel }, itemEl); }
        else if (item.type === 'channel') { const p = programmeAt(item.ref.id, ctx.now()); ctx.openActions({ type: 'channel', channelId: item.ref.id, label: `${item.ref.number} ${item.ref.name}`, programme: p }, itemEl); }
        else if (item.type === 'listing') ctx.nav.openOverlay({ name: 'showings', params: { title: item.ref.title, from: item.ref.id } });
        return true;
      }
      return false;
    },
  };
}
