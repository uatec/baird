// The shell: layers (ambient · ground video · places · pages · overlays), the tab bar, the shared ctx, and global keys.
import { installInput, installStageScaling } from './input.js';
import { isLowPower, webglRenderer } from './device.js';
import { createNavigator } from './nav.js';
import { createPlayer } from './player.js';
import { createHome } from './screens/home.js';
import { create as createGuide } from './screens/guide.js';
import { create as createSearch } from './screens/search.js';
import { create as createShow } from './screens/show.js';
import { create as createSettings } from './screens/settings.js';
import { createActionsOverlay, actionsFor } from './overlays/actions.js';
import { create as createInfo } from './overlays/info.js';
import { create as createChannels } from './overlays/channels.js';
import { create as createEndcard } from './overlays/endcard.js';
import { create as createNearend } from './overlays/nearend.js';
import { create as createNumber } from './overlays/number.js';
import { create as createShowings } from './overlays/showings.js';
import { create as createReminder } from './overlays/reminder.js';
import { h, hhmm, art, stageRect } from './ui.js';
import { icon } from './icons.js';
import { titleById, channelById, channels, SERVICE_NAMES, bestSource, touchUpNext, catalogue, programmeAt, defaultWatchlist,
  requestSeasons, completeRequest, seasonState, episodeState, requestEpisode } from './data.js';

const PLACES = ['home', 'guide', 'search', 'settings'];
const PLACE_LABEL = { home: 'Home', guide: 'Live TV', search: 'Search', settings: 'Settings' };
const PAGES = { guide: createGuide, show: createShow };
const OVERLAYS = { info: createInfo, channels: createChannels, endcard: createEndcard, nearend: createNearend, number: createNumber,
  showings: createShowings, reminder: createReminder, actions: (ctx, p) => createActionsOverlay(ctx, p) };
const ENTER = { channels: 'enter-left', info: 'enter-up', endcard: 'enter-up', nearend: 'enter-up', number: 'enter-fade', reminder: 'enter-down', showings: 'enter-right', actions: '' };

const stage = document.getElementById('stage');
const lite = isLowPower({ ua: navigator.userAgent, renderer: webglRenderer(), search: location.search });
stage.classList.toggle('lite', lite);

const ambient = h('div', { class: 'ambient' });
const ground = h('div', { class: 'ground' });
const tuneLayer = h('div', { class: 'tune-layer' });
const placeLayer = h('div', { class: 'layer layer-place' });
const tabbar = h('div', { class: 'tabbar' }, PLACES.map(p => h('div', { class: 'tab', 'data-place': p }, p === 'settings' ? icon('gear', 30) : PLACE_LABEL[p])));
const pageLayer = h('div', { class: 'layer layer-page' });
const backChip = h('div', { class: 'back-chip glass' });
const overlayLayer = h('div', { class: 'layer layer-overlay' });
const clock = h('div', { class: 'clock' });
const toastEl = h('div', { class: 'toast glass' });
const zoomLayer = h('div', { class: 'zoom-layer' });
stage.append(ambient, ground, tuneLayer, placeLayer, pageLayer, tabbar, backChip, clock, overlayLayer, zoomLayer, h('div', { class: 'grain' }), toastEl);

let clockShift = 0;
const nav = createNavigator();
const state = {
  watchlist: [...defaultWatchlist],
  reminders: new Map(),     // programme id → programme
  settings: { scale: 100, subs: false, autoplay: true, guideLayout: 'rows' },
};
let tabFocus = false;       // is the tab bar lit (rather than the place's content)?

const ctx = {
  nav, lite, state,
  hold: new URLSearchParams(location.search).has('hold'), // screenshots: no auto-dismiss or countdowns
  now: () => Date.now() + clockShift,
  shiftClock(ms) { clockShift += ms; },
  setAmbient(src, colour) {
    const img = src ? h('img', { src, alt: '' }) : h('div', { class: 'amb-colour', style: { background: colour ? `radial-gradient(70% 80% at 70% 30%, ${colour}, transparent 70%)` : 'none' } });
    ambient.append(img);
    requestAnimationFrame(() => requestAnimationFrame(() => {
      img.classList.add('on');
      [...ambient.children].slice(0, -1).forEach(o => { o.classList.remove('on'); setTimeout(() => o.remove(), 700); });
    }));
  },
  toast(text) {
    toastEl.textContent = text;
    toastEl.classList.add('on');
    clearTimeout(ctx._toastT);
    ctx._toastT = setTimeout(() => toastEl.classList.remove('on'), 3200);
  },

  // ---- navigation
  openPage(name, params = {}) {
    if (name === 'guide' && !nav.state().atVideo && !nav.state().pages.length) { ctx.goPlace('guide', params); return; }
    nav.openPage({ name, params });
  },
  goPlace(name, params) {
    nav.setPlace(name);
    tabFocus = false;
    if (params && placeCtrl(name).focus) placeCtrl(name).focus(params);
    render();
  },
  openShow(titleId, fromEl) {
    if (!titleId) return;
    zoom(fromEl, titleById(titleId).art.backdrop, () => nav.openPage({ name: 'show', params: { titleId } }));
  },
  openTop(name) { if (name === 'home') nav.home(); else ctx.goPlace(name); },

  // ---- playback
  playVod(titleId, season, episode, posMs = 0, fromEl) {
    const cur = player.current;
    if (cur?.kind === 'vod' && cur.titleId === titleId && (cur.season ?? null) === (season ?? null) && (cur.episode ?? null) === (episode ?? null)) {
      nav.toVideo(); // already playing: go back to it rather than restarting
      return;
    }
    const t = titleById(titleId);
    if (t.seasons && season && episodeState(t, season, episode) !== 'available') {
      ctx.toast(`S${season} E${episode} of ${t.name} isn't streaming yet. Request it from the show page.`);
      return;
    }
    if (t.seasons) t.progress = { season, episode, positionMins: posMs / 60_000 };
    else if (!t.progress) t.progress = { positionMins: 0 };
    touchUpNext(titleId);
    const ep = t.seasons ? t.seasons.find(s => s.n === season).episodes[episode - 1] : null;
    zoom(fromEl, ep?.still ?? t.art.backdrop, () => { player.playVod(titleId, season, episode, posMs); nav.play({ titleId, season, episode }, 'vod'); flashBackChip(); });
  },
  playLive(channelId, fromEl, { keepOverlay = false } = {}) {
    if (player.current?.kind === 'live' && player.current.channelId === channelId && !nav.state().atVideo) { nav.toVideo(); return; }
    const changing = player.current?.kind === 'live' && player.current.channelId !== channelId;
    const go = () => { tune(channelId, changing); nav.play({ channelId }, 'live', { keepOverlay }); if (!keepOverlay) flashBackChip(); };
    if (changing || !fromEl) go(); else zoom(fromEl, programmeAt(channelId, ctx.now())?.art.backdrop, go);
  },
  /** Play a title from where you are in it: the next episode, the resume point, or the start. */
  playTitle(titleId, fromEl) {
    const t = titleById(titleId);
    if (t.seasons) {
      const p = t.progress ?? (() => { const s = t.seasons.find(x => seasonState(t, x.n) === 'available') ?? t.seasons[0]; return { season: s.n, episode: 1, positionMins: 0 }; })();
      return ctx.playVod(t.id, p.season, p.episode, (p.positionMins ?? 0) * 60_000, fromEl);
    }
    ctx.playVod(t.id, null, null, (t.progress?.positionMins ?? 0) * 60_000, fromEl);
  },
  playCatchup(p) {
    ctx.playLive(p.channelId);
    player.setBehind(ctx.now() - p.start);
    ctx.toast(`Playing ${p.title} from the start`);
  },
  activate(card, how, fromEl) {
    if (card.type === 'channel') return ctx.playLive(card.channelId, fromEl);
    if (card.type === 'episode') return ctx.playVod(card.titleId, card.season, card.episode, 0, fromEl);
    const t = titleById(card.titleId);
    if (t.kind === 'film' && t.availability === 'play') return ctx.playTitle(t.id, fromEl);
    if (t.progress && t.availability !== 'get') return ctx.playTitle(t.id, fromEl); // series in progress (e.g. from Up next)
    ctx.openShow(t.id, fromEl);
  },
  openActions(card, anchorEl) {
    const t = card.titleId ? titleById(card.titleId) : null;
    nav.openOverlay({ name: 'actions', params: { title: card.type === 'channel' ? card.programme.title : t.name, subtitle: card.type === 'channel' ? card.label : card.scope ?? card.sublabel,
      items: actionsFor({ kind: 'card', card }, ctx), anchor: anchorEl ? stageRect(anchorEl) : null } });
  },

  // ---- library
  request(titleId, seasons = null) {
    const t = titleById(titleId);
    requestSeasons(t, seasons);
    const what = t.seasons ? (seasons?.length === 1 ? `season ${seasons[0]} of ${t.name}` : `the missing seasons of ${t.name}`) : t.name;
    ctx.toast(`Requested ${what}. It downloads to Jellyfin and appears in Up next when it's ready.`);
    places.home.refresh();
  },
  requestEpisode(titleId, season, episode) {
    const t = titleById(titleId);
    requestEpisode(t, season, episode);
    ctx.toast(`Requested S${season} E${episode} of ${t.name}. It downloads to Jellyfin and appears in Up next when it's ready.`);
    placeCtrl('home').refresh();
  },
  watchEntry: (titleId, season, episode) => ({ kind: episode ? 'episode' : season ? 'season' : 'title', titleId, ...(season ? { season } : {}), ...(episode ? { episode } : {}) }),
  inWatchlist: (titleId, season, episode) => state.watchlist.some(e => e.titleId === titleId && (e.season ?? null) === (season ?? null) && (e.episode ?? null) === (episode ?? null)),
  toggleWatchlist(titleId, season = null, episode = null) {
    const t = titleById(titleId);
    const what = episode ? `S${season} E${episode} of ${t.name}` : season ? `Season ${season} of ${t.name}` : t.name;
    const i = state.watchlist.findIndex(e => e.titleId === titleId && (e.season ?? null) === season && (e.episode ?? null) === episode);
    if (i >= 0) { state.watchlist.splice(i, 1); ctx.toast(`Removed ${what} from your watchlist`); }
    else { state.watchlist.unshift(ctx.watchEntry(titleId, season, episode)); ctx.toast(`Added ${what} to your watchlist`); }
    places.home.refresh();
  },
  hasReminder: id => state.reminders.has(id),
  toggleReminder(p) {
    const ch = channelById(p.channelId);
    if (state.reminders.delete(p.id)) { ctx.toast(`Reminder cancelled: ${p.title}`); return; }
    state.reminders.set(p.id, p);
    ctx.toast(`Reminder set: ${p.title}, ${ch.name}, ${dayWord(p.start)} ${hhmm(p.start)}. A minute before, we'll ask before switching.`);
  },
  playingHero() {
    const cur = player.current;
    if (!cur) return null;
    if (cur.kind === 'live') {
      const p = player.programme(), ch = channelById(cur.channelId);
      return { key: `playing-${p.id}`, backdrop: null, ambient: null, logo: p.titleId ? titleById(p.titleId)?.art.logo : p.art.logo,
        eyebrow: [h('span', { class: 'pill live', text: 'Live' }), `Now playing on ${ch.name}`], title: p.title,
        meta: [h('b', { text: `${hhmm(p.start)}–${hhmm(p.end)}` })], synopsis: p.synopsis, progress: (ctx.now() - p.start) / (p.end - p.start) };
    }
    const t = titleById(cur.titleId), ep = player.episodeOf(cur);
    return { key: `playing-${cur.titleId}-${cur.episode}`, backdrop: null, ambient: null, logo: t.art.logo, eyebrow: `Now playing on ${SERVICE_NAMES[bestSource(t)?.service] ?? ''}`,
      title: t.name, meta: cur.season ? [h('b', { text: `S${cur.season} E${cur.episode}` }), ep.title] : [h('b', { text: String(t.year) })], synopsis: ep.synopsis || t.synopsis, progress: cur.posMs / cur.durMs };
  },
};
const dayWord = ms => {
  const d = new Date(ms), n = new Date(ctx.now());
  const days = Math.round((new Date(d.getFullYear(), d.getMonth(), d.getDate()) - new Date(n.getFullYear(), n.getMonth(), n.getDate())) / 86_400_000);
  return days === 0 ? 'today' : days === 1 ? 'tomorrow' : days === -1 ? 'yesterday' : d.toLocaleDateString('en-GB', { weekday: 'long' });
};
ctx.dayWord = dayWord;

const player = createPlayer(ctx, ground);
ctx.player = player;

// ------------------------------------------------------------------ places (the tabs)
const places = { home: null, guide: null, search: null, settings: null };
const placeFactory = { home: () => createHome(ctx), guide: () => createGuide(ctx, { place: true }), search: () => createSearch(ctx, {}), settings: () => createSettings(ctx) };
function placeCtrl(name) {
  if (!places[name]) places[name] = placeFactory[name]();
  return places[name];
}
placeCtrl('home');
ctx.resetPlace = name => { if (places[name] && name !== 'home') { places[name].destroy?.(); places[name] = null; if (shownPlace === name) shownPlace = null; } };

// ------------------------------------------------------------------ shared-element zoom (a card grows into what it opens)
function zoom(fromEl, src, then) {
  if (!fromEl || ctx.lite) { then(); return; }
  const r = stageRect(fromEl.querySelector?.('.card-art, .top-art, .end-thumb') ?? fromEl);
  const el = h('div', { class: 'zoom', style: { left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px` } }, art(src, 'zoom'));
  zoomLayer.append(el);
  requestAnimationFrame(() => requestAnimationFrame(() => { el.classList.add('full'); }));
  setTimeout(() => { then(); el.classList.add('fade'); setTimeout(() => el.remove(), 420); }, 380);
}

// ------------------------------------------------------------------ tuning: the picture dims, an ident shows while the tuner locks (~1.5 s)
let tuneTimer = null;
function tune(channelId, changing) {
  const ch = channelById(channelId);
  const p = programmeAt(channelId, ctx.now());
  player.playLive(channelId);
  ground.classList.add('tuning');
  tuneLayer.replaceChildren(h('div', { class: 'ident glass' },
    h('div', { class: 'ident-num', text: ch.number }),
    h('div', { class: 'ident-text' }, h('div', { class: 'ident-name', text: ch.name }), h('div', { class: 'ident-prog', text: p.noInfo ? 'No programme information' : `${p.title} · until ${hhmm(p.end)}` })),
    h('div', { class: 'ident-spin' })));
  tuneLayer.classList.add('on');
  clearTimeout(tuneTimer);
  tuneTimer = setTimeout(() => {
    ground.classList.remove('tuning');
    setTimeout(() => tuneLayer.classList.remove('on'), changing ? 700 : 300);
  }, ctx.lite ? 900 : 1500);
}

// ------------------------------------------------------------------ rendering the layer stack
let shownPage = null, shownOverlay = null, shownPlace = null, placeWasHidden = false, placeHideTimer = null;
const alivePages = new Set();
function render() {
  const s = nav.state();
  const top = nav.top();
  ground.classList.toggle('on', !!s.video);

  const placeVisible = s.pages.length === 0 && !(s.video && s.atVideo);
  // Hide the place only once whatever covers it has finished coming in (a page or the video).
  clearTimeout(placeHideTimer);
  if (placeVisible) placeLayer.style.display = '';
  else if (placeLayer.style.display !== 'none') placeHideTimer = setTimeout(() => { const st = nav.state(); if (st.pages.length || (st.video && st.atVideo)) placeLayer.style.display = 'none'; }, 460);
  tabbar.classList.toggle('on', placeVisible);
  if (placeVisible && shownPlace !== s.place) {
    const prev = shownPlace;
    const ctrl = placeCtrl(s.place);
    const dir = prev ? Math.sign(PLACES.indexOf(s.place) - PLACES.indexOf(prev)) : 0;
    // Keep the place we're leaving underneath until the new one has come in: nothing flashes through.
    [...placeLayer.children].forEach(old => { if (old !== ctrl.el) { old.classList.add('outgoing'); setTimeout(() => { if (old.parentNode && old !== placeCtrl(shownPlace).el) old.remove(); }, 460); } });
    placeLayer.append(ctrl.el);
    ctrl.el.classList.remove('enter-from-right', 'enter-from-left', 'enter-fade');
    void ctrl.el.offsetWidth;
    ctrl.el.classList.add(dir > 0 ? 'enter-from-right' : dir < 0 ? 'enter-from-left' : 'enter-fade');
    ctrl.onShow?.({ playing: !!s.video });
    if (tabFocus) ctrl.onFocusOut?.(); else ctrl.onFocusIn?.();
    shownPlace = s.place;
  } else if (placeVisible && placeWasHidden) {
    placeCtrl(s.place).onShow?.({ playing: !!s.video });
  }
  placeWasHidden = !placeVisible;
  places.home?.el.classList.toggle('over-video', !!s.video);
  [...tabbar.children].forEach(t => {
    t.classList.toggle('current', t.dataset.place === s.place);
    t.classList.toggle('focused', tabFocus && t.dataset.place === s.place && !s.overlay);
  });
  stage.classList.toggle('tabs-lit', tabFocus && placeVisible);

  const page = s.pages.at(-1) ?? null;
  if (page !== shownPage) {
    const leaving = shownPage?.ctrl?.el;
    const going = leaving && !s.pages.includes(shownPage); // popped (Back), not just covered
    [...pageLayer.children].forEach(e => { if (e !== leaving) e.remove(); });
    if (page) {
      if (!page.ctrl) page.ctrl = PAGES[page.name](ctx, page.params, page);
      const el = page.ctrl.el;
      el.classList.remove('enter-up', 'leaving', 'enter-fade'); void el.offsetWidth;
      // Coming back to a page underneath: it's already there, so it fades up gently; a new page rises.
      el.classList.add(going ? 'enter-fade' : 'enter-up');
      if (going) pageLayer.prepend(el); else pageLayer.append(el);
      page.ctrl.onShow?.();
    }
    if (leaving && leaving !== page?.ctrl?.el) {
      if (going || !page) { leaving.classList.add('leaving'); if (page) pageLayer.append(leaving); setTimeout(() => leaving.remove(), 300); }
      else setTimeout(() => { if (leaving !== nav.state().pages.at(-1)?.ctrl?.el) leaving.remove(); }, 460);
    }
    shownPage?.ctrl?.onHide?.();
    shownPage = page;
  }
  // Pages that have left the stack (and aren't waiting under the video) release their timers.
  const kept = new Set([...s.pages, ...s.originPages]);
  for (const p of alivePages) if (!kept.has(p)) { p.ctrl?.destroy?.(); alivePages.delete(p); }
  for (const p of kept) alivePages.add(p);

  const ov = s.overlay;
  if (ov !== shownOverlay) {
    shownOverlay?.ctrl?.destroy?.();
    const old = shownOverlay?.ctrl?.el;
    [...overlayLayer.children].forEach(e => { if (e !== old) e.remove(); });
    if (old) { old.classList.add('leaving'); setTimeout(() => old.remove(), 260); }
    if (ov) {
      ov.ctrl = OVERLAYS[ov.name](ctx, ov.params ?? {});
      if (ENTER[ov.name]) ov.ctrl.el.classList.add(ENTER[ov.name]);
      overlayLayer.append(ov.ctrl.el);
    }
    shownOverlay = ov;
  }

  // Where Back goes: a small chip, top left, on stacked pages.
  const below = s.pages.length >= 2 ? s.pages.at(-2) : null;
  const backTo = page && !ov ? (below ? labelFor(below) : s.video && s.atVideo ? labelForVideo() : PLACE_LABEL[s.place]) : null;
  if (!backChip.classList.contains('flash')) {
    backChip.classList.toggle('on', !!backTo);
    if (backTo) backChip.replaceChildren(h('span', { class: 'chev', text: '‹' }), `Back to ${backTo}`);
  }

  ambient.style.opacity = top === 'video' || (s.video && s.atVideo && !page) ? '0' : '1';
  clock.style.display = top === 'video' ? 'none' : '';
}
const labelFor = p => (p.name === 'show' ? titleById(p.params.titleId).name : p.name === 'guide' ? 'Live TV' : PLACE_LABEL[p.name] ?? p.name);
const labelForVideo = () => { const c = player.current; return c?.kind === 'live' ? channelById(c.channelId).name : c ? titleById(c.titleId).name : 'the video'; };
let chipTimer = null;
function flashBackChip() {
  // Entering the video: briefly say where Back will go.
  const s = nav.state();
  const to = s.originPages.length ? labelFor(s.originPages.at(-1)) : PLACE_LABEL[s.place];
  backChip.replaceChildren(h('span', { class: 'chev', text: '‹' }), `Back to ${to}`);
  backChip.classList.add('on', 'flash');
  clearTimeout(chipTimer);
  chipTimer = setTimeout(() => { backChip.classList.remove('on', 'flash'); render(); }, 2600);
}
nav.onChange(render);

function active() {
  const s = nav.state();
  if (s.overlay) return s.overlay.ctrl;
  const page = s.pages.at(-1);
  if (page) return page.ctrl;
  if (s.video && s.atVideo) return videoCtrl;
  return placeCtrl(s.place);
}

// ------------------------------------------------------------------ the bare video's keys
const videoCtrl = { onAction: (a, opts) => videoAction(a, opts) };
function videoAction(a, opts = {}) {
  const cur = player.current;
  if (!cur) return false;
  if (a === 'ok') { nav.openOverlay({ name: 'info' }); return true; }
  if (a === 'hold-ok') { nav.openOverlay({ name: 'actions', params: { title: 'Now playing', subtitle: cur.kind === 'live' ? channelById(cur.channelId).name : titleById(cur.titleId).name, items: actionsFor({ kind: 'playing' }, ctx) } }); return true; }
  if ((a === 'up' || a === 'down') && cur.kind === 'live') { nav.openOverlay({ name: 'channels', params: { step: a } }); return true; }
  if ((a === 'pageup' || a === 'pagedown') && cur.kind === 'live') {
    const n = player.channelIndex() + (a === 'pageup' ? 1 : -1);
    ctx.playLive(channels[(n + channels.length) % channels.length].id);
    return true;
  }
  if (a.startsWith('digit:')) { nav.openOverlay({ name: 'number', params: { first: +a.slice(6) } }); return true; }
  if (a === 'playpause') { player.togglePause(); nav.openOverlay({ name: 'info' }); return true; }
  if (a === 'prev' || a === 'next') { player.seek(a === 'prev' ? -10_000 : 30_000); nav.openOverlay({ name: 'info' }); return true; }
  if (a === 'left' || a === 'right') {
    // Scrub: ◂ ▸ move the playhead (holding speeds up), with the info bar showing where you are.
    player.seek((a === 'left' ? -1 : 1) * (opts.repeat ? 120_000 : 30_000));
    if (nav.state().overlay?.name !== 'info') nav.openOverlay({ name: 'info', params: { scrub: true } });
    return true;
  }
  return false;
}
ctx.videoAction = videoAction;

// ------------------------------------------------------------------ player moments: end cards, near-end, saving position
const nearShown = new Set();
let endedFor = null;
player.onChange(ev => {
  const cur = player.current;
  if (!cur) return;
  const onVideo = () => { const t = nav.top(); return t === 'video' || (typeof t === 'object' && t.name === 'info'); };
  if (ev === 'play') { endedFor = null; return; }
  if (ev === 'tick' && cur.kind === 'vod' && cur.posMs < cur.durMs) {
    const t = titleById(cur.titleId);
    if (t.seasons && cur.season) t.progress = { season: cur.season, episode: cur.episode, positionMins: cur.posMs / 60_000 };
    else if (!t.seasons) t.progress = { positionMins: cur.posMs / 60_000 };
  }
  if (ev === 'ended' && cur.kind === 'vod') {
    const key = `${cur.titleId}-${cur.season}-${cur.episode}`;
    if (endedFor === key) return;
    endedFor = key;
    const t = titleById(cur.titleId);
    if (t.seasons) {
      const season = t.seasons.find(s => s.n === cur.season);
      const nextSeason = t.seasons.find(s => s.n > cur.season && seasonState(t, s.n) === 'available');
      const next = cur.episode < season.episodes.length ? { season: cur.season, episode: cur.episode + 1 } : nextSeason ? { season: nextSeason.n, episode: 1 } : null;
      if (next) {
        t.progress = { ...next, positionMins: 0 };
        if (onVideo()) nav.openOverlay({ name: 'endcard', params: { titleId: t.id, ...next } });
        return;
      }
    }
    t.progress = null;
    player.stop(); nav.stop(); nav.home(); places.home.refresh();
    ctx.toast(`Finished ${t.name}`);
  }
  if (ev === 'tick' && cur.kind === 'live') {
    const p = player.programme();
    const leftMs = p.end - (ctx.now() - cur.behindMs);
    if (!p.noInfo && leftMs <= 5 * 60_000 && !nearShown.has(p.id) && nav.top() === 'video') { nearShown.add(p.id); nav.openOverlay({ name: 'nearend' }); }
  }
});

// ------------------------------------------------------------------ the help legend
let legendEl = null;
function toggleLegend() {
  if (legendEl) { legendEl.remove(); legendEl = null; return; }
  const rows = [['Arrows', '↑ ↓ ← →'], ['OK', 'Enter (hold for actions)'], ['Back', 'Esc or Backspace'], ['Home', 'H'], ['Guide (≡)', 'G'],
    ['Play / pause', 'Space'], ['⏮ / ⏭', '[ and ]'], ['Pg+ / Pg−', 'Page Up / Page Down'], ['Numbers', '0–9'], ['DEL', 'Delete'], ['Voice', 'V'],
    ['Key inspector', 'K'], ['Jump near the end', 'F'], ['Jump to a reminder', 'R'], ['This help', '?']];
  legendEl = h('div', { class: 'legend glass enter-fade' }, h('h2', { text: 'Remote ↔ keyboard' }), rows.map(([a, b]) => [h('div', { text: a }), h('div', {}, h('kbd', { text: b }))]));
  stage.append(legendEl);
}

// ------------------------------------------------------------------ global keys
function onAction(a, opts = {}) {
  if (legendEl && a !== 'help') { toggleLegend(); if (a === 'back') return; }
  if (a === 'help') return toggleLegend();
  if (a === 'debug-ff') { player.ff(); return; }
  if (a === 'debug-reminder') {
    const next = [...state.reminders.values()].filter(p => p.start > ctx.now()).sort((x, y) => x.start - y.start)[0];
    if (next) { ctx.shiftClock(next.start - 55_000 - ctx.now()); ctx.toast(`Jumped to just before ${next.title}`); } else ctx.toast('No reminders set. Hold OK on a future programme in the guide.');
    return;
  }
  const s = nav.state();
  if (a === 'home') { nav.home(); tabFocus = false; placeCtrl('home').reset?.(); render(); return; }
  if (a === 'guide') {
    const page = s.pages.at(-1);
    if (page?.name === 'guide') { nav.closeOverlay(); page.ctrl?.onAction('guide'); return; }
    if (s.video && s.atVideo && !s.pages.length) {
      // From the video, the guide opens over it (Back returns to the picture).
      const cur = player.current;
      nav.openPage({ name: 'guide', params: cur?.kind === 'live' ? { channelId: cur.channelId } : {} });
      return;
    }
    if (s.place === 'guide' && !s.pages.length) { nav.closeOverlay(); tabFocus = false; placeCtrl('guide').onAction('guide'); render(); return; }
    nav.home(); ctx.goPlace('guide');
    return;
  }
  if (a === 'voice') {
    if (!(s.place === 'search' && !s.pages.length && !s.atVideo)) { nav.home(); ctx.goPlace('search'); }
    nav.closeOverlay(); tabFocus = false; placeCtrl('search').onAction('voice'); render();
    return;
  }

  // The tab bar: moving across it switches the place immediately; ▾ / OK enter the content.
  const onPlace = !s.overlay && !s.pages.length && !(s.video && s.atVideo);
  if (onPlace && tabFocus) {
    const i = PLACES.indexOf(s.place);
    if (a === 'left' && i > 0) { nav.setPlace(PLACES[i - 1]); render(); return; }
    if (a === 'right' && i < PLACES.length - 1) { nav.setPlace(PLACES[i + 1]); render(); return; }
    if (a === 'down' || a === 'ok') { tabFocus = false; placeCtrl(s.place).onFocusIn?.(); render(); return; }
    if (a === 'back') { if (s.place !== 'home') { nav.setPlace('home'); render(); } return; }
    if (a === 'left' || a === 'right' || a === 'up') return;
  }

  const ctrl = active();
  if (ctrl?.onAction(a, opts)) return;
  // Over the video, Pg±, digits and ⏮/⏭ keep their global meaning whatever overlay is up.
  if (s.overlay && s.video && s.atVideo && !s.pages.length && (a === 'pageup' || a === 'pagedown' || a === 'prev' || a === 'next' || a.startsWith('digit:'))) {
    if (videoAction(a)) return;
  }
  if (onPlace && (a === 'up' || a === 'back')) {
    // ▴ off the top of a place's content, or Back from inside it, lights its tab.
    tabFocus = true; placeCtrl(s.place).onFocusOut?.(); render(); return;
  }
  if (a === 'back') nav.back();
  else if (a === 'playpause' && player.current) player.togglePause();
}

installStageScaling(stage);
installInput(onAction);

// ------------------------------------------------------------------ the clock: playback, downloads, reminders
let ticks = 0;
const reminderShown = new Set();
setInterval(() => {
  clock.textContent = hhmm(ctx.now());
  player.tick();
  ticks++;
  // Requested seasons/films download in the background and land on Jellyfin, in Up next.
  if (ticks % 2 === 0) for (const t of catalogue) {
    for (const k of Object.keys(t.requests)) {
      t.requests[k] = Math.min(1, t.requests[k] + 0.008);
      if (t.requests[k] >= 1) {
        completeRequest(t, k);
        const [sn, en] = String(k).split('.').map(Number);
        if (!t.progress) t.progress = t.seasons ? { season: sn, episode: en || 1, positionMins: 0 } : { positionMins: 0 };
        touchUpNext(t.id);
        ctx.toast(`${!t.seasons ? t.name : en ? `S${sn} E${en} of ${t.name}` : `Season ${sn} of ${t.name}`} is ready to watch`);
      }
    }
  }
  if (ticks % 10 === 0 && shownPlace === 'home' && !nav.state().overlay) places.home.refresh();
  // Reminders: a minute before, ask; at the start, switch unless dismissed.
  for (const p of state.reminders.values()) {
    const left = p.start - ctx.now();
    if (left <= 60_000 && left > -30_000 && !reminderShown.has(p.id)) { reminderShown.add(p.id); nav.openOverlay({ name: 'reminder', params: { programme: p } }); }
    if (left < -60_000) state.reminders.delete(p.id);
  }
}, 500);
clock.textContent = hhmm(ctx.now());
render();
ctx.setAmbient(null);

// ------------------------------------------------------------------ debug routes for screenshots (#guide, #play-live+info …)
const route = decodeURIComponent(location.hash.slice(1));
if (route) {
  for (const step of route.split('+')) {
    const [name, arg] = step.split('=');
    if (name === 'layout') { state.settings.guideLayout = arg; ctx.resetPlace('guide'); }
    else if (name === 'guide') { ctx.goPlace('guide', arg ? { channelId: arg } : null); }
    else if (name === 'search') { ctx.goPlace('search'); if (arg) placeCtrl('search').setQuery?.(arg); }
    else if (name === 'show') nav.openPage({ name: 'show', params: { titleId: arg } });
    else if (name === 'settings') ctx.goPlace('settings');
    else if (name === 'play-live') ctx.playLive(arg ?? 'bbc1');
    else if (name === 'play-vod') ctx.playVod(arg ?? 'loki', 1, 4);
    else if (name === 'end') player.ff();
    else if (name === 'home') nav.home();
    else if (name === 'tabs') { tabFocus = true; render(); }
    else onAction(name); // e.g. ok, hold-ok, up, down, back
  }
}
window.__baird = { nav, player, ctx, onAction, state };
