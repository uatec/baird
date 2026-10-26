// One-off: downloads real artwork from TMDB's public site/CDN into assets/art/ and writes assets/art/manifest.json.
// No API key: the TMDB web pages carry the image paths; the images come from image.tmdb.org.
// Usage: node tools/fetch-art.mjs   (re-running skips files that already exist)
import { writeFile, mkdir, readFile, access } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
const run = promisify(execFile);

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = join(ROOT, 'assets', 'art');
const UA = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130 Safari/537.36';

// key → TMDB search (type + query) or a fixed path, and which seasons need episode stills.
const TITLES = [
  { key: 'loki', path: 'tv/84958', seasons: [1] },
  { key: 'simpsons', path: 'tv/456', seasons: [6] },
  { key: 'casper', type: 'movie', q: 'Casper', year: 1995 },
  { key: 'planet-earth-3', type: 'tv', q: 'Planet Earth III', seasons: [1] },
  { key: 'severance', type: 'tv', q: 'Severance' },
  { key: 'stranger-things', type: 'tv', q: 'Stranger Things' },
  { key: 'the-crown', type: 'tv', q: 'The Crown' },
  { key: 'doctor-who', type: 'tv', q: 'Doctor Who 2005' },
  { key: 'blade-runner-2049', type: 'movie', q: 'Blade Runner 2049' },
  { key: 'paddington-2', type: 'movie', q: 'Paddington 2' },
  { key: 'dune-2', type: 'movie', q: 'Dune Part Two' },
  { key: 'slow-horses', type: 'tv', q: 'Slow Horses' },
  { key: 'andor', type: 'tv', q: 'Andor' },
  { key: 'the-last-of-us', type: 'tv', q: 'The Last of Us' },
  { key: 'sinners', type: 'movie', q: 'Sinners', year: 2025 },
  { key: 'alien', type: 'movie', q: 'Alien', year: 1979 },
  { key: 'the-bear', type: 'tv', q: 'The Bear' },
  { key: 'shogun', type: 'tv', q: 'Shogun' },
  // Broadcast programmes for the guide
  { key: 'eastenders', type: 'tv', q: 'EastEnders' },
  { key: 'gogglebox', type: 'tv', q: 'Gogglebox' },
  { key: 'match-of-the-day', type: 'tv', q: 'Match of the Day' },
  { key: 'grand-designs', type: 'tv', q: 'Grand Designs' },
  { key: 'gardeners-world', type: 'tv', q: "Gardeners' World" },
  { key: 'pointless', type: 'tv', q: 'Pointless' },
  { key: 'bbc-news', type: 'tv', q: 'BBC News at Six' },
  { key: 'the-one-show', type: 'tv', q: 'The One Show' },
  { key: 'question-time', type: 'tv', q: 'Question Time' },
  { key: 'house-of-games', type: 'tv', q: "Richard Osman's House of Games" },
  { key: 'emmerdale', type: 'tv', q: 'Emmerdale' },
  { key: 'coronation-street', type: 'tv', q: 'Coronation Street' },
  { key: 'hollyoaks', type: 'tv', q: 'Hollyoaks' },
  { key: 'family-guy', type: 'tv', q: 'Family Guy' },
  { key: 'top-gear', type: 'tv', q: 'Top Gear' },
  { key: 'taskmaster', type: 'tv', q: 'Taskmaster' },
  { key: 'qi', type: 'tv', q: 'QI' },
  { key: 'bluey', type: 'tv', q: 'Bluey' },
  { key: 'blue-peter', type: 'tv', q: 'Blue Peter' },
  { key: 'antiques-roadshow', type: 'tv', q: 'Antiques Roadshow' },
  { key: 'strictly', type: 'tv', q: 'Strictly Come Dancing' },
  { key: 'great-british-bake-off', type: 'tv', q: 'The Great British Bake Off' },
  { key: 'mock-the-week', type: 'tv', q: 'Have I Got News for You' },
  { key: 'police-interceptors', type: 'tv', q: 'Police Interceptors' },
  { key: 'only-connect', type: 'tv', q: 'Only Connect' },
  { key: 'mission-impossible', type: 'movie', q: 'Mission Impossible Fallout' },
  { key: 'jurassic-park', type: 'movie', q: 'Jurassic Park', year: 1993 },
];

const sleep = ms => new Promise(r => setTimeout(r, ms));
// curl, not fetch: TMDB's bot protection rejects Node's TLS fingerprint with 403.
async function curl(args) {
  return run('curl', ['-sSL', '--fail', '-m', '20', '-A', UA, '-H', 'Accept-Language: en-GB', ...args], { maxBuffer: 64 << 20, encoding: 'buffer' });
}
async function page(url) {
  await sleep(1200);
  return (await curl([url])).stdout.toString('utf8');
}
const exists = p => access(p).then(() => true, () => false);
async function download(path, size, file) {
  const dest = join(OUT, file);
  if (await exists(dest)) return file;
  await writeFile(dest, (await curl([`https://image.tmdb.org/t/p/${size}/${path}`])).stdout);
  return file;
}
const first = (html, re) => html.match(re)?.[1] ?? null;

async function resolvePath(t) {
  if (t.path) return t.path;
  const html = await page(`https://www.themoviedb.org/search?query=${encodeURIComponent(t.q)}`);
  const ids = [...html.matchAll(new RegExp(`href="/(${t.type}/\\d+)[^"]*"`, 'g'))].map(m => m[1]);
  if (!ids.length) throw new Error(`no ${t.type} result for ${t.q}`);
  if (t.year) {
    // Prefer the result whose card mentions the year.
    for (const id of [...new Set(ids)]) {
      const i = html.indexOf(`/${id}`);
      if (html.slice(i, i + 3000).includes(String(t.year))) return id;
    }
  }
  return ids[0];
}

async function main() {
  await mkdir(OUT, { recursive: true });
  const manifestPath = join(OUT, 'manifest.json');
  const manifest = (await exists(manifestPath)) ? JSON.parse(await readFile(manifestPath, 'utf8')) : {};
  for (const t of TITLES) {
    try {
      const path = await resolvePath(t);
      const html = await page(`https://www.themoviedb.org/${path}`);
      const backdrop = first(html, /t\/p\/w(?:1066_and_h600|533_and_h300)_face\/([A-Za-z0-9]+\.jpg)/);
      const poster = first(html, /t\/p\/w(?:300_and_h450|220_and_h330|600_and_h900)_(?:face|bestv2)\/([A-Za-z0-9]+\.jpg)/);
      // Title logos live on their own page; the main page's original/*.png are streaming-service logos.
      const logos = await page(`https://www.themoviedb.org/${path}/images/logos?image_language=en`);
      const logo = first(logos, /t\/p\/w500\/([A-Za-z0-9]+\.png)/);
      const entry = { tmdb: path };
      if (backdrop) entry.backdrop = await download(backdrop, 'w1280', `${t.key}-backdrop.jpg`);
      if (poster) entry.poster = await download(poster, 'w500', `${t.key}-poster.jpg`);
      if (logo) entry.logo = await download(logo, 'w500', `${t.key}-logo.png`);
      for (const n of t.seasons ?? []) {
        const s = await page(`https://www.themoviedb.org/${path}/season/${n}`);
        const stills = [...new Set([...s.matchAll(/t\/p\/w227_and_h127_face\/([A-Za-z0-9]+\.jpg)/g)].map(m => m[1]))];
        entry[`s${n}`] = [];
        for (const [i, still] of stills.entries()) entry[`s${n}`].push(await download(still, 'w500', `${t.key}-s${n}e${i + 1}.jpg`));
      }
      manifest[t.key] = entry;
      console.log(`ok   ${t.key.padEnd(24)} ${path}  backdrop:${!!backdrop} poster:${!!poster} logo:${!!logo}`);
    } catch (e) {
      console.log(`MISS ${t.key.padEnd(24)} ${e.message}`);
    }
  }
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2));
}

main();
