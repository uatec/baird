import { test } from 'node:test';
import assert from 'node:assert/strict';
import { catalogue, channels, scheduleFor, upNext, onNow, watchlist, discover, bestSource, titleById } from '../js/data.js';

const H = 3600_000;
const at = (h, m = 0, day = 25) => new Date(2026, 9, day, h, m).getTime();

test('every channel schedule is contiguous and covers now-3h..now+24h', () => {
  const now = at(18, 10);
  for (const c of channels) {
    const s = scheduleFor(c.id, now);
    assert.ok(s[0].start <= now - 3 * H, `${c.id} starts too late`);
    assert.ok(s.at(-1).end >= now + 24 * H, `${c.id} ends too early`);
    for (let i = 0; i < s.length - 1; i++) assert.equal(s[i].end, s[i + 1].start, `${c.id} gap at ${i}`);
  }
});

test('around midnight exactly one programme per channel contains now', () => {
  const now = at(23, 50);
  for (const c of channels) {
    const live = scheduleFor(c.id, now).filter(p => p.start <= now && now < p.end);
    assert.equal(live.length, 1, c.id);
  }
});

test('fixed slots: The Simpsons on Channel 4 at 18:00, EastEnders on BBC One at 19:00', () => {
  const now = at(18, 10);
  const simpsons = scheduleFor('c4', now).find(p => p.start === at(18, 0));
  assert.equal(simpsons.title, 'The Simpsons');
  assert.equal(simpsons.end, at(18, 30));
  assert.equal(simpsons.titleId, 'simpsons');
  const ee = scheduleFor('bbc1', now).find(p => p.title === 'EastEnders' && p.start === at(19, 0));
  assert.ok(ee);
});

test('schedules are deterministic for the same day', () => {
  const a = scheduleFor('bbc2', at(12)).map(p => p.title + p.start);
  const b = scheduleFor('bbc2', at(12, 5)).map(p => p.title + p.start);
  assert.deepEqual(a.slice(1, 10), b.slice(1, 10));
});

test('up next: Loki S1 E4 first, Casper half-watched, Severance requested at 62%', () => {
  const cards = upNext(at(18, 10));
  assert.equal(cards[0].titleId, 'loki');
  assert.equal(cards[0].season, 1);
  assert.equal(cards[0].episode, 4);
  const casper = cards.find(c => c.titleId === 'casper');
  assert.ok(casper.progress > 0.4 && casper.progress < 0.6);
  const sev = cards.find(c => c.titleId === 'severance');
  assert.equal(sev.badge, 'requested');
  assert.equal(sev.progress, 0.62);
});

test('best source prefers Jellyfin', () => {
  assert.equal(bestSource(titleById('loki')).service, 'jellyfin');
});

test('on now has at least 10 live channels with progress', () => {
  const cards = onNow(at(18, 10));
  assert.ok(cards.length >= 10);
  for (const c of cards) {
    assert.equal(c.type, 'channel');
    assert.ok(c.progress >= 0 && c.progress <= 1);
  }
});

test('watchlist and discover are non-empty and every title has art', () => {
  assert.ok(watchlist().length >= 4);
  assert.ok(discover().some(c => c.badge === 'get'));
  for (const t of catalogue) assert.ok(t.art.backdrop, t.id);
});

import { showingsOf, watchlistCards, channelById as chanById } from '../js/data.js';

test('every series has seasons with episodes (Andor has two seasons)', () => {
  for (const t of catalogue.filter(x => x.kind === 'series')) assert.ok(t.seasons?.length >= 1 && t.seasons[0].episodes.length >= 1, t.id);
  assert.equal(titleById('andor').seasons.length, 2);
  assert.equal(titleById('andor').seasons[0].episodes[0].title, 'Kassa');
});

test('the schedule can cover 7 days ahead and 7 days back', () => {
  const now = at(18, 10);
  const s = scheduleFor('bbc1', now, { back: 7 * 24 * H, ahead: 7 * 24 * H });
  assert.ok(s[0].start <= now - 7 * 24 * H && s.at(-1).end >= now + 7 * 24 * H);
});

test('all showings of a programme span channels and days, with catch-up and repeats marked', () => {
  const now = at(18, 10);
  const all = showingsOf('The Simpsons', now);
  assert.ok(all.some(x => x.channelId === 'c4') && all.some(x => x.channelId === 'sky'), 'more than one channel');
  assert.ok(all.some(x => x.when === 'catchup') && all.some(x => x.when === 'later'));
  assert.ok(all.some(x => x.repeat), 'some showings are repeats');
  for (let i = 1; i < all.length; i++) assert.ok(all[i - 1].start <= all[i].start, 'sorted by time');
});

test('a channel with no guide data has no programmes, only an empty placeholder', () => {
  const c = chanById('tpt');
  assert.ok(c && c.noEpg);
  const s = scheduleFor('tpt', at(18, 10));
  assert.equal(s.length, 1);
  assert.equal(s[0].noInfo, true);
});

test('some programmes have no artwork so the fallbacks show', () => {
  const s = scheduleFor('bbc1', at(18, 10));
  assert.ok(s.some(p => !p.art.backdrop));
  for (const c of channels) assert.match(c.colour, /^#[0-9a-f]{6}$/i, `${c.id} has a brand colour`);
});

test('watchlist entries can be a title, a season or an episode, and say so', () => {
  const cards = watchlistCards([
    { kind: 'title', titleId: 'shogun' },
    { kind: 'season', titleId: 'andor', season: 2 },
    { kind: 'episode', titleId: 'doctor-who', season: 3, episode: 10 },
  ]);
  assert.equal(cards[0].scope, null);
  assert.equal(cards[1].scope, 'Season 2');
  assert.equal(cards[2].scope, 'S3 E10');
});

import { availabilitySummary, requestSeasons, completeRequest, seasonState } from '../js/data.js';

test('availability is per season: partial titles say what streams and what can be requested', () => {
  const st = titleById('stranger-things');
  assert.equal(st.availability, 'partial');
  assert.equal(availabilitySummary(st), 'Seasons 1–2 on Netflix · Season 3 can be requested');
  assert.equal(availabilitySummary(titleById('the-crown')), 'On Netflix');
  assert.equal(availabilitySummary(titleById('slow-horses')), 'Not available to stream');
});

test('requesting only the missing seasons, then the download landing on Jellyfin', () => {
  const t = titleById('andor');
  requestSeasons(t, [2]);
  assert.equal(seasonState(t, 2), 'requested');
  assert.equal(seasonState(t, 1), 'available', 'season 1 untouched');
  completeRequest(t, 2);
  assert.equal(seasonState(t, 2), 'available');
  assert.equal(t.availability, 'play');
});

import { episodeState, requestEpisode, missingSeasons } from '../js/data.js';

test('a single episode can be requested, and lands on its own', () => {
  const t = titleById('slow-horses');
  assert.equal(episodeState(t, 2, 3), 'missing');
  requestEpisode(t, 2, 3);
  assert.equal(episodeState(t, 2, 3), 'requested');
  assert.equal(episodeState(t, 2, 4), 'missing', 'only that episode');
  completeRequest(t, '2.3');
  assert.equal(episodeState(t, 2, 3), 'available');
  assert.equal(seasonState(t, 2), 'missing', 'the rest of the season is still missing');
  assert.match(availabilitySummary(t), /1 episode of season 2 on Jellyfin/);
});

test('missingSeasons lists every season you could request', () => {
  assert.deepEqual(missingSeasons(titleById('the-last-of-us')), [1, 2]);
  assert.deepEqual(missingSeasons(titleById('stranger-things')), [3]);
});
