// One-off: real season/episode titles, runtimes, overviews and stills for every series, from TMDB's public pages.
// Writes assets/art/episodes.json and assets/art/<key>-s<N>e<M>.jpg. Usage: node tools/fetch-episodes.mjs
import { writeFile, readFile, access } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const run = promisify(execFile);
const OUT = join(dirname(fileURLToPath(import.meta.url)), '..', 'assets', 'art');
const UA = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130 Safari/537.36';
const sleep = ms => new Promise(r => setTimeout(r, ms));
const exists = p => access(p).then(() => true, () => false);
const curl = async args => (await run('curl', ['-sSL', '--fail', '-m', '20', '-A', UA, '-H', 'Accept-Language: en-GB', ...args], { maxBuffer: 64 << 20, encoding: 'buffer' })).stdout;
const page = async url => { await sleep(1100); return (await curl([url])).toString('utf8'); };
const unescape = s => s.replace(/&amp;/g, '&').replace(/&#39;|&#x27;/g, "'").replace(/&quot;/g, '"').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&#8217;/g, '’');

// which seasons to take: explicit list, or { latest: n }
const SERIES = {
  loki: [1, 2], simpsons: [5, 6, 7], 'planet-earth-3': [1], andor: [1, 2], severance: [1, 2], 'stranger-things': [1, 2, 3],
  'the-crown': [1, 2, 3], 'doctor-who': [1, 2, 3], 'slow-horses': [1, 2, 3], 'the-last-of-us': [1, 2], 'the-bear': [1, 2, 3],
  shogun: [1], gogglebox: { latest: 1 }, eastenders: { latest: 1 },
};
const MAX_EPS = { simpsons: 25 };

async function main() {
  const manifest = JSON.parse(await readFile(join(OUT, 'manifest.json'), 'utf8'));
  const out = (await exists(join(OUT, 'episodes.json'))) ? JSON.parse(await readFile(join(OUT, 'episodes.json'), 'utf8')) : {};
  for (const [key, want] of Object.entries(SERIES)) {
    const path = manifest[key]?.tmdb;
    if (!path) { console.log(`MISS ${key}: not in manifest`); continue; }
    try {
      let seasons = want;
      if (!Array.isArray(want)) {
        const list = await page(`https://www.themoviedb.org/${path}/seasons`);
        const ns = [...new Set([...list.matchAll(/\/season\/(\d+)"/g)].map(m => +m[1]))].filter(n => n > 0).sort((a, b) => a - b);
        seasons = ns.slice(-want.latest);
      }
      const entry = [];
      for (const n of seasons) {
        const html = await page(`https://www.themoviedb.org/${path}/season/${n}`);
        // Each episode card: still (w227_and_h127_face), then the title link, runtime, overview.
        const blocks = html.split(/<div class="card"|<div class="episode_list"/).slice(1);
        const eps = [];
        for (const b of html.split('data-episode-number="').slice(1)) void b;
        const titleRe = /<h3><a class="no_click open"[^>]*data-episode-number="(\d+)"[^>]*>([^<]+)<\/a>/g;
        const titles = [...html.matchAll(titleRe)];
        for (let i = 0; i < titles.length && eps.length < (MAX_EPS[key] ?? 12); i++) {
          const start = titles[i].index, end = titles[i + 1]?.index ?? html.length;
          const prevEnd = titles[i - 1]?.index ?? 0;
          const before = html.slice(prevEnd, start), after = html.slice(start, end);
          const still = [...before.matchAll(/t\/p\/w227_and_h127_face\/([A-Za-z0-9]+\.jpg)/g)].at(-1)?.[1];
          const mins = +(after.match(/(\d+)m<\/span>/)?.[1] ?? 0) || null;
          const overview = unescape(after.match(/class="overview">\s*<p>([^<]*)<\/p>/)?.[1] ?? '').trim();
          const epN = +titles[i][1];
          let file = null;
          if (still) {
            file = `${key}-s${n}e${epN}.jpg`;
            if (!(await exists(join(OUT, file)))) await writeFile(join(OUT, file), await curl([`https://image.tmdb.org/t/p/w500/${still}`]));
          }
          eps.push({ n: epN, title: unescape(titles[i][2]).trim(), mins, overview, still: file });
        }
        void blocks;
        entry.push({ n, episodes: eps });
      }
      out[key] = entry;
      console.log(`ok   ${key.padEnd(18)} seasons ${entry.map(s => `${s.n}(${s.episodes.length})`).join(' ')}`);
    } catch (e) { console.log(`MISS ${key}: ${e.message}`); }
  }
  await writeFile(join(OUT, 'episodes.json'), JSON.stringify(out, null, 1));
}
main();
