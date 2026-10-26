// Real-time walk-through of the judging journey: drives headless Chrome over CDP with real key events,
// screenshots each step into /tmp/baird-walk/, and checks exactly one thing is lit in the top layer.
// Usage: node tools/walkthrough.mjs [url]       (needs google-chrome-stable; uses CDP port 9333)
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';

const URL = process.argv[2] ?? 'http://100.109.40.12:8077/';
const OUT = '/tmp/baird-walk';
const PORT = 9333;
const sleep = ms => new Promise(r => setTimeout(r, ms));

const extra = process.env.SLOW ? ['--disable-gpu', '--disable-software-rasterizer'] : [];
const chrome = spawn('google-chrome-stable', [...extra, '--headless=new', `--remote-debugging-port=${PORT}`, '--window-size=1920,1080', '--hide-scrollbars',
  '--user-data-dir=/tmp/baird-walk-profile', '--autoplay-policy=no-user-gesture-required', 'about:blank'], { stdio: 'ignore' });
process.on('exit', () => chrome.kill());

let ws, id = 0;
const pending = new Map();
const send = (method, params = {}) => new Promise(res => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
const evalJs = async expr => (await send('Runtime.evaluate', { expression: expr, returnByValue: true })).result?.value;

const KEYS = { Enter: 13, Escape: 27, ArrowUp: 38, ArrowDown: 40, ArrowLeft: 37, ArrowRight: 39, PageUp: 33, PageDown: 34, Delete: 46, ' ': 32 };
async function key(k, holdMs = 0) {
  const code = KEYS[k] ?? k.toUpperCase().charCodeAt(0);
  const base = { key: k, code: k.length === 1 ? `Key${k.toUpperCase()}` : k, windowsVirtualKeyCode: code };
  await send('Input.dispatchKeyEvent', { type: 'keyDown', ...base, text: k.length === 1 ? k : undefined });
  if (holdMs) await sleep(holdMs);
  await send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
  await sleep(380);
}
const keys = async (...ks) => { for (const k of ks) await key(k); };

let step = 0;
const problems = [];
async function shot(name) {
  await sleep(450);
  const lit = await evalJs(`(() => {
    const ov = document.querySelector('.layer-overlay > *');
    const pg = document.querySelector('.layer-page > *');
    const pl = document.querySelector('.layer-place');
    const top = ov ?? pg ?? (pl.style.display !== 'none' ? pl : null);
    const tabs = ov || pg ? 0 : document.querySelectorAll('.tabbar.on .tab.focused').length;
    return top ? top.querySelectorAll('.focused').length + tabs : -1;
  })()`);
  const off = await evalJs(`(() => {
    const st = document.getElementById('stage').getBoundingClientRect(), k = st.width / 1920;
    const ov = document.querySelector('.layer-overlay > :not(.leaving)'), pg = document.querySelector('.layer-page > :not(.leaving)');
    const pl = document.querySelector('.layer-place');
    const top = ov ?? pg ?? (pl.style.display !== 'none' ? pl : null);
    if (!top) return [];
    return [...top.querySelectorAll('.focused')].map(e => { const r = e.getBoundingClientRect(); return { t: (r.top - st.top) / k, b: (r.bottom - st.top) / k, l: (r.left - st.left) / k, r: (r.right - st.left) / k, c: e.className.slice(0, 40) }; })
      .filter(r => r.t < -2 || r.b > 1082 || r.l < -2 || r.r > 1922);
  })()`);
  if (off?.length) problems.push(`${name}: lit element off screen ${JSON.stringify(off)}`);
  const { data } = await send('Page.captureScreenshot', { format: 'png' });
  const file = `${OUT}/${String(++step).padStart(2, '0')}-${name}.png`;
  await writeFile(file, Buffer.from(data, 'base64'));
  const ok = lit === 1 || lit === -1 || (lit === 0 && /video|ended/.test(name));
  if (!ok) problems.push(`${name}: ${lit} lit elements`);
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${file}  (lit: ${lit === -1 ? 'bare video' : lit})`);
}
const title = () => evalJs(`document.querySelector('.guide-info .title')?.textContent ?? ''`);

async function typeOnKeyboard(word) {
  const ROWS = ['1234567890', 'qwertyuiop', "asdfghjkl'", 'zxcvbnm-&.'];
  for (const ch of word) {
    const cur = await evalJs(`(() => { const ks=[...document.querySelectorAll('.kbd .key')]; return ks.findIndex(k=>k.classList.contains('focused')); })()`);
    let r = Math.floor(cur / 10), c = cur % 10;
    const tr = ROWS.findIndex(x => x.includes(ch)), tc = ROWS[tr].indexOf(ch);
    while (r < tr) { await key('ArrowDown'); r++; }
    while (r > tr) { await key('ArrowUp'); r--; }
    while (c < tc) { await key('ArrowRight'); c++; }
    while (c > tc) { await key('ArrowLeft'); c--; }
    await key('Enter');
  }
}

async function main() {
  await mkdir(OUT, { recursive: true });
  await sleep(1500);
  const targets = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json();
  ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
  await new Promise(r => ws.addEventListener('open', r));
  ws.addEventListener('message', e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m); pending.delete(m.id); } });
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Page.navigate', { url: URL });
  await sleep(3500);
  await evalJs(`window.__long = []; new PerformanceObserver(l => l.getEntries().forEach(e => window.__long.push(Math.round(e.duration)))).observe({ type: 'longtask', buffered: false }); 1`);

  await shot('home');
  await key('Enter'); await sleep(800); await shot('video-loki-s1e4');
  await key('f'); await sleep(11500); await shot('endcard');
  await key(']'); await sleep(900); await shot('video-loki-s1e5');
  await key('Escape'); await sleep(700); await shot('home-now-playing');
  await key('ArrowDown'); await shot('home-on-now');
  await key('Enter'); await sleep(900); await shot('video-live');
  await key('ArrowDown'); await key('ArrowDown'); await shot('channel-list');
  await sleep(1600); await shot('channel-list-tuned');
  await key('Escape'); await key('Enter'); await sleep(700); await shot('info-bar');
  await key('g'); await sleep(900); await shot('guide');
  await keys('1', '0', '1'); await sleep(1200); await shot('guide-bbc-one');
  await key('g'); await key('ArrowRight'); await key('Enter'); await sleep(900);
  for (let i = 0; i < 8 && (await title()) !== 'EastEnders'; i++) await key('ArrowLeft');
  await shot(`guide-${(await title()).replace(/\W+/g, '-').toLowerCase()}`);
  await key('Enter', 650); await sleep(400); await shot('guide-actions');
  await key('Escape'); await key('Escape'); await sleep(500); await shot('back-to-video');
  await key('Escape'); await sleep(500); await shot('back-on-home');
  await key('h'); await sleep(700); await shot('home-again');
  await key('ArrowUp'); await shot('tab-bar-lit');
  await key('ArrowRight'); await sleep(500); await shot('tab-live-tv');
  await key('ArrowRight'); await sleep(500); await key('ArrowDown'); await sleep(400); await shot('search');
  await key('v'); await sleep(1800); await shot('search-voice-simpsons');
  for (let i = 0; i < 10; i++) await key('ArrowRight');
  await shot('search-results');
  await key('Enter'); await sleep(1200); await shot('show-simpsons');
  await key('Escape'); await sleep(500);
  for (let i = 0; i < 8; i++) await key('Delete');
  await key('ArrowLeft'); await sleep(300);
  await typeOnKeyboard('sever'); await sleep(900); await shot('search-sever');
  await key('h'); await sleep(600);
  await keys('ArrowDown', 'ArrowDown', 'ArrowDown'); await shot('home-discover');
  await key('Enter'); await sleep(1200); await shot('show-dune');
  await key('Enter'); await sleep(700); await shot('show-dune-requested');

  const long = await evalJs('window.__long');
  console.log(`Long tasks (>50 ms) during the walk: ${long.length}${long.length ? `, worst ${Math.max(...long)} ms, all: ${long.join(' ')}` : ''}`);
  console.log(`Lite mode: ${await evalJs("document.getElementById('stage').classList.contains('lite')")}`);
  console.log(problems.length ? `\nProblems:\n${problems.join('\n')}` : '\nEvery step had exactly one lit element (or the bare video).');
  ws.close();
  chrome.kill();
  process.exit(problems.length ? 1 : 0);
}
main().catch(e => { console.error(e); chrome.kill(); process.exit(2); });
