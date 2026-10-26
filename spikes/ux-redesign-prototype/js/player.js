// The ground floor: a simulated player. There is no real video: the programme/episode artwork drifts slowly
// (Ken Burns) and a clock advances the position. Live TV is time-shifted against the wall clock.
import { h, art } from './ui.js';
import { titleById, channelById, programmeAt, channels } from './data.js';

export function createPlayer(ctx, groundEl) {
  let cur = null; // { kind:'vod', titleId, season, episode, posMs, durMs } | { kind:'live', channelId, behindMs }
  let paused = false;
  let pausedAt = 0;
  let lastTick = ctx.now();
  let subtitles = false;
  const listeners = [];

  function episodeOf(c) {
    const t = titleById(c.titleId);
    if (!t.seasons) return { title: t.name, mins: t.mins ?? 100, still: t.art.backdrop, synopsis: t.synopsis };
    return t.seasons.find(s => s.n === c.season).episodes[c.episode - 1];
  }

  function draw() {
    if (!cur) { groundEl.replaceChildren(); return; }
    let src, key, fb = {};
    if (cur.kind === 'vod') { const ep = episodeOf(cur); src = ep.still ?? titleById(cur.titleId).art.backdrop; key = cur.titleId; fb = { title: titleById(cur.titleId).name }; }
    else { const p = programme(); src = p.art.backdrop; key = p.art.key; fb = { title: p.noInfo ? null : p.title, channel: channelById(cur.channelId) }; }
    // The picture: keep the old one until the new one has decoded, then crossfade (never an empty frame).
    const pictures = [...groundEl.querySelectorAll('.pic')];
    const top = pictures.at(-1);
    if (!top || top.dataset.src !== String(src ?? key)) {
      const img = art(src, key, 'kb pic', fb);
      img.dataset.src = String(src ?? key);
      groundEl.prepend(...pictures, img); // pictures stay first, in order, under the extras
      const show = () => requestAnimationFrame(() => {
        img.classList.add('on');
        setTimeout(() => pictures.forEach(p => p.remove()), 520);
      });
      if (img.decode) img.decode().then(show, show); else show();
    }
    // The extras (subtitles, pause mark) sit above the picture.
    groundEl.querySelectorAll('.subtitle-line, .paused-mark').forEach(e => e.remove());
    if (subtitles) groundEl.append(h('div', { class: 'subtitle-line', text: cur.kind === 'live' ? '[ANNOUNCER] Good evening, and welcome.' : '— I think we should go back. — We can’t.' }));
    if (paused) groundEl.append(h('div', { class: 'paused-mark', text: '❚❚' }));
  }

  function programme(at = ctx.now() - (cur?.behindMs ?? 0)) {
    return programmeAt(cur.channelId, at);
  }

  const api = {
    get current() { return cur; },
    get paused() { return paused; },
    get subtitles() { return subtitles; },
    episodeOf,
    programme,
    onChange(fn) { listeners.push(fn); },
    playVod(titleId, season, episode, posMs = 0) {
      const t = titleById(titleId);
      const ep = t.seasons ? t.seasons.find(s => s.n === season).episodes[episode - 1] : null;
      const durMs = (ep ? ep.mins : t.mins ?? 100) * 60_000;
      cur = { kind: 'vod', titleId, season, episode, posMs, durMs };
      paused = false;
      draw();
      listeners.forEach(fn => fn('play'));
    },
    playLive(channelId) {
      cur = { kind: 'live', channelId, behindMs: 0 };
      paused = false;
      draw();
      listeners.forEach(fn => fn('play'));
    },
    stop() { cur = null; draw(); },
    togglePause() {
      if (!cur) return;
      paused = !paused;
      if (paused) pausedAt = ctx.now();
      else if (cur.kind === 'live') cur.behindMs += ctx.now() - pausedAt;
      draw();
    },
    seek(deltaMs) {
      if (!cur) return;
      if (cur.kind === 'vod') cur.posMs = Math.max(0, Math.min(cur.durMs - 1000, cur.posMs + deltaMs));
      else cur.behindMs = Math.max(0, Math.min(3 * 3600_000, cur.behindMs - deltaMs)); // 3 h time-shift buffer
      draw();
    },
    /** Live only: watch `ms` behind live (catch-up from a programme's start). */
    setBehind(ms) {
      if (cur?.kind !== 'live') return;
      cur.behindMs = Math.max(0, Math.min(3 * 3600_000, ms));
      draw();
    },
    startOver() {
      if (cur?.kind === 'live') { const p = programme(); cur.behindMs = ctx.now() - p.start; draw(); }
      else if (cur) { cur.posMs = 0; draw(); }
    },
    toggleSubtitles() { subtitles = !subtitles; draw(); return subtitles; },
    /** Debug: jump to 10 s before the end (on-demand) or 4 min before the programme ends (live). */
    ff() {
      if (!cur) return;
      if (cur.kind === 'vod') cur.posMs = cur.durMs - 10_000;
      else { const p = programme(); ctx.shiftClock(p.end - 4 * 60_000 - (ctx.now() - cur.behindMs)); }
      draw();
      listeners.forEach(fn => fn('tick'));
    },
    channelIndex() { return channels.findIndex(c => c.id === cur?.channelId); },
    channel() { return cur?.kind === 'live' ? channelById(cur.channelId) : null; },
    tick() {
      const now = ctx.now();
      const dt = now - lastTick;
      lastTick = now;
      if (!cur || paused) return;
      if (cur.kind === 'vod') {
        cur.posMs = Math.min(cur.durMs, cur.posMs + dt);
        if (cur.posMs >= cur.durMs) listeners.forEach(fn => fn('ended'));
      }
      const before = groundEl.dataset.prog;
      if (cur.kind === 'live') {
        const p = programme();
        if (before !== p.id) { groundEl.dataset.prog = p.id; draw(); }
      }
      listeners.forEach(fn => fn('tick'));
    },
  };
  return api;
}
