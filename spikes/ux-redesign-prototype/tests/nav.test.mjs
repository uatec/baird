import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createNavigator } from '../js/nav.js';

const page = name => ({ name, params: {} });
const ov = (name, params = {}) => ({ name, params });
const topName = nav => { const t = nav.top(); return typeof t === 'string' ? t : t.name; };

test('Back over the video closes the overlay and returns to the video', () => {
  const nav = createNavigator();
  nav.play({ channelId: 'bbc1' }, 'live');
  nav.openOverlay(ov('channels'));
  assert.equal(topName(nav), 'channels');
  nav.back();
  assert.equal(nav.top(), 'video');
});

test('Back steps through the page hierarchy to Home', () => {
  const nav = createNavigator();
  nav.openPage(page('search'));
  nav.openPage(page('show'));
  nav.back();
  assert.equal(topName(nav), 'search');
  nav.back();
  assert.equal(nav.top(), 'home');
});

test('Back from the video retraces the journey to the page it was started from', () => {
  const nav = createNavigator();
  nav.openPage(page('search'));
  nav.openPage(page('show'));
  nav.play({ titleId: 'loki' }, 'vod');
  assert.equal(nav.top(), 'video');
  nav.back();
  assert.equal(topName(nav), 'show');
  assert.ok(nav.state().video, 'video keeps playing underneath');
  nav.back();
  assert.equal(topName(nav), 'search');
  nav.back();
  assert.equal(nav.top(), 'home');
});

test('Back from a video started on Home goes Home and keeps the video', () => {
  const nav = createNavigator();
  nav.play({ titleId: 'loki' }, 'vod');
  nav.back();
  assert.equal(nav.top(), 'home');
  assert.ok(nav.state().video);
});

test('Back on Home with nothing above it does nothing', () => {
  const nav = createNavigator();
  let changes = 0;
  nav.onChange(() => changes++);
  nav.back();
  assert.equal(nav.top(), 'home');
  assert.equal(changes, 0);
});

test('Guide opened over the video: Back returns to the video', () => {
  const nav = createNavigator();
  nav.play({ channelId: 'bbc1' }, 'live');
  nav.openPage(page('guide'));
  nav.back();
  assert.equal(nav.top(), 'video');
});

test('home() clears pages and overlays and keeps the video', () => {
  const nav = createNavigator();
  nav.play({ channelId: 'bbc1' }, 'live');
  nav.openPage(page('guide'));
  nav.home();
  assert.equal(nav.top(), 'home');
  assert.ok(nav.state().video);
  nav.back();
  assert.equal(nav.top(), 'home');
});

test('actions overlay over a page closes back to that page', () => {
  const nav = createNavigator();
  nav.openPage(page('guide'));
  nav.openOverlay(ov('actions'));
  assert.equal(topName(nav), 'actions');
  nav.back();
  assert.equal(topName(nav), 'guide');
});

test('playing from the video layer (channel change) keeps the original origin pages', () => {
  const nav = createNavigator();
  nav.openPage(page('search'));
  nav.play({ channelId: 'bbc1' }, 'live');
  nav.play({ channelId: 'bbc2' }, 'live'); // tuned from the channel list on the video
  nav.back();
  assert.equal(topName(nav), 'search');
});

test('stop() drops the video and returns to the pages it was started from', () => {
  const nav = createNavigator();
  nav.openPage(page('search'));
  nav.play({ titleId: 'casper' }, 'vod');
  nav.stop();
  assert.equal(nav.state().video, null);
  assert.equal(topName(nav), 'search');
});

test('places: switching tabs changes the place; Back from a place goes Home', () => {
  const nav = createNavigator();
  nav.setPlace('search');
  assert.equal(nav.top(), 'search');
  nav.setPlace('guide');
  assert.equal(nav.top(), 'guide');
  nav.back();
  assert.equal(nav.top(), 'home');
});

test('places: a page opened from a place backs out to that place', () => {
  const nav = createNavigator();
  nav.setPlace('search');
  nav.openPage(page('show'));
  nav.back();
  assert.equal(nav.top(), 'search');
});

test('places: a video started from the guide place backs out to the guide', () => {
  const nav = createNavigator();
  nav.setPlace('guide');
  nav.play({ channelId: 'bbc1' }, 'live');
  assert.equal(nav.top(), 'video');
  nav.back();
  assert.equal(nav.top(), 'guide');
  assert.ok(nav.state().video);
});

test('places: home() returns to the Home place', () => {
  const nav = createNavigator();
  nav.setPlace('settings');
  nav.openPage(page('show'));
  nav.home();
  assert.equal(nav.top(), 'home');
  assert.equal(nav.state().place, 'home');
});

test('play(..., { keepOverlay }) changes what is playing without closing the overlay (tuning from the channel list)', () => {
  const nav = createNavigator();
  nav.play({ channelId: 'bbc1' }, 'live');
  const list = ov('channels');
  nav.openOverlay(list);
  nav.play({ channelId: 'bbc2' }, 'live', { keepOverlay: true });
  assert.equal(nav.top(), list);
  assert.equal(nav.state().video.ref.channelId, 'bbc2');
});
