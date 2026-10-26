import { test } from 'node:test';
import assert from 'node:assert/strict';
import { searchAll } from '../js/search.js';

const now = new Date(2026, 9, 25, 18, 10).getTime();

test('simpsons: the show is the playable top result, and the Channel 4 18:00 broadcast is listed', () => {
  const r = searchAll('simpsons', now);
  assert.equal(r.top.ref.id, 'simpsons');
  assert.equal(r.top.badge, 'play');
  const c4 = r.onTv.find(x => x.ref.channelId === 'c4' && x.ref.start === new Date(2026, 9, 25, 18, 0).getTime());
  assert.ok(c4, 'Channel 4 18:00 listing');
  assert.equal(c4.badge, 'live', 'it is on now at 18:10');
  const later = searchAll('simpsons', new Date(2026, 9, 25, 18, 40).getTime());
  assert.equal(later.onTv.find(x => x.ref.channelId === 'c4' && x.ref.start === c4.ref.start).badge, 'catchup');
});

test('titles you do not have carry requested/get badges', () => {
  assert.equal(searchAll('sever', now).top.badge, 'requested');
  assert.equal(searchAll('dune', now).top.badge, 'get');
});

test('a channel number finds the channel', () => {
  const r = searchAll('104', now);
  assert.equal(r.channels[0].ref.id, 'c4');
});

test('matching is on word prefixes, case-insensitive', () => {
  assert.ok(searchAll('LAST of', now).titles.some(t => t.ref.id === 'the-last-of-us'));
  assert.ok(!searchAll('impsons', now).top, 'mid-word does not match');
});

test('an empty query returns nothing', () => {
  const r = searchAll('  ', now);
  assert.equal(r.top, null);
  assert.deepEqual([r.titles.length, r.channels.length, r.onTv.length], [0, 0, 0]);
});

test('On TV lists each channel once (its next relevant showing), so one channel cannot crowd out the rest', () => {
  const r = searchAll('simpsons', new Date(2026, 9, 26, 0, 33).getTime());
  const chans = r.onTv.map(x => x.ref.channelId);
  assert.equal(new Set(chans).size, chans.length);
  assert.ok(chans.includes('c4'), 'Channel 4 18:00 is listed');
});
