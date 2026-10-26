// Mock catalogue, channels and a clock-relative TV schedule.
// Art: assets/art/manifest.json (tools/fetch-art.mjs). Episodes: assets/art/episodes.json (tools/fetch-episodes.mjs).
import manifest from '../assets/art/manifest.json' with { type: 'json' };
import episodeData from '../assets/art/episodes.json' with { type: 'json' };

const MIN = 60_000;
const H = 60 * MIN;
const ART = 'assets/art/';

export function artFor(key) {
  const m = manifest[key];
  if (!m) return { backdrop: null, poster: null, logo: null, ambient: null, key };
  const f = x => (x ? ART + x : null);
  return { backdrop: f(m.backdrop), poster: f(m.poster), logo: f(m.logo), ambient: m.backdrop ? `${ART}ambient/${key}.jpg` : null, key };
}

// ---------------------------------------------------------------- services

export const SERVICE_NAMES = { jellyfin: 'Jellyfin', netflix: 'Netflix', disney: 'Disney+', iplayer: 'BBC iPlayer', youtube: 'YouTube', channel4: 'Channel 4' };
const SERVICE_ORDER = ['jellyfin', 'netflix', 'disney', 'iplayer', 'channel4', 'youtube'];

// ---------------------------------------------------------------- catalogue
// sources: [{ service, seasons? }] — seasons omitted means "all of it". requests: { [season | 0 for films]: progress 0..1 }.

function seasonsFor(key) {
  return (episodeData[key] ?? []).map(s => ({
    n: s.n,
    episodes: s.episodes.map(e => ({ n: e.n, title: e.title, mins: e.mins ?? 45, still: e.still ? ART + e.still : artFor(key).backdrop, synopsis: e.overview })),
  }));
}

function T(id, kind, name, year, rating, synopsis, sources, extra = {}) {
  const t = {
    id, kind, name, year, rating, synopsis, art: artFor(id), youWatch: false, requests: {},
    sources: sources.map(s => (typeof s === 'string' ? { service: s } : s)),
    ...(kind === 'series' ? { seasons: seasonsFor(id) } : {}),
    ...extra,
  };
  Object.defineProperty(t, 'availability', { get: () => availabilityOf(t), enumerable: true });
  Object.defineProperty(t, 'requestProgress', { get: () => { const v = Object.values(t.requests); return v.length ? Math.min(...v) : null; }, enumerable: true });
  return t;
}

export const catalogue = [
  T('loki', 'series', 'Loki', 2021, '12', 'The mercurial villain Loki resumes his role as the God of Mischief in a new series that takes place after the events of Avengers: Endgame.',
    [{ service: 'jellyfin', seasons: [1] }, { service: 'disney', seasons: [1, 2] }], { youWatch: true, progress: { season: 1, episode: 4, positionMins: 0 } }),
  T('simpsons', 'series', 'The Simpsons', 1989, 'PG', 'Set in Springfield, the average American town, the show focuses on the antics and everyday adventures of the Simpson family.',
    [{ service: 'jellyfin', seasons: [5, 6] }, { service: 'disney', seasons: [5, 6, 7] }], { youWatch: true, airsAs: 'The Simpsons', progress: { season: 6, episode: 12, positionMins: 0 } }),
  T('casper', 'film', 'Casper', 1995, 'PG', 'An afterlife therapist and his daughter meet a friendly young ghost when they move into a crumbling mansion.',
    ['jellyfin'], { youWatch: true, mins: 100, progress: { positionMins: 52 } }),
  T('planet-earth-3', 'series', 'Planet Earth III', 2023, 'PG', 'David Attenborough presents the most ambitious natural history series ever made, revealing animals finding ingenious ways to survive.',
    ['iplayer'], { youWatch: true, airsAs: 'Planet Earth III', progress: { season: 1, episode: 3, positionMins: 0 } }),
  T('severance', 'series', 'Severance', 2022, '15', 'Mark leads a team of office workers whose memories have been surgically divided between their work and personal lives.',
    [], { requests: { 1: 0.62 } }),
  T('stranger-things', 'series', 'Stranger Things', 2016, '15', 'When a young boy vanishes, a small town uncovers a mystery involving secret experiments and terrifying supernatural forces.',
    [{ service: 'netflix', seasons: [1, 2] }]),
  T('the-crown', 'series', 'The Crown', 2016, '15', 'The gripping, decades-spanning inside story of Her Majesty Queen Elizabeth II and the Prime Ministers who shaped Britain.', ['netflix']),
  T('doctor-who', 'series', 'Doctor Who', 2005, 'PG', 'The Doctor, a Time Lord, travels through time and space in the TARDIS with a succession of companions.', ['iplayer'], { airsAs: 'Doctor Who' }),
  T('blade-runner-2049', 'film', 'Blade Runner 2049', 2017, '15', 'A young blade runner discovers a long-buried secret that could plunge what is left of society into chaos.', ['jellyfin'], { mins: 164 }),
  T('paddington-2', 'film', 'Paddington 2', 2017, 'PG', 'Paddington picks up a series of odd jobs to buy the perfect present for Aunt Lucy, only for it to be stolen.', ['jellyfin', 'netflix'], { mins: 103, airsAs: 'Paddington 2' }),
  T('dune-2', 'film', 'Dune: Part Two', 2024, '12', 'Paul Atreides unites with Chani and the Fremen while seeking revenge against those who destroyed his family.', [], { mins: 166 }),
  T('slow-horses', 'series', 'Slow Horses', 2022, '15', 'A dysfunctional team of MI5 agents, banished to Slough House, find themselves dragged into dangerous operations.', []),
  T('andor', 'series', 'Andor', 2022, '12', 'Cassian Andor walks the path that will turn him into a rebel hero in the years before Rogue One.', [{ service: 'disney', seasons: [1] }]),
  T('the-last-of-us', 'series', 'The Last of Us', 2023, '15', 'Joel, a hardened survivor, is hired to smuggle Ellie, a 14-year-old girl, out of an oppressive quarantine zone.', []),
  T('sinners', 'film', 'Sinners', 2025, '15', 'Twin brothers return to their hometown to start again, only to find an even greater evil waiting for them.', [], { mins: 137 }),
  T('alien', 'film', 'Alien', 1979, '15', 'The crew of a commercial spacecraft encounter a deadly lifeform after investigating a mysterious transmission.', ['jellyfin'], { mins: 117, airsAs: 'Alien' }),
  T('the-bear', 'series', 'The Bear', 2022, '15', 'A young chef from the fine-dining world returns to Chicago to run his late brother\'s sandwich shop.', ['disney']),
  T('shogun', 'series', 'Shōgun', 2024, '18', 'In feudal Japan, Lord Toranaga fights for his life as his enemies on the Council of Regents unite against him.', ['disney']),
  T('eastenders', 'series', 'EastEnders', 1985, '12', 'The everyday lives of the residents of Albert Square in the East End of London.', ['iplayer'], { youWatch: true, airsAs: 'EastEnders' }),
  T('jurassic-park', 'film', 'Jurassic Park', 1993, 'PG', 'A wealthy entrepreneur secretly creates a theme park featuring living dinosaurs drawn from prehistoric DNA.', [], { mins: 127, airsAs: 'Jurassic Park' }),
  T('mission-impossible', 'film', 'Mission: Impossible – Fallout', 2018, '12', 'Ethan Hunt and his IMF team race against time after a mission goes wrong.', ['netflix'], { mins: 147, airsAs: 'Mission: Impossible – Fallout' }),
  T('gogglebox', 'series', 'Gogglebox', 2013, '15', 'Households across Britain share their opinions on the week\'s TV.', ['channel4'], { airsAs: 'Gogglebox' }),
];

const BY_ID = new Map(catalogue.map(t => [t.id, t]));
export const titleById = id => BY_ID.get(id) ?? null;

const covers = (src, n) => !src.seasons || src.seasons.includes(n);
/** Where season n (or the film, n = null) can be streamed, best first. */
export function sourcesFor(t, n = null) {
  return t.sources.filter(s => n == null || covers(s, n)).sort((a, b) => SERVICE_ORDER.indexOf(a.service) - SERVICE_ORDER.indexOf(b.service));
}
export const seasonSource = (t, n) => sourcesFor(t, n)[0] ?? null;
/** 'available' | 'requested' | 'missing' for a season (or a film with n = null). */
export function seasonState(t, n = null) {
  if ((t.seasons ? sourcesFor(t, n) : t.sources).length) return 'available';
  return t.requests[n ?? 0] != null ? 'requested' : 'missing';
}
/** play: everything streams · partial: some seasons stream · requested: nothing yet, something on its way · get: nothing. */
function availabilityOf(t) {
  if (!t.seasons) return t.sources.length ? 'play' : t.requests[0] != null ? 'requested' : 'get';
  const states = t.seasons.map(s => seasonState(t, s.n));
  if (states.every(s => s === 'available')) return 'play';
  if (states.includes('available')) return 'partial';
  return states.includes('requested') ? 'requested' : 'get';
}
export function bestSource(t) {
  if (!t.seasons) return sourcesFor(t)[0] ?? null;
  for (const s of t.seasons) { const src = seasonSource(t, s.n); if (src) return src; }
  return null;
}
/** Season numbers with nothing streaming and nothing requested. */
export const missingSeasons = t => (t.seasons ?? []).filter(s => seasonState(t, s.n) === 'missing').map(s => s.n);

/** One episode: available if its season streams or it was downloaded on its own; requested if it or its season is on the way. */
export function episodeState(t, season, episode) {
  if (seasonState(t, season) === 'available' || t.episodeSources?.has(`${season}.${episode}`)) return 'available';
  return t.requests[season] != null || t.requests[`${season}.${episode}`] != null ? 'requested' : 'missing';
}
/** Ask for one episode (in reality via Sonarr: Seerr requests whole seasons). */
export function requestEpisode(t, season, episode) { t.requests[`${season}.${episode}`] = t.requests[`${season}.${episode}`] ?? 0.02; }

/** Ask Seerr for missing seasons (or the film). */
export function requestSeasons(t, seasons = null) {
  if (!t.seasons) { t.requests[0] = t.requests[0] ?? 0.02; return; }
  for (const n of seasons ?? t.seasons.map(s => s.n)) if (seasonState(t, n) === 'missing') t.requests[n] = 0.02;
}
/** A request completing: it lands on Jellyfin. */
export function completeRequest(t, key) {
  delete t.requests[key];
  if (String(key).includes('.')) { (t.episodeSources ??= new Set()).add(String(key)); return; }
  if (!t.seasons) { t.sources.push({ service: 'jellyfin' }); return; }
  const jf = t.sources.find(s => s.service === 'jellyfin' && s.seasons);
  if (jf) jf.seasons.push(+key); else t.sources.unshift({ service: 'jellyfin', seasons: [+key] });
}

/** Human summary of where a title can be watched, e.g. "Seasons 1–2 on Netflix · Season 3 can be requested". */
export function availabilitySummary(t) {
  const runs = ns => { const out = []; for (const n of ns) { const last = out.at(-1); if (last && n === last[1] + 1) last[1] = n; else out.push([n, n]); } return out.map(([a, b]) => (a === b ? `${a}` : `${a}–${b}`)).join(', '); };
  const label = ns => `${ns.length === 1 ? 'Season' : 'Seasons'} ${runs(ns)}`;
  if (!t.seasons) {
    const s = sourcesFor(t)[0];
    return s ? `On ${SERVICE_NAMES[s.service]}` : t.requests[0] != null ? `Requested · ${Math.round(t.requests[0] * 100)}%` : 'Not available to stream';
  }
  const parts = [];
  const bySvc = new Map();
  for (const s of t.seasons) { const src = seasonSource(t, s.n); if (src) bySvc.set(src.service, [...(bySvc.get(src.service) ?? []), s.n]); }
  for (const [svc, ns] of bySvc) parts.push(ns.length === t.seasons.length ? `On ${SERVICE_NAMES[svc]}` : `${label(ns)} on ${SERVICE_NAMES[svc]}`);
  const req = t.seasons.filter(s => seasonState(t, s.n) === 'requested').map(s => s.n);
  const miss = t.seasons.filter(s => seasonState(t, s.n) === 'missing').map(s => s.n);
  if (req.length) parts.push(`${label(req)} downloading`);
  const eps = [...(t.episodeSources ?? [])].map(k => +k.split('.')[0]).filter(n => seasonState(t, n) !== 'available');
  for (const n of [...new Set(eps)]) { const c = eps.filter(x => x === n).length; parts.push(`${c} episode${c === 1 ? '' : 's'} of season ${n} on Jellyfin`); }
  if (miss.length) parts.push(parts.length ? `${label(miss)} can be requested` : 'Not available to stream');
  return parts.join(' · ');
}

// ---------------------------------------------------------------- channels & schedule

const P = (title, art, mins, synopsis, titleId) => ({ title, art, mins, synopsis, titleId });
const NEWS = P('BBC News', 'bbc-news', 30, 'The latest national and international news from the BBC.');
// Programmes whose art key isn't in the manifest have no artwork, on purpose: they show the fallbacks.
const POOLS = {
  bbc1: [P('Pointless', 'pointless', 45, 'Quiz show in which contestants try to find the most obscure answers.'), P('Antiques Roadshow', 'antiques-roadshow', 60, 'Experts value treasures brought along by the public.'),
    P('Strictly Come Dancing', 'strictly', 90, 'Celebrities partner professional dancers in the glittering ballroom competition.'), P('Doctor Who', 'doctor-who', 60, 'The Doctor and friends travel through time and space.', 'doctor-who'),
    P('Homes Under the Hammer', 'no-art', 60, 'Property auctions and the renovations that follow.'), NEWS],
  bbc2: [P("Richard Osman's House of Games", 'house-of-games', 30, 'Four celebrities compete in a week of quickfire quiz games.'), P("Gardeners' World", 'gardeners-world', 30, 'Practical advice for the season ahead.'),
    P('Top Gear', 'top-gear', 60, 'Cars, challenges and very British mishaps.'), P('Only Connect', 'only-connect', 30, 'The quiz where knowledge will only take you so far.'), P('Planet Earth III', 'planet-earth-3', 60, 'Extraordinary animal behaviour filmed across the planet.', 'planet-earth-3'),
    P('Politics Live', 'no-art', 60, 'Westminster debate and interviews.')],
  itv1: [P('Coronation Street', 'coronation-street', 30, 'Drama on the cobbles of Weatherfield.'), P('Emmerdale', 'emmerdale', 30, 'Village life in the Yorkshire Dales.'),
    P('ITV News', 'bbc-news', 30, 'The latest news from ITV.'), P('Police Interceptors', 'police-interceptors', 60, 'Following the police on the roads of Britain.'), P('The Chase', 'no-art', 60, 'Contestants take on the Chaser.')],
  c4: [P('Hollyoaks', 'hollyoaks', 30, 'Drama in the Chester village of Hollyoaks.'), P('Grand Designs', 'grand-designs', 60, 'Kevin McCloud follows ambitious self-builders.'),
    P('The Great British Bake Off', 'great-british-bake-off', 75, 'Amateur bakers compete in the famous tent.'), P('Gogglebox', 'gogglebox', 60, "Britain's sofas react to the week's TV.", 'gogglebox'),
    P('A Place in the Sun', 'no-art', 60, 'House hunters search for a home abroad.')],
  c5: [P('Police Interceptors', 'police-interceptors', 60, 'Following the police on the roads of Britain.'), P('5 News', 'no-art', 30, 'The latest news from Channel 5.'), P('Jurassic Park', 'jurassic-park', 120, 'Dinosaurs run riot in a theme park.', 'jurassic-park'),
    P('Milkshake!', 'no-art', 60, 'Programmes for pre-school children.')],
  itv2: [P('Family Guy', 'family-guy', 30, 'The misadventures of Peter Griffin and family.'), P('Family Guy', 'family-guy', 30, 'More from the Griffins of Quahog.'), P('Love Island: Aftersun', 'no-art', 60, 'The week in the villa.')],
  bbc3: [P('Taskmaster', 'taskmaster', 60, 'Comedians attempt absurd tasks to win the Taskmaster\'s approval.'), P('Family Guy', 'family-guy', 30, 'The Griffins of Quahog.')],
  bbc4: [P('QI', 'qi', 30, 'The quiz where the answers are interesting rather than right.'), P('Only Connect', 'only-connect', 30, 'Connections, sequences and walls.'), P('Planet Earth III', 'planet-earth-3', 60, 'Wildlife from every corner of the planet.', 'planet-earth-3'),
    P('Top of the Pops', 'no-art', 30, 'The charts from 1991.')],
  sky: [P('The Simpsons', 'simpsons', 30, 'Another day in Springfield.', 'simpsons'), P('Have I Got News for You', 'mock-the-week', 30, 'Satirical news quiz.'),
    P('Brassic', 'no-art', 60, 'A gang of friends in a northern town try to stay one step ahead of the law.'), P('A League of Their Own', 'no-art', 60, 'Sports panel show.'), P('Taskmaster', 'taskmaster', 60, 'Comedians attempt absurd tasks.')],
  film4: [P('Alien', 'alien', 120, 'In space, no one can hear you scream.', 'alien'), P('Mission: Impossible – Fallout', 'mission-impossible', 150, 'Ethan Hunt races against time.', 'mission-impossible'), P('Jurassic Park', 'jurassic-park', 120, 'Life finds a way.', 'jurassic-park'), P('Paddington 2', 'paddington-2', 105, 'Paddington\'s perfect present is stolen.', 'paddington-2')],
  dave: [P('Top Gear', 'top-gear', 60, 'Classic Top Gear.'), P('QI', 'qi', 30, 'Quite interesting.'), P('Taskmaster', 'taskmaster', 60, 'The Taskmaster sets more absurd tasks.')],
  bbcnews: [NEWS],
  skynews: [P('Sky News', 'no-art', 60, 'Breaking news and analysis.')],
  cbbc: [P('Blue Peter', 'blue-peter', 30, 'Makes, challenges and badges.'), P('Bluey', 'bluey', 30, 'An inexhaustible six-year-old Blue Heeler dog.')],
  cbeebies: [P('Bluey', 'bluey', 30, 'Bluey and Bingo turn everyday family life into extraordinary adventures.')],
};
const FIXED = {
  bbc1: [['18:00', P('BBC News at Six', 'bbc-news', 30, 'The latest national and international news, with reports from BBC correspondents worldwide.')], ['18:30', P('Regional News', 'regional', 30, 'News from where you are.')],
    ['19:00', P('EastEnders', 'eastenders', 30, 'Tensions rise in the Square as a secret threatens to come out.', 'eastenders')], ['19:30', P('The One Show', 'the-one-show', 30, 'Topical magazine show with stories and studio guests.')],
    ['22:00', P('BBC News at Ten', 'bbc-news', 30, 'The day\'s news, with analysis.')], ['22:30', P('Match of the Day', 'match-of-the-day', 80, 'Highlights from the day\'s Premier League matches.')]],
  bbc2: [['21:00', P('Question Time', 'question-time', 60, 'Topical debate in front of an audience.')]],
  itv1: [['18:30', P('ITV Evening News', 'bbc-news', 30, 'The day\'s news.')], ['19:00', P('Emmerdale', 'emmerdale', 30, 'Village life in the Dales.')], ['19:30', P('Coronation Street', 'coronation-street', 60, 'Drama on the cobbles.')]],
  c4: [['18:00', P('The Simpsons', 'simpsons', 30, 'Homer joins the Stonecutters, a secret society, and is revealed to be their Chosen One.', 'simpsons')], ['18:30', P('Hollyoaks', 'hollyoaks', 30, 'Drama in the village.')],
    ['19:00', P('Channel 4 News', 'no-art', 55, 'In-depth news and analysis.')], ['21:00', P('Gogglebox', 'gogglebox', 60, "Britain's sofas react to the week's TV.", 'gogglebox')]],
  film4: [['21:00', P('Alien', 'alien', 120, 'The crew of the Nostromo answer a distress call.', 'alien')]],
};

// Brand colours drive the branded fallback cards (logo-style wordmark on the channel colour).
const C = (id, number, name, catchup, colour, extra = {}) => ({ id, number, name, catchup, colour, ...extra });
export const channels = [
  C('bbc1', 101, 'BBC One', true, '#c8102e'), C('bbc2', 102, 'BBC Two', true, '#5b2c83'), C('itv1', 103, 'ITV1', true, '#2a6ebb'), C('c4', 104, 'Channel 4', true, '#3c3c3c'),
  C('c5', 105, 'Channel 5', true, '#e10a0a'), C('itv2', 106, 'ITV2', true, '#007c91'), C('bbc3', 107, 'BBC Three', true, '#d4145a'), C('bbc4', 109, 'BBC Four', true, '#1f4e79'),
  C('sky', 111, 'Sky Mix', false, '#0a5ec2'), C('film4', 115, 'Film4', true, '#7a3e8e'), C('dave', 120, 'Dave', true, '#1b1b1b'), C('tpt', 121, 'Talking Pictures TV', false, '#5a4632', { noEpg: true }),
  C('bbcnews', 131, 'BBC News', false, '#b80000'), C('skynews', 133, 'Sky News', false, '#b5121b'), C('cbbc', 201, 'CBBC', true, '#8dc63f'), C('cbeebies', 202, 'CBeebies', true, '#ffd200'),
];
export const channelById = id => channels.find(c => c.id === id) ?? null;

function seeded(str) {
  let h = 2166136261;
  for (const ch of str) h = Math.imul(h ^ ch.charCodeAt(0), 16777619);
  return () => ((h = Math.imul(h ^ (h >>> 15), 2246822507) ^ Math.imul(h ^ (h >>> 13), 3266489909)) >>> 0) / 4294967296;
}
const midnight = ms => { const d = new Date(ms); d.setHours(0, 0, 0, 0); return d.getTime(); };
const wall = (dayStart, minutes) => { const d = new Date(dayStart); return new Date(d.getFullYear(), d.getMonth(), d.getDate(), 0, minutes).getTime(); };
const nextDay = dayStart => wall(dayStart, 1440);
const hm = s => { const [h, m] = s.split(':').map(Number); return h * 60 + m; };

const dayCache = new Map();
function daySchedule(channelId, dayStart) {
  const key = `${channelId}@${dayStart}`;
  if (dayCache.has(key)) return dayCache.get(key);
  const rand = seeded(key);
  const pool = POOLS[channelId];
  const fixed = (FIXED[channelId] ?? []).map(([t, p]) => ({ at: hm(t), p })).sort((a, b) => a.at - b.at);
  const out = [];
  let t = 0;
  while (t < 1440) {
    const f = fixed.find(x => x.at === t);
    const nextFixed = fixed.find(x => x.at > t)?.at ?? 1440;
    const p = f ? f.p : pool[Math.floor(rand() * pool.length)];
    const len = Math.max(5, Math.min(p.mins, nextFixed - t, 1440 - t));
    // Wall-clock times, so 18:00 is 18:00 even on the day the clocks change.
    const start = wall(dayStart, t), end = wall(dayStart, t + len);
    out.push({
      id: `${channelId}-${start}`, channelId, title: p.title, subtitle: p.titleId && channelId !== 'film4' ? `Episode ${1 + Math.floor(rand() * 8)}` : '',
      synopsis: p.synopsis, start, end, titleId: p.titleId ?? null, art: artFor(p.art),
    });
    t += len;
  }
  dayCache.set(key, out);
  return out;
}

/** Programmes on a channel, contiguous, from before now−back to after now+ahead (default −3 h … +24 h). */
export function scheduleFor(channelId, nowMs, { back = 3 * H, ahead = 24 * H } = {}) {
  const from = nowMs - back, to = nowMs + ahead;
  if (channelById(channelId)?.noEpg) {
    return [{ id: `${channelId}-none`, channelId, title: 'No information', subtitle: '', synopsis: '', start: midnight(from), end: nextDay(midnight(to)), titleId: null, art: artFor(null), noInfo: true }];
  }
  const out = [];
  for (let d = wall(midnight(from), -1440); d <= to; d = nextDay(d)) out.push(...daySchedule(channelId, d));
  return out.filter(p => p.end > from && p.start < to);
}

export const programmeAt = (channelId, ms) => scheduleFor(channelId, ms).find(p => p.start <= ms && ms < p.end) ?? null;

/** Every showing of a programme over the last and next 7 days, across channels. Repeats share an earlier subtitle. */
export function showingsOf(title, nowMs) {
  const all = [];
  for (const c of channels) {
    if (c.noEpg) continue;
    for (const p of scheduleFor(c.id, nowMs, { back: 7 * 24 * H, ahead: 7 * 24 * H })) {
      if (p.title !== title) continue;
      const when = p.end <= nowMs ? 'catchup' : p.start <= nowMs ? 'live' : 'later';
      if (when === 'catchup' && !c.catchup) continue;
      all.push({ ...p, when });
    }
  }
  all.sort((a, b) => a.start - b.start);
  const seen = new Set();
  for (const s of all) { const k = s.subtitle || s.title; s.repeat = seen.has(k); seen.add(k); }
  return all;
}

// ---------------------------------------------------------------- Home rows

function episodeCard(t, season, episode) {
  const ep = t.seasons.find(s => s.n === season).episodes[episode - 1];
  const p = t.progress;
  const progress = p && p.season === season && p.episode === episode ? Math.min(1, (p.positionMins ?? 0) / ep.mins) : 0;
  return { type: 'episode', titleId: t.id, season, episode, label: t.name, sublabel: `S${season} E${episode} · ${ep.title}`, art: ep.still ?? t.art.backdrop, logo: t.art.logo, backdrop: t.art.backdrop, progress, badge: null };
}

/** Titles in progress, most recent first. Playing something moves it to the front (touchUpNext). */
export const upNextOrder = ['loki', 'simpsons', 'casper', 'planet-earth-3'];
export function touchUpNext(titleId) {
  const i = upNextOrder.indexOf(titleId);
  if (i >= 0) upNextOrder.splice(i, 1);
  upNextOrder.unshift(titleId);
}

export function upNext(nowMs) {
  const cards = [];
  for (const id of upNextOrder) {
    const t = titleById(id);
    if (!t?.progress) continue;
    if (t.seasons && t.progress.season) cards.push(episodeCard(t, t.progress.season, t.progress.episode));
    else if (!t.seasons) cards.push({ type: 'title', titleId: t.id, label: t.name, sublabel: `${Math.max(1, Math.round(t.mins - t.progress.positionMins))} min left`, art: t.art.backdrop, logo: t.art.logo, backdrop: t.art.backdrop, progress: t.progress.positionMins / t.mins, badge: null });
  }
  for (const t of catalogue.filter(x => Object.keys(x.requests).length)) {
    const [k, v] = Object.entries(t.requests).sort((a, b) => a[1] - b[1])[0];
    const what = !t.seasons ? 'Downloading' : String(k).includes('.') ? `S${k.split('.')[0]} E${k.split('.')[1]} downloading` : `Season ${k} downloading`;
    cards.push({ type: 'title', titleId: t.id, label: t.name, sublabel: `${what} · ${Math.round(v * 100)}%`, art: t.art.backdrop, logo: t.art.logo, backdrop: t.art.backdrop, progress: v, badge: 'requested' });
  }
  return cards;
}

export function onNow(nowMs) {
  return channels.map(c => {
    const p = programmeAt(c.id, nowMs);
    return {
      type: 'channel', channelId: c.id, label: `${c.number}  ${c.name}`, sublabel: p.noInfo ? 'No programme information' : p.title, art: p.art.backdrop, logo: null, backdrop: p.art.backdrop,
      progress: p.noInfo ? null : (nowMs - p.start) / (p.end - p.start), badge: 'live', programme: p, colour: c.colour, channelName: c.name,
    };
  });
}

const badgeFor = t => (t.availability === 'play' ? null : t.availability);
const titleCard = t => ({ type: 'title', titleId: t.id, label: t.name, sublabel: `${t.year} · ${t.kind === 'film' ? 'Film' : 'Series'}`, art: t.art.poster ?? t.art.backdrop, logo: t.art.logo, backdrop: t.art.backdrop, progress: null, badge: badgeFor(t), scope: null });

/** Watchlist entries: { kind: 'title' } | { kind: 'season', season } | { kind: 'episode', season, episode }. */
export const defaultWatchlist = [
  { kind: 'title', titleId: 'blade-runner-2049' }, { kind: 'season', titleId: 'the-bear', season: 3 }, { kind: 'title', titleId: 'shogun' },
  { kind: 'episode', titleId: 'doctor-who', season: 3, episode: 10 }, { kind: 'season', titleId: 'stranger-things', season: 3 },
  { kind: 'title', titleId: 'paddington-2' }, { kind: 'title', titleId: 'dune-2' }, { kind: 'title', titleId: 'alien' },
];
export function watchlistCards(entries) {
  return entries.map(e => {
    const t = titleById(e.titleId);
    const c = titleCard(t);
    if (e.kind === 'season') { c.scope = `Season ${e.season}`; c.season = e.season; c.badge = seasonState(t, e.season) === 'available' ? null : seasonState(t, e.season) === 'requested' ? 'requested' : 'get'; }
    if (e.kind === 'episode') {
      const ep = t.seasons.find(s => s.n === e.season)?.episodes[e.episode - 1];
      Object.assign(c, { type: 'episode', season: e.season, episode: e.episode, scope: `S${e.season} E${e.episode}`, sublabel: ep?.title ?? c.sublabel, art: ep?.still ?? c.art });
    }
    c.entry = e;
    return c;
  });
}
export const watchlist = () => watchlistCards(defaultWatchlist);
export const discover = () => ['dune-2', 'andor', 'the-last-of-us', 'sinners', 'slow-horses', 'stranger-things', 'the-crown', 'doctor-who', 'mission-impossible'].map(id => titleCard(titleById(id)));
