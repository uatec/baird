// Hold OK: everything you can do with the lit thing. Opens beside what you held OK on, when we know where that is.
import { h, hhmm } from '../ui.js';
import { icon } from '../icons.js';
import { titleById, channelById, bestSource, SERVICE_NAMES, seasonState, showingsOf, sourcesFor, missingSeasons, episodeState } from '../data.js';

const runs = ns => { const out = []; for (const n of ns) { const l = out.at(-1); if (l && n === l[1] + 1) l[1] = n; else out.push([n, n]); } return out.map(([a, b]) => (a === b ? `${a}` : `${a}–${b}`)).join(', '); };

/** The request sheet: what you can ask for, from the narrowest (this episode) to the widest (everything missing). */
export function requestChoices(t, ctx, { season = null, episode = null } = {}) {
  const items = [];
  const miss = missingSeasons(t);
  if (!t.seasons) return [{ id: 'film', glyph: 'get', label: `Request ${t.name}`, hint: 'Downloads to Jellyfin', run: () => ctx.request(t.id) }];
  if (episode && episodeState(t, season, episode) === 'missing') {
    const ep = t.seasons.find(s => s.n === season).episodes[episode - 1];
    items.push({ id: 'ep', glyph: 'get', label: `This episode: S${season} E${episode}`, hint: ep.title, run: () => ctx.requestEpisode(t.id, season, episode) });
  }
  if (season && miss.includes(season)) items.push({ id: `s${season}`, glyph: 'get', label: `Season ${season}`, hint: `${t.seasons.find(s => s.n === season).episodes.length} episodes`, run: () => ctx.request(t.id, [season]) });
  if (miss.length > 1) items.push({ id: 'all', glyph: 'get', label: 'All missing seasons', hint: `Seasons ${runs(miss)}`, run: () => ctx.request(t.id, miss) });
  for (const n of miss) if (n !== season) items.push({ id: `s${n}`, glyph: 'get', label: `Season ${n}`, hint: `${t.seasons.find(s => s.n === n).episodes.length} episodes`, run: () => ctx.request(t.id, [n]) });
  return items;
}
export function openRequestSheet(t, ctx, scope = {}) {
  ctx.nav.openOverlay({ name: 'actions', params: { title: `Request ${t.name}`, subtitle: 'Downloads to Jellyfin, then appears in Up next', items: requestChoices(t, ctx, scope) } });
}

/**
 * item: { kind:'card', card } | { kind:'programme', programme, guide } | { kind:'playing' }
 * returns [{ id, glyph, label, hint, disabled, run() }]
 */
export function actionsFor(item, ctx) {
  const now = ctx.now();
  const out = [];
  const A = (id, glyph, label, hint, run, disabled = false) => out.push({ id, glyph, label, hint, run, disabled });
  const wl = (t, season = null, episode = null, label) => {
    const on = ctx.inWatchlist(t.id, season, episode);
    A(`wl-${season ?? ''}-${episode ?? ''}`, on ? 'minus' : 'plus', `${on ? 'Remove' : 'Add'} ${label} ${on ? 'from' : 'to'} watchlist`, '', () => ctx.toggleWatchlist(t.id, season, episode));
  };
  const missing = t => (t.seasons ?? []).filter(s => seasonState(t, s.n) === 'missing').map(s => s.n);

  if (item.kind === 'card') {
    const c = item.card;
    if (c.type === 'channel') {
      const p = c.programme, ch = channelById(c.channelId);
      A('watch', 'play', 'Watch now', ch.name, () => ctx.playLive(c.channelId));
      if (!p.noInfo) A('showings', 'list', 'All showings', 'Repeats and catch-up', () => ctx.nav.openOverlay({ name: 'showings', params: { title: p.title } }));
      A('series', 'series', 'Open the series', p.titleId ? 'Stream or request' : 'Not in the catalogue', () => ctx.openShow(p.titleId), !p.titleId);
      A('guide', 'grid', 'This channel in the guide', ch.name, () => ctx.openPage('guide', { channelId: c.channelId }));
      return out;
    }
    const t = titleById(c.titleId);
    if (c.type === 'episode') {
      const st = episodeState(t, c.season, c.episode);
      if (st === 'available') A('play', 'play', `Play S${c.season} E${c.episode}`, SERVICE_NAMES[sourcesFor(t, c.season)[0]?.service ?? 'jellyfin'], () => ctx.playVod(t.id, c.season, c.episode));
      else if (st === 'requested') A('play', 'get', 'Downloading', 'It appears in Up next when ready', () => {}, true);
      else A('get', 'get', 'Request…', 'This episode, the season, or more', () => openRequestSheet(t, ctx, { season: c.season, episode: c.episode }));
      wl(t, c.season, c.episode, 'this episode');
      wl(t, c.season, null, `season ${c.season}`);
      wl(t, null, null, 'the series');
      A('watched', 'check', 'Mark as watched', '', () => ctx.toast(`Marked S${c.season} E${c.episode} as watched`));
      A('series', 'series', 'Go to the series', t.name, () => ctx.openShow(t.id));
      return out;
    }
    if (t.availability === 'play' || t.availability === 'partial') A('play', 'play', t.progress ? 'Resume' : 'Play', SERVICE_NAMES[bestSource(t).service], () => ctx.playTitle(t.id));
    if ((!t.seasons && t.availability === 'get') || missing(t).length) A('get', 'get', 'Request…', t.seasons ? `Seasons ${runs(missing(t))} aren't streaming` : 'Downloads to Jellyfin', () => openRequestSheet(t, ctx, { season: c.season ?? null }));
    A('series', 'series', t.kind === 'film' ? 'Go to the film' : 'Go to the series', '', () => ctx.openShow(t.id));
    if (t.airsAs && showingsOf(t.airsAs, now).some(s => s.when !== 'catchup')) A('showings', 'list', 'On TV', 'All showings', () => ctx.nav.openOverlay({ name: 'showings', params: { title: t.airsAs } }));
    if (c.season) wl(t, c.season, null, `season ${c.season}`);
    wl(t, null, null, t.kind === 'film' ? 'the film' : 'the series');
    return out;
  }

  if (item.kind === 'programme') {
    const p = item.programme, ch = channelById(p.channelId);
    const t = p.titleId ? titleById(p.titleId) : null;
    if (p.noInfo) { A('watch', 'play', 'Watch now', ch.name, () => ctx.playLive(ch.id)); return out; }
    if (p.start <= now && now < p.end) A('watch', 'play', 'Watch now', ch.name, () => ctx.playLive(ch.id));
    if (p.start < now && ch.catchup) A('start', 'restart', 'Watch from the start', 'Catch-up', () => ctx.playCatchup(p));
    if (p.start > now) {
      const on = ctx.hasReminder(p.id);
      A('remind', on ? 'bellOn' : 'bell', on ? 'Cancel reminder' : 'Remind me', `${ctx.dayWord(p.start)} ${hhmm(p.start)} on ${ch.name}`, () => { ctx.toggleReminder(p); item.guide?.refresh?.(); });
    }
    A('showings', 'list', 'All showings', 'Other days, channels and catch-up', () => ctx.nav.openOverlay({ name: 'showings', params: { title: p.title, from: p.id } }));
    A('series', 'series', 'Open the series', t ? (t.availability === 'play' ? 'Stream now' : t.availability === 'partial' ? 'Partly streamable' : 'Request it') : 'Not in the catalogue', () => ctx.openShow(p.titleId), !t);
    if (t) wl(t, null, null, t.kind === 'film' ? 'the film' : 'the series');
    return out;
  }

  if (item.kind === 'playing') {
    const cur = ctx.player.current;
    A('subs', 'cc', 'Subtitles', ctx.player.subtitles ? 'On' : 'Off', () => ctx.toast(ctx.player.toggleSubtitles() ? 'Subtitles on' : 'Subtitles off'));
    A('start', 'restart', 'Start over', cur.kind === 'live' ? `From ${hhmm(ctx.player.programme().start)}` : '', () => ctx.player.startOver());
    if (cur.kind === 'live') {
      const p = ctx.player.programme();
      A('guide', 'grid', 'This channel in the guide', channelById(cur.channelId).name, () => ctx.openPage('guide', { channelId: cur.channelId }));
      if (!p.noInfo) A('showings', 'list', 'All showings', p.title, () => ctx.nav.openOverlay({ name: 'showings', params: { title: p.title } }));
      A('series', 'series', 'Open the series', p.titleId ? '' : 'Not in the catalogue', () => ctx.openShow(p.titleId), !p.titleId);
    } else {
      A('series', 'series', 'Go to the series', titleById(cur.titleId).name, () => ctx.openShow(cur.titleId));
    }
    A('audio', 'audio', 'Audio', 'English', () => ctx.toast('Audio: English (audio description off)'));
    if (cur.kind === 'live') A('fav', 'star', 'Add channel to favourites', '', () => ctx.toast(`${channelById(cur.channelId).name} added to favourites`));
    return out;
  }
  return out;
}

export function createActionsOverlay(ctx, { title, subtitle, items, anchor }) {
  let i = items.findIndex(x => !x.disabled);
  const list = h('div', { class: 'menu' });
  const panel = h('div', { class: `actions-panel glass${anchor ? ' anchored' : ' enter-right'}` },
    h('div', { class: 'actions-title', text: title }),
    subtitle ? h('div', { class: 'actions-sub', text: subtitle }) : null,
    list);
  if (anchor) {
    // Open beside what was held: to the right if it fits, else to the left; grow out of it.
    const W = 640, right = anchor.left + anchor.width + 28 + W <= 1880;
    const left = right ? anchor.left + anchor.width + 28 : Math.max(40, anchor.left - W - 28);
    const top = Math.max(60, Math.min(1080 - 60 - (110 + items.length * 82), anchor.top - 40));
    Object.assign(panel.style, { left: `${left}px`, top: `${top}px`, right: 'auto', bottom: 'auto', width: `${W}px`,
      transformOrigin: `${right ? 'left' : 'right'} ${Math.max(0, anchor.top + anchor.height / 2 - top)}px` });
  }
  const tether = anchor ? h('div', { class: 'actions-tether', style: { left: `${anchor.left - 6}px`, top: `${anchor.top - 6}px`, width: `${anchor.width + 12}px`, height: `${anchor.height + 12}px` } }) : null;
  const el = h('div', { class: 'overlay-actions' }, h('div', { class: 'scrim-dim enter-fade' }), tether, panel);

  function draw() {
    list.replaceChildren(...items.map((it, j) => h('div', { class: `menu-item${j === i ? ' focused' : ''}${it.disabled ? ' disabled' : ''}` },
      h('span', { class: 'glyph' }, icon(it.glyph)), h('span', { text: it.label }), it.hint ? h('span', { class: 'hint', text: it.hint }) : null)));
  }
  draw();
  return {
    el,
    onAction(a) {
      if (a === 'up' || a === 'down') {
        let j = i;
        do { j += a === 'up' ? -1 : 1; } while (j >= 0 && j < items.length && items[j].disabled);
        if (j >= 0 && j < items.length) { i = j; draw(); }
        return true;
      }
      if (a === 'ok') { const it = items[i]; ctx.nav.closeOverlay(); it?.run(); return true; }
      return a === 'left' || a === 'right' || a === 'hold-ok';
    },
  };
}
