// Behaviour regressions from the final review, checked in a real browser over CDP (port 9333, never 9222).
// Usage: node tools/regress.mjs [base-url]   → prints PASS/FAIL per check, exit 1 on any failure.
import { spawn } from 'node:child_process';

const BASE = process.argv[2] ?? 'http://100.109.40.12:8077/';
const PORT = 9333;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const chrome = spawn('google-chrome-stable', ['--headless=new', `--remote-debugging-port=${PORT}`, '--window-size=1920,1080',
  `--user-data-dir=/tmp/baird-regress-${process.pid}`, 'about:blank'], { stdio: 'ignore' });
process.on('exit', () => chrome.kill());

let ws, id = 0;
const pending = new Map();
const send = (method, params = {}) => new Promise(res => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
const js = async expr => (await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })).result?.value;
const KEYS = { Enter: 13, Escape: 27, ArrowUp: 38, ArrowDown: 40, ArrowLeft: 37, ArrowRight: 39, PageUp: 33, PageDown: 34, Delete: 46 };
async function key(k, hold = 0) {
  const base = { key: k, code: k.length === 1 ? (/\d/.test(k) ? `Digit${k}` : `Key${k.toUpperCase()}`) : k, windowsVirtualKeyCode: KEYS[k] ?? k.toUpperCase().charCodeAt(0) };
  await send('Input.dispatchKeyEvent', { type: 'keyDown', ...base });
  if (hold) await sleep(hold);
  await send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
  await sleep(250);
}
async function open(url, ua) {
  if (ua) await send('Emulation.setUserAgentOverride', ua);
  await send('Page.navigate', { url: 'about:blank' }); // a hash-only change would not reload the app
  await sleep(200);
  await send('Page.navigate', { url });
  await sleep(1800);
}
const litInTop = `(() => { const ov = document.querySelector('.layer-overlay > *'); const pg = document.querySelector('.layer-page > *');
  const pl = document.querySelector('.layer-place'); const top = ov ?? pg ?? (pl.style.display !== 'none' ? pl : null);
  const tabs = ov || pg ? 0 : document.querySelectorAll('.tabbar.on .tab.focused').length;
  return top ? top.querySelectorAll('.focused').length + tabs : -1; })()`;

const results = [];
async function check(name, fn) {
  try { const r = await fn(); results.push([r === true ? 'PASS' : 'FAIL', name, r === true ? '' : JSON.stringify(r)]); }
  catch (e) { results.push(['FAIL', name, e.message]); }
}

async function main() {
  await sleep(1500);
  const targets = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json();
  ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
  await new Promise(r => ws.addEventListener('open', r));
  ws.addEventListener('message', e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m); pending.delete(m.id); } });
  await send('Page.enable'); await send('Runtime.enable');

  await check('#1 Pg+ twice changes channel twice (overlay up after the first)', async () => {
    await open(`${BASE}#play-live=bbc1`);
    await key('PageUp'); await key('PageUp');
    const ch = await js('__baird.player.current.channelId');
    return ch === 'itv1' || { ch };
  });
  await check('#1 digits still tune while the info bar is up', async () => {
    await open(`${BASE}?hold#play-live=bbc1`);
    await key('Enter'); await key('1'); await key('0'); await key('4'); await key('Enter');
    const ch = await js('__baird.player.current.channelId');
    return ch === 'c4' || { ch };
  });
  await check('#2 catch-up plays the chosen earlier programme from its start', async () => {
    await open(`${BASE}`);
    const r = await js(`(async () => {
      const { scheduleFor } = await import('./js/data.js');
      const now = __baird.ctx.now();
      const p = scheduleFor('c4', now).filter(x => x.end <= now - 30 * 60000).at(-1);
      __baird.ctx.playCatchup(p);
      const playing = __baird.player.programme();
      return { want: p.id, got: playing.id };
    })()`);
    return r.want === r.got || r;
  });
  await check('#3 OK on the playing title in Up next returns to it without restarting', async () => {
    await open(`${BASE}`);
    await key('Enter'); await sleep(300);
    await js('__baird.player.seek(30 * 60000)');
    await key('h'); await sleep(400);
    await key('Enter'); await sleep(300);
    const r = await js('({ pos: __baird.player.current.posMs, top: __baird.nav.top() })');
    return (r.pos > 29 * 60000 && r.top === 'video') || r;
  });
  await check('#3 leaving a film part-way saves the resume point', async () => {
    await open(`${BASE}#show=casper`);
    await key('Enter'); await sleep(300);
    await js('__baird.player.seek(10 * 60000)');
    await key('h'); await sleep(400);
    const mins = await js(`(async () => (await import('./js/data.js')).titleById('casper').progress.positionMins)()`);
    return mins >= 61 || { mins };
  });
  await check('#4 no "NaN" after playing a series with no episode data', async () => {
    await open(`${BASE}#show=stranger-things`);
    await key('Enter'); await sleep(300); await key('h'); await sleep(600);
    const body = await js('document.body.innerText');
    return !body.includes('NaN') || 'NaN on screen';
  });
  await check('#5 guide opened on a long live programme shows the NOW line', async () => {
    await open(`${BASE}`);
    const r = await js(`(async () => {
      const { channels, scheduleFor } = await import('./js/data.js');
      // Film4's Alien runs 21:00–23:00 every day: move the clock to 22:50 so it started 110 min ago.
      const d = new Date(__baird.ctx.now()); const target = new Date(d.getFullYear(), d.getMonth(), d.getDate(), 22, 50).getTime();
      __baird.ctx.shiftClock(target - __baird.ctx.now());
      __baird.ctx.openPage('guide', { channelId: 'film4' });
      await new Promise(r => setTimeout(r, 600));
      return getComputedStyle(document.querySelector('.guide-now')).opacity;
    })()`);
    return r === '1' || { opacity: r };
  });
  await check('#6 search: no results never leaves nothing lit or focus stuck', async () => {
    await open(`${BASE}#search=simpsons`);
    for (let i = 0; i < 10; i++) await key('ArrowRight');
    await key('9'); await key('9'); await sleep(400);
    const a = await js(litInTop);
    await key('ArrowRight'); await key('ArrowLeft');
    const b = await js(litInTop);
    return (a === 1 && b === 1) || { a, b };
  });
  await check('#7 V on Search does not stack a second Search', async () => {
    await open(`${BASE}#search`);
    await key('v'); await sleep(1500);
    const r = await js('({ pages: __baird.nav.state().pages.map(p => p.name).join(","), place: __baird.nav.state().place })');
    return (r.pages === '' && r.place === 'search') || r;
  });
  await check('#7 G with actions open over the guide does not stack a second guide', async () => {
    await open(`${BASE}?hold#guide`);
    await key('Enter', 650); await key('g');
    const r = await js('({ pages: __baird.nav.state().pages.map(p => p.name).join(","), place: __baird.nav.state().place })');
    return (r.pages === '' && r.place === 'guide') || r;
  });
  await check('#9 Home cards are not rebuilt by the background refresh', async () => {
    await open(`${BASE}`);
    await js(`window.__card = document.querySelector('.layer-place .home .card'); 1`);
    await sleep(5600);
    return (await js(`window.__card.isConnected`)) === true || 'first card was replaced';
  });
  await check('#9 Search and Show keep their cards when focus moves', async () => {
    await open(`${BASE}#show=loki`);
    await js(`window.__c = document.querySelector('.show-eps .card'); 1`);
    await key('ArrowDown'); await key('ArrowDown'); await key('ArrowRight');
    const showOk = await js('window.__c?.isConnected');
    await open(`${BASE}#search=simpsons`);
    await js(`window.__t = document.querySelector('.top-card'); 1`);
    for (let i = 0; i < 10; i++) await key('ArrowRight');
    const searchOk = await js('window.__t?.isConnected');
    return (showOk && searchOk) || { showOk, searchOk };
  });


  // ---------------------------------------------------------------- revision 2
  await check('R2 tabs: ▴ lights the tab bar, ▸ switches the place without OK', async () => {
    await open(`${BASE}`);
    await key('ArrowUp'); await key('ArrowRight');
    const r = await js('({ place: __baird.nav.state().place, lit: !!document.querySelector(".tabbar .tab.focused[data-place=guide]") })');
    return (r.place === 'guide' && r.lit) || r;
  });
  await check('R2 tabs: Back from inside a place lights its tab, Back again goes Home', async () => {
    await open(`${BASE}#guide`);
    await key('ArrowDown'); await key('Escape');
    const a = await js('({ place: __baird.nav.state().place, lit: !!document.querySelector(".tabbar .tab.focused") })');
    await key('Escape');
    const b = await js('__baird.nav.state().place');
    return (a.place === 'guide' && a.lit && b === 'home') || { a, b };
  });
  await check('R2 tabs: the tab bar is visible on Settings', async () => {
    await open(`${BASE}#settings`);
    return (await js('document.querySelector(".tabbar").classList.contains("on")')) === true || 'tab bar hidden';
  });
  await check('R2 remind me: OK on a future programme sets a reminder; a minute before, a countdown card asks', async () => {
    await open(`${BASE}#guide=bbc1`);
    await key('ArrowRight'); await key('ArrowRight'); await key('Enter');
    const n = await js('__baird.state.reminders.size');
    await key('r'); await sleep(1300);
    const ov = await js('__baird.nav.state().overlay?.name');
    return (n === 1 && ov === 'reminder') || { n, ov };
  });
  await check('R2 guide actions: "All showings", no "This channel\'s schedule"', async () => {
    await open(`${BASE}?hold#guide=c4`);
    await key('Enter', 650);
    const items = await js('[...document.querySelectorAll(".menu-item")].map(e => e.textContent).join("|")');
    return (items.includes('All showings') && !items.includes("schedule")) || items;
  });
  await check('R2 all showings lists more than one channel and marks repeats', async () => {
    await open(`${BASE}?hold`);
    await js(`__baird.ctx.nav.openOverlay({ name: 'showings', params: { title: 'The Simpsons' } }); 1`);
    await sleep(500);
    const r = await js('({ rows: document.querySelectorAll(".sh-row").length, chans: new Set([...document.querySelectorAll(".sh-ch")].map(e => e.textContent.split(" ")[0])).size, repeat: document.body.innerText.includes("Repeat") })');
    return (r.rows > 3 && r.chans > 1 && r.repeat) || r;
  });
  await check('R2 Andor: two season tabs, season 2 can be requested', async () => {
    await open(`${BASE}#show=andor`);
    const r = await js('({ tabs: document.querySelectorAll(".season-tab").length, req: [...document.querySelectorAll(".btn")].some(b => b.textContent.includes("Request season 2")) })');
    return (r.tabs === 2 && r.req) || r;
  });
  await check('R2 channel list opens on the current channel, then moves one step', async () => {
    await open(`${BASE}?hold#play-live=bbc2`);
    await sleep(1800);
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'ArrowDown', code: 'ArrowDown', windowsVirtualKeyCode: 40 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'ArrowDown', code: 'ArrowDown', windowsVirtualKeyCode: 40 });
    await sleep(80);
    const first = await js('document.querySelector(".ch-row.focused .ch-name b")?.textContent');
    await sleep(500);
    const then = await js('document.querySelector(".ch-row.focused .ch-name b")?.textContent');
    return (first === '102' && then === '103') || { first, then };
  });
  await check('R2 ◂ on the bare live video scrubs backwards', async () => {
    await open(`${BASE}#play-live=bbc1`);
    await sleep(1800);
    await key('ArrowLeft');
    const b = await js('__baird.player.current.behindMs');
    return b >= 30_000 || { behindMs: b };
  });
  await check('R2 changing channel shows the tuning ident, then the picture', async () => {
    await open(`${BASE}#play-live=bbc1`);
    await sleep(2500);
    await key('PageUp');
    const during = await js('document.querySelector(".tune-layer").classList.contains("on")');
    await sleep(2600);
    const after = await js('document.querySelector(".tune-layer").classList.contains("on")');
    return (during && !after) || { during, after };
  });
  await check('R2 a channel with no guide data shows "No programme information" in On now', async () => {
    await open(`${BASE}`);
    return (await js('document.body.innerText.includes("No programme information")')) === true || 'missing';
  });
  await check('R2 search results never sit over the keyboard', async () => {
    await open(`${BASE}#search=the`);
    for (let i = 0; i < 10; i++) await key('ArrowRight');
    for (let i = 0; i < 4; i++) await key('ArrowRight');
    const minLeft = await js('Math.min(...[...document.querySelectorAll(".search-results .card, .search-results .top-card")].filter(e => { const r = e.getBoundingClientRect(); return r.width && r.bottom > 0 && r.top < innerHeight; }).map(e => e.getBoundingClientRect().left))');
    const kbRight = await js('document.querySelector(".kbd").getBoundingClientRect().right');
    return minLeft > kbRight || { minLeft, kbRight };
  });
  await check('R2 watchlist shows scoped entries (a season, an episode)', async () => {
    await open(`${BASE}`);
    const t = await js('document.body.innerText');
    return (t.includes('Season 3') && t.includes('S3 E10')) || 'no scope labels';
  });


  // ---------------------------------------------------------------- transitions: out and between, not just in
  await check('T1 switching tabs keeps the old place on screen until the new one has faded in', async () => {
    await open(`${BASE}#play-live=bbc1+home`);
    await sleep(2200);
    await key('ArrowUp'); await sleep(50);
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'ArrowRight', code: 'ArrowRight', windowsVirtualKeyCode: 39 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'ArrowRight', code: 'ArrowRight', windowsVirtualKeyCode: 39 });
    await sleep(60);
    const n = await js('document.querySelector(".layer-place").children.length');
    return n === 2 || { childrenDuringSwitch: n };
  });
  await check('T2 tuning from the channel list keeps the same list on screen (no close and reopen)', async () => {
    await open(`${BASE}#play-live=bbc1`);
    await sleep(2200);
    await key('ArrowDown'); await sleep(400);
    await js('window.__panel = document.querySelector(".ch-panel"); 1');
    await sleep(1500); // rests on the channel: it tunes
    const r = await js('({ same: window.__panel === document.querySelector(".ch-panel"), ch: __baird.player.current.channelId })');
    return (r.same && r.ch === 'bbc2') || r;
  });
  await check('T3 changing channel never leaves the picture area empty (old picture stays until the new one is ready)', async () => {
    await open(`${BASE}#play-live=bbc1`);
    await sleep(2200);
    await js(`window.__empty = 0; window.__obs = new MutationObserver(() => { if (!document.querySelector('.ground').children.length || !document.querySelector('.ground img.on, .ground .art-fallback')) window.__empty++; }); window.__obs.observe(document.querySelector('.ground'), { childList: true, subtree: true, attributes: true }); 1`);
    await key('PageUp'); await sleep(1200);
    return (await js('window.__empty')) === 0 || { emptyMoments: await js('window.__empty') };
  });
  await check('T4 closing an overlay animates it out', async () => {
    await open(`${BASE}?hold#play-live=bbc1`);
    await sleep(2200);
    await key('Enter'); await sleep(500);
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await sleep(60);
    const r = await js('({ n: document.querySelector(".layer-overlay").children.length, leaving: !!document.querySelector(".layer-overlay .leaving") })');
    return (r.n === 1 && r.leaving) || r;
  });
  await check('T5 opening a page keeps the page beneath visible until it has faded in', async () => {
    await open(`${BASE}`);
    await key('Enter', 0); // Loki → video; Back to Home, then open a show page from Watchlist
    await sleep(800); await key('Escape'); await sleep(600);
    await js(`__baird.ctx.openShow('andor'); 1`);
    await sleep(60);
    return (await js('document.querySelector(".layer-place").style.display')) !== 'none' || 'place hidden immediately';
  });


  // ---------------------------------------------------------------- the lit thing is always fully on screen
  const offScreen = `(() => {
    const st = document.getElementById('stage').getBoundingClientRect(), k = st.width / 1920;
    const ov = document.querySelector('.layer-overlay > :not(.leaving)'), pg = document.querySelector('.layer-page > :not(.leaving)');
    const pl = document.querySelector('.layer-place');
    const top = ov ?? pg ?? (pl.style.display !== 'none' ? pl : null);
    if (!top) return [];
    return [...top.querySelectorAll('.focused')].map(e => { const r = e.getBoundingClientRect(); return { t: (r.top - st.top) / k, b: (r.bottom - st.top) / k, l: (r.left - st.left) / k, r: (r.right - st.left) / k, c: e.className.slice(0, 40) }; })
      .filter(r => r.t < -2 || r.b > 1082 || r.l < -2 || r.r > 1922);
  })()`;
  await check('V1 Andor: every row of the show page keeps the lit item fully on screen', async () => {
    await open(`${BASE}#show=andor`);
    const bad = [];
    for (const k of ['ArrowDown', 'ArrowDown', 'ArrowRight', 'ArrowRight', 'ArrowUp', 'ArrowUp']) { await key(k); await sleep(500); const o = await js(offScreen); if (o.length) bad.push({ k, o }); }
    return bad.length === 0 || bad;
  });
  await check('V2 Home: lit posters in Watchlist and Discover are fully on screen', async () => {
    await open(`${BASE}`);
    const bad = [];
    for (const k of ['ArrowDown', 'ArrowDown', 'ArrowRight', 'ArrowDown']) { await key(k); await sleep(500); const o = await js(offScreen); if (o.length) bad.push({ k, o }); }
    return bad.length === 0 || bad;
  });
  await check('R3 OK on an unavailable episode offers "this episode", the season, and all missing seasons', async () => {
    await open(`${BASE}?hold#show=slow-horses`);
    await key('ArrowDown'); await key('ArrowDown'); await key('Enter'); await sleep(400);
    const items = await js('[...document.querySelectorAll(".menu-item")].map(e => e.textContent).join("|")');
    return (/This episode/.test(items) && /Season 1/.test(items) && /All missing seasons/.test(items)) || items;
  });
  await check('R3 one Request… button, and season tabs show a state (not "Request")', async () => {
    await open(`${BASE}#show=slow-horses`);
    const r = await js('({ btns: [...document.querySelectorAll(".show-btns .btn")].map(b => b.textContent), tabs: [...document.querySelectorAll(".season-tab .st-src")].map(e => e.textContent) })');
    return (r.btns.filter(b => b.includes('Request')).length === 1 && !r.tabs.includes('Request')) || r;
  });

  await check('R4 a requested episode shows its download percentage, updating in place', async () => {
    await open(`${BASE}#show=slow-horses`);
    await key('ArrowDown'); await key('ArrowDown'); await key('Enter'); await sleep(300); await key('Enter'); await sleep(1200);
    const a = await js('document.querySelector(".show-eps .ring[data-dl]")?.textContent');
    await js('window.__ring = document.querySelector(".show-eps .ring[data-dl]"); 1');
    await sleep(2500);
    const b = await js('({ text: window.__ring?.textContent, same: window.__ring === document.querySelector(".show-eps .ring[data-dl]") })');
    return (a && /%$/.test(a) && b.same && b.text !== a) || { a, b };
  });

  for (const [s, n, d] of results) console.log(`${s}  ${n}${d ? `  — ${d}` : ''}`);
  const failed = results.filter(r => r[0] === 'FAIL').length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  ws.close(); chrome.kill();
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); chrome.kill(); process.exit(2); });
