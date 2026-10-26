import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mapKey, createHoldDetector, numberEntry } from '../js/keys.js';

const k = (key, code = '') => mapKey({ key, code });

test('mapKey maps keyboard stand-ins and remote codes to actions', () => {
  assert.equal(k('Enter'), 'ok');
  for (const b of ['Escape', 'Backspace', 'BrowserBack']) assert.equal(k(b), 'back', b);
  for (const h of ['h', 'BrowserHome', 'HomePage']) assert.equal(k(h), 'home', h);
  for (const g of ['g', 'ContextMenu']) assert.equal(k(g), 'guide', g);
  assert.equal(k(' '), 'playpause');
  assert.equal(k('MediaPlayPause'), 'playpause');
  assert.equal(k('['), 'prev');
  assert.equal(k('MediaTrackPrevious'), 'prev');
  assert.equal(k(']'), 'next');
  assert.equal(k('MediaTrackNext'), 'next');
  assert.equal(k('PageUp'), 'pageup');
  assert.equal(k('ChannelUp'), 'pageup');
  assert.equal(k('PageDown'), 'pagedown');
  assert.equal(k('Delete'), 'del');
  assert.equal(k('v'), 'voice');
  assert.equal(k('7'), 'digit:7');
  assert.equal(k('f'), 'debug-ff');
  assert.equal(k('k'), 'debug-keys');
  assert.equal(k('?'), 'help');
  assert.equal(k('r'), 'debug-reminder');
  assert.equal(k('ArrowUp'), 'up');
  assert.equal(k('q'), null);
});

test('hold detector: short press is ok, long press fires hold-ok once', () => {
  let t = 0;
  const d = createHoldDetector({ holdMs: 500, now: () => t });
  d.down();
  t = 200;
  assert.equal(d.up(), 'ok');

  t = 1000;
  d.down();
  t = 1520;
  assert.equal(d.tick(), 'hold-ok');
  assert.equal(d.tick(), null);
  assert.equal(d.up(), null);
});

const chans = [
  { id: 'bbc1', number: 101, name: 'BBC One' },
  { id: 'c4', number: 104, name: 'Channel 4' },
];

test('numberEntry finds a channel by typed number', () => {
  const n = numberEntry(chans);
  n.push(1); n.push(0);
  const r = n.push(4);
  assert.equal(r.text, '104');
  assert.equal(r.match.id, 'c4');
  assert.equal(r.error, null);
});

test('numberEntry reports a missing channel without matching', () => {
  const n = numberEntry(chans);
  n.push(9); n.push(9);
  const r = n.push(9);
  assert.equal(r.match, null);
  assert.equal(r.error, 'No channel 999');
});

test('numberEntry del removes the last digit', () => {
  const n = numberEntry(chans);
  n.push(1); n.push(0);
  assert.equal(n.del().text, '1');
});
