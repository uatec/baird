# Baird UX Redesign Prototype Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A remote-driven, Apple-TV-grade HTML prototype of the redesigned Baird UX (Home, Guide, Search, show page, live and on-demand playback layers), served on the Tailscale IP.

**Architecture:** Static ES modules with no build step. The behaviour lives in pure modules (key mapping, navigation stack, focus movement, mock data and schedule, search), and Node's built-in test runner tests them. Screens are plain DOM renderers over one 1920×1080 stage scaled to the window. A one-off script fetches real TMDB artwork into the folder, so the page works offline and holds no API keys.

**Tech Stack:** HTML, CSS, vanilla JS (ES2022 modules), Node 26 `node --test` for unit tests, Python `http.server` for serving, headless `google-chrome-stable` for screenshot checks.

**Spec:** `docs/superpowers/specs/2026-10-25-baird-ux-redesign-prototype-design.md`

## Global Constraints

- All files live under `spikes/ux-redesign-prototype/`. **Do not commit** (the owner's say-so is required). There are no commit steps.
- No framework, no bundler, no npm dependencies. The browser loads `index.html` → `js/app.js` as `type="module"`.
- Stage: a fixed 1920×1080 design canvas, scaled uniformly to fit the window and letterboxed. Safe margin: 96 px horizontal, 54 px vertical. Minimum body text: 24 px.
- Exactly one focused element at any time. Arrows never open panels.
- Colour: red `#ff3b30` only for LIVE/NOW. Gold `#ffcc66` only for YOU WATCH. The chrome is otherwise neutral.
- Motion: transform/opacity only, 200–350 ms, `cubic-bezier(.2,.8,.2,1)`.
- Lite mode (Pi): enabled when `navigator.userAgent` matches `/aarch64|armv|Linux arm/i` or the URL has `?lite`. In lite mode, no `backdrop-filter` (use `rgba(28,28,32,.92)` panels), no grain overlay, and no Ken Burns.
- Images: backdrops ≤ 1280 px wide (`w1280`), posters `w500`, stills `w500`, logos `w500`. All stored locally in `assets/art/`.
- Serve on `100.109.40.12`, port `8077`.
- Hold OK = Enter held ≥ 500 ms (fires on the hold threshold, not on keyup). A short press fires on keyup.

## Review Focus

1. **Back from every state lands where the spec's Back rule says.** Pinned by `nav.test.mjs` cases for overlay → video, page stack → Home, video → origin page, Home → no-op (Task 3).
2. **Remote keys arrive as unfamiliar codes on the Pi** (`BrowserBack`, `MediaPlayPause`, `ContextMenu`, Backspace navigating away). Pinned by `keys.test.mjs` aliases plus the key inspector (Task 1).
3. **Guide navigation across programmes of unequal length** keeps a sensible time anchor, so ↓ from a long programme doesn't jump hours. Pinned by `focus.test.mjs` "anchor time" cases (Task 4).
4. **Clock-relative data around midnight**: the schedule and NOW line must not break when "now" is 23:50. Pinned by a `data.test.mjs` case with a fixed 23:50 clock (Task 2).
5. **Typing a channel number that doesn't exist** (e.g. 999): it shows "No channel 999" and doesn't tune. Pinned in `keys.test.mjs` via `numberEntry` (Task 1).

---

### Task 1: Scaffold, server, key mapping, number entry, key inspector

**Files:**
- Create: `spikes/ux-redesign-prototype/index.html`, `serve.sh`, `js/keys.js`, `js/app.js` (stub), `styles/base.css`, `tests/keys.test.mjs`, `package.json` (`{"type":"module","scripts":{"test":"node --test tests/"}}`)

**Interfaces:**
- Produces: `mapKey(e: {key: string, code: string}) -> Action | null`, where `Action` is one of `'up'|'down'|'left'|'right'|'ok'|'back'|'home'|'guide'|'playpause'|'prev'|'next'|'pageup'|'pagedown'|'del'|'voice'|'digit:N'|'debug-ff'|'debug-keys'|'help'`.
- Produces: `createHoldDetector({holdMs = 500, now = () => performance.now()}) -> {down(e), up(e) -> 'ok'|'hold-ok'|null}` (OK only).
- Produces: `numberEntry(channels: Channel[]) -> {push(d: number) -> {text: string, match: Channel|null, error: string|null}, del() -> sameShape, clear()}`.

- [ ] **Step 1: Write failing tests** in `tests/keys.test.mjs`:
  - `mapKey({key:'Enter'})==='ok'`. `'Escape'`, `'Backspace'` and `'BrowserBack'` → `'back'`. `'h'`, `'BrowserHome'` and `'HomePage'` → `'home'`. `'g'` and `'ContextMenu'` → `'guide'`. `' '` and `'MediaPlayPause'` → `'playpause'`. `'['` and `'MediaTrackPrevious'` → `'prev'`. `']'` and `'MediaTrackNext'` → `'next'`. `'PageUp'` and `'ChannelUp'` → `'pageup'`. `'Delete'` → `'del'`. `'v'` → `'voice'`. `'7'` → `'digit:7'`. `'f'` → `'debug-ff'`. `'k'` → `'debug-keys'`. `'?'` → `'help'`. `'q'` → `null`.
  - Hold detector: down at t=0 and up at t=200 → `'ok'`. Down at 0, then a check at 520 → `'hold-ok'` fires once, and up after that returns `null`.
  - numberEntry with channels 101 BBC One and 104 Channel 4: push(1), push(0), push(4) → `{text:'104', match: Channel 4}`. push 9, 9, 9 → `error: 'No channel 999'`, `match: null`. del() after '10' → text `'1'`.
- [ ] **Step 2: Run** `cd spikes/ux-redesign-prototype && node --test tests/keys.test.mjs`. Expected: FAIL (module not found).
- [ ] **Step 3: Implement `js/keys.js`.** The hold detector exposes `tick(t)` for tests and uses `setTimeout` in the browser.
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Shell and server.**
  - `index.html` holds `<div id="stage">` sized 1920×1080 and scaled by JS to fit the window, plus `<div id="keyinspector">`.
  - `app.js` installs the keydown/keyup listeners. It calls `preventDefault` on every mapped key and on Backspace. It pushes a `history` entry and re-pushes on `popstate`, so the browser's back can't leave the page.
  - `debug-keys` toggles the inspector, which shows the last `key` / `code` / mapped action.
  - `serve.sh` runs `python3 -m http.server 8077 --bind 100.109.40.12` from the folder.
- [ ] **Step 6: Verify.** Run `bash serve.sh &`, then `curl -s http://100.109.40.12:8077/ | grep -c stage`. Expected: `1`.

### Task 2: Artwork fetch and mock data

**Files:**
- Create: `tools/fetch-art.mjs`, `assets/art/` (generated), `js/data.js`, `tests/data.test.mjs`

**Interfaces:**
- Produces: `catalogue: Title[]`, where `Title = {id, kind:'series'|'film', name, year, rating, synopsis, art:{backdrop, poster, logo|null}, sources:[{service:'jellyfin'|'netflix'|'disney'|'iplayer'|'youtube', id}], availability:'play'|'get'|'requested', requestProgress?:number, seasons?:[{n, episodes:[{n, title, mins, still, synopsis}]}], progress?:{season, episode, positionMins}|{positionMins}, youWatch:boolean, airsAs?: string /* programme title in EPG */}`.
- Produces: `channels: Channel[]`, where `Channel = {id, number, name, catchup:boolean, art: string /* a backdrop used for its programmes */}`.
- Produces: `scheduleFor(channelId, nowMs) -> Programme[]`, where `Programme = {id, channelId, title, subtitle, synopsis, start, end /* ms */, titleId?: string, art}`. It covers `now − 3 h` … `now + 24 h`, deterministic for a given day, with no gaps or overlaps.
- Produces: `upNext(nowMs) -> Card[]`, `onNow(nowMs) -> Card[]`, `watchlist() -> Card[]`, `discover() -> Card[]`, where `Card = {type:'title'|'episode'|'channel', titleId?, channelId?, label, sublabel, art, progress?:0..1, badge?: 'live'|'get'|'requested'|'youwatch'}`.
- Produces: `bestSource(title) -> source` (the Jellyfin copy if present, else the first source).

- [ ] **Step 1: Write `tools/fetch-art.mjs`.**
  - Input: a list of `{key, tmdb:'tv/84958' | 'movie/8839', seasons?:[n]}`.
  - Fetch `https://www.themoviedb.org/<tmdb>` with a browser User-Agent and pull out:
    - the first `w1066_and_h600_face/<path>.jpg` → download `https://image.tmdb.org/t/p/w1280/<path>` as `<key>-backdrop.jpg`;
    - the first `w300_and_h450_bestv2/<path>.jpg` (or any `_face` poster) → `w500` as `-poster.jpg`;
    - the first `/original/<path>.png` → `w500` as `-logo.png`.
  - For each season, fetch `/<tmdb>/season/<n>` and download episode stills (`w500`) as `<key>-s<n>e<m>.jpg`.
  - Write `assets/art/manifest.json` mapping keys to files.
  - Titles: Loki, The Simpsons, Casper, Planet Earth III, Severance, Stranger Things, The Crown, Doctor Who, Blade Runner 2049, Paddington 2, Dune: Part Two, Slow Horses, Andor, EastEnders, Gogglebox, Match of the Day, Grand Designs, Gardeners' World, Pointless, Alien.
  - Missing art is logged, not fatal.
- [ ] **Step 2: Run** `node tools/fetch-art.mjs`. Expected: `manifest.json` exists, and at least 15 titles have a backdrop. Look at 3 images to confirm they're the right shows.
- [ ] **Step 3: Write failing tests** in `tests/data.test.mjs`:
  - For every channel, `scheduleFor(id, now)` has contiguous programmes (`p[i].end === p[i+1].start`) covering `now−3h..now+24h`.
  - With `now = 2026-10-25T23:50` local: exactly one programme per channel contains `now`.
  - `scheduleFor('c4', <a day at 18:10>)` has "The Simpsons" at 18:00–18:30. `scheduleFor('bbc1', …)` has "EastEnders" starting at 19:00.
  - `upNext(now)[0]` is Loki S1 E4. Casper's progress is between 0.4 and 0.6. Severance has `badge:'requested'` with progress 0.62.
  - `bestSource(Loki).service === 'jellyfin'`.
  - `onNow(now).length >= 10`, and every card has a `progress` in 0..1.
- [ ] **Step 4: Run** `node --test tests/data.test.mjs`. Expected: FAIL.
- [ ] **Step 5: Implement `js/data.js`.**
  - About 15 channels with real numbers: 101 BBC One, 102 BBC Two, 103 ITV1, 104 Channel 4, 105 Channel 5, 106 ITV2, 107 BBC Three, 109 BBC Four, 111 Sky Mixed, 115 Film4, 120 Dave, 131 BBC News, 133 Sky News, 201 CBBC, 202 CBeebies.
  - Schedules are built from per-channel daily templates anchored to local midnight, plus the fixed slots above, with a seeded pseudo-random filler.
  - Art paths come from `manifest.json` (imported with `import manifest from '../assets/art/manifest.json' with {type:'json'}`), with gradient fallbacks keyed by title.
- [ ] **Step 6: Run** `node --test tests/`. Expected: PASS.

### Task 3: Navigation model (layers, pages, Back)

**Files:**
- Create: `js/nav.js`, `tests/nav.test.mjs`

**Interfaces:**
- Produces: `createNavigator() -> Nav` with:
  - `state(): {video: null|{kind:'live'|'vod', ref}, pages: Page[], overlay: null|Overlay, originPages: Page[]}`
  - `openPage(page)`, `openOverlay(overlay)`, `play(videoRef, kind)`, `back() -> void`, `home()`, `top() -> Page|Overlay|'video'|'home'`
  - `onChange(fn)`
  - `Page = {name:'guide'|'search'|'show'|'settings'|'channel-schedule', params}`, `Overlay = {name:'info'|'channels'|'actions'|'endcard'|'nearend'|'number', params}`. Home is the implicit bottom.

- [ ] **Step 1: Write failing tests** in `tests/nav.test.mjs`:
  - Overlay over video: `play(bbc1,'live')`, `openOverlay(channels)`, `back()` → `top()==='video'`.
  - Page stack to Home: `openPage(search)`, `openPage(show)`, `back()` → top is `search`. `back()` again → `'home'`.
  - Back retraces the journey: search → show → `play(loki,'vod')` → top is `'video'` and pages are hidden. `back()` → top is `show` and the video keeps playing (`state().video` not null). `back()` → `search`. `back()` → `'home'`.
  - Play from Home, then `back()` → `'home'` with the video still set (it shows in the hero).
  - `back()` on Home with nothing above it → no change.
  - Guide opened over the video: `play(bbc1)`, `openPage(guide)`, `back()` → `'video'`.
  - `home()` clears pages and overlays and keeps the video.
  - Opening an overlay while a page is on top isn't allowed (overlays only sit over the video). Hold-OK actions over a page use overlay `actions` with a `params.over` page, and back closes it to that page.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement `js/nav.js`.** `play()` moves the current `pages` into `originPages`, clears `pages`, and sets the video. When `back()` is called on the bare video, it restores `originPages` (or goes Home if they're empty).
- [ ] **Step 4: Run.** Expected: PASS.

### Task 4: Focus movement (rows and guide grid)

**Files:**
- Create: `js/focus.js`, `tests/focus.test.mjs`

**Interfaces:**
- Produces: `moveInRows(rows: number[] /* item counts */, pos:{row, col}, dir, memory: Map<row,col>) -> {row, col}`. Up and down restore the column last used on that row, clamped. At edges it returns the same position.
- Produces: `moveInGuide(grid: {channels: Programme[][]}, pos:{ch, anchorMs, progId}, dir, pageSize=6) -> pos`.
  - ←/→ move to the previous/next programme on that channel and set `anchorMs` to its start, clamped to `now−3h`.
  - ↑/↓ change channel and pick the programme containing `anchorMs` (not the start of the current cell).
  - `pageup`/`pagedown` move `pageSize` channels.
- Produces: `jumpGuideToChannel(grid, channelIndex, anchorMs) -> pos`, and `jumpGuideToTime(grid, pos, ms) -> pos`.

- [ ] **Step 1: Write failing tests:**
  - Rows `[5,8,4]`: from {0,3} down → {1,3}. Right ×3 → {1,6}. Up → {0,3} (memory). Down → {1,6}. Down → {2,3} (clamped).
  - Guide anchor: on BBC One, focus "EastEnders" 19:00–19:30 with anchor 19:00, then ↓ to a channel where a 18:00–20:00 film runs → focus is that film, and the anchor stays 19:00. ↓ again to a channel with 19:00 and 19:30 programmes → the 19:00 one.
  - ← from the earliest programme (window start) → unchanged.
  - `pagedown` from channel 2 with 15 channels → channel 8. `pagedown` from 12 → 14 (clamped).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.

### Task 5: Unified search

**Files:**
- Create: `js/search.js`, `tests/search.test.mjs`

**Interfaces:**
- Consumes: `catalogue`, `channels`, and `scheduleFor` from Task 2.
- Produces: `searchAll(query, nowMs) -> {top: Result|null, titles: Result[], channels: Result[], onTv: Result[]}`, where `Result = {type:'title'|'channel'|'listing', ref, label, sublabel, art, badge}`. Matching is case-insensitive on word prefixes. Channel numbers match by prefix. Listings come from now to now + 24 h, plus earlier catch-up listings (badge `'catchup'`).

- [ ] **Step 1: Write failing tests:**
  - `searchAll('simpsons')`: the top result is The Simpsons title with badge `'play'`, and `onTv` includes the Channel 4 18:00 listing.
  - `searchAll('sever')`: Severance with badge `'requested'` (or `'get'` for an un-requested title such as Andor via `searchAll('andor')`).
  - `searchAll('104')`: channels[0] is Channel 4.
  - `searchAll('')`: everything empty.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.

### Task 6: Design system, stage and Home

**Files:**
- Create: `styles/tokens.css`, `styles/components.css`, `js/ui.js` (DOM helpers: `h(tag, props, ...children)`, `art(url, fallbackKey)`), `js/screens/home.js`
- Modify: `js/app.js` (router: renders the layers from `nav.state()`)

**Interfaces:**
- Consumes: nav (Task 3), moveInRows (Task 4), and data (Task 2).
- Produces: `renderHome(root, ctx) -> {onAction(action) -> boolean, focusEl() -> Element}`. Every screen and overlay module exports the same `{onAction, focusEl}` contract. `ctx = {nav, now(), data, lite}`.

- [ ] **Step 1: Tokens.**
  - Type scale: 120/72/48/34/28/24 px. Font stack: `-apple-system, "SF Pro Display", "Inter", "Helvetica Neue", system-ui, sans-serif`.
  - Radii 14/20/28. Glass: `rgba(28,28,32,.62)` + `blur(28px) saturate(1.3)`.
  - Focus: `scale(1.07)`, `0 30px 60px rgba(0,0,0,.6)` shadow, and an animated sheen highlight.
  - Grain overlay (SVG turbulence) at 0.06 opacity, off in lite mode.
- [ ] **Step 2: Home.**
  - Top nav: Home · Live TV · Search · ⚙. Reachable with ↑ from the first row. OK opens the page; Home is a no-op.
  - Hero, about 58% of the height: the lit card's backdrop, full-bleed, cross-faded over 350 ms, with Ken Burns drift. Scrims on the left and bottom. Logo image if present, else the title in 96 px. Eyebrow (source · type), subtitle, and one synopsis line. No buttons.
  - If a video is playing, the hero shows *that* (with a "Now playing" eyebrow) until focus moves.
  - Rows: Up Next (16:9 cards with progress bar, requested badge with ring), On Now (16:9 cards: programme still, channel number + name, LIVE pill, progress), Watchlist (posters 2:3), Discover (posters with a ⤓ badge for "get").
  - The focused row scrolls so the focused card sits at a fixed x (≈ 96 px + one card) once past the first items. Rows translate vertically so the focused row sits at a fixed y.
  - OK: an episode plays it (nav.play vod); a channel tunes (nav.play live); a title opens show. Hold OK → actions overlay over the page.
- [ ] **Step 3: Verify visually.** Run `google-chrome-stable --headless=new --hide-scrollbars --window-size=1920,1080 --screenshot=/tmp/home.png http://100.109.40.12:8077/`, then read the PNG. Expected: real artwork in the hero and cards, one lit card, legible text, nothing overflowing the safe area. Repeat with `?lite`.

### Task 7: Guide, channel schedule and actions panel

**Files:**
- Create: `js/screens/guide.js`, `js/screens/channel-schedule.js`, `js/overlays/actions.js`

**Interfaces:**
- Consumes: moveInGuide/jumpGuideToChannel/jumpGuideToTime (Task 4), scheduleFor (Task 2), and numberEntry (Task 1).
- Produces: `actionsFor(item, ctx) -> [{id, label, hint, run()}]`. The lists come from the spec: for a programme, Watch / Watch from the start (catch-up) · Open the series · Next showing on TV · This channel's schedule · Remind me · Add to Watchlist. For a card or title: Play/Resume · Open the series · Add/Remove Watchlist · (episode) Mark watched. Live-playback actions are in Task 8.

- [ ] **Step 1: Guide.**
  - Top 40%: info for the lit programme (eyebrow "104 · Channel 4 · 18:00–18:30", 72 px title, badges ↺ / YOU WATCH / LIVE, synopsis), plus a live preview top-right (the current video's art with a LIVE pill, or the lit channel's art if nothing is playing).
  - Day strip: Now · Tonight 21:00 · Tomorrow · next 3 days. ≡/G focuses it, OK jumps the time.
  - Grid: 6 visible rows, a 2-hour window at 7.5 px/min, channel column 280 px.
  - **Lit cell = white fill with dark text, same size.** Past cells are dimmed. A red NOW line updates every 30 s.
  - The grid translates (doesn't jump) to keep focus in view.
  - Digits use numberEntry with a pill showing "1 0 _". Pg± page.
  - OK: now → play live. Past + catch-up → play vod (catch-up). Future → actions with Remind me focused.
- [ ] **Step 2: Actions panel.**
  - Glass panel from the right, 640 px wide, items 72 px tall, focus = white fill.
  - "Next showing on TV" closes the panel and calls `jumpGuideToTime` to the next airing of the same title on any channel (searching `scheduleFor`), with the grid gliding there.
  - "This channel's schedule" opens the `channel-schedule` page (a list grouped by day, current programme lit). Back returns to the same guide position (the guide keeps its `pos` in nav params).
  - "Open the series" opens `show` for `titleId`. Programmes with no `titleId` disable it.
- [ ] **Step 3: Verify.** Take headless screenshots of the guide at rest and with actions open (use a URL hash `#guide` and `#guide+actions` debug routes in app.js). Expected: as in the accepted mockups, with no growing tile and one lit element.

### Task 8: Playback layers (live and on-demand)

**Files:**
- Create: `js/screens/player.js`, `js/overlays/info.js`, `js/overlays/channels.js`, `js/overlays/endcard.js`, `js/overlays/nearend.js`, `js/overlays/number.js`

**Interfaces:**
- Consumes: nav, actionsFor, numberEntry, and scheduleFor.
- Produces: `player = {position(): ms, duration(): ms, paused, seek(deltaMs), ff()}`. A simulated clock. For live, the position is time-shifted relative to the wall clock and can't go past the live edge.

- [ ] **Step 1: Ground floor.** Full-bleed art for the programme or episode, with slow Ken Burns and grain. A tiny "simulated" corner tag only when the debug inspector is on.
- [ ] **Step 2: Live controls.**
  - OK → info bar (glass, bottom, 5 s auto-hide; ◂ ▸ focus ⏯ / Subtitles / Audio; red live-edge mark; "N min behind live").
  - ↑↓ → channel list (left strip, 7 visible rows, tunes after resting 1000 ms, OK tunes now).
  - Pg± → tune immediately, with a channel banner for 3 s.
  - Digits → number overlay, then tune (or "No channel 999").
  - The near-end card appears when ≤ 5 min remain (◂ ▸ choose Stay / Find something else).
  - Hold OK → live actions: Subtitles (toggles a subtitle line on screen) · Start over · This channel's schedule · Open the series · Audio · Add channel to favourites.
  - ⏯ pauses; ⏮/⏭ seek −10 s/+30 s.
- [ ] **Step 3: On-demand controls.**
  - Same info bar without channel elements. ↑↓ do nothing.
  - End card at the end (`f` jumps to end−10 s): 8 s countdown ring, ▶ Play now (S1 E5) / Back to Loki. Autoplay advances the episode and updates Up Next.
  - A film ending returns Home and removes it from Up Next.
- [ ] **Step 4: Verify.** Take screenshots via the `#play-live`, `#play-live+info`, `#play-live+channels`, `#play-vod+end` debug routes. Then run the judging walk-through by keyboard in a real browser window (Task 10).

### Task 9: Show page, Search screen and Settings

**Files:**
- Create: `js/screens/show.js`, `js/screens/search.js`, `js/overlays/keyboard.js`, `js/screens/settings.js`

**Interfaces:**
- Consumes: searchAll (Task 5), bestSource (Task 2), moveInRows (Task 4), and actionsFor (Task 7).

- [ ] **Step 1: Show page.**
  - Full-bleed backdrop and logo/title, meta line, synopsis.
  - Primary button ▶ Resume S1 E4 / ▶ Play / ⤓ Get it / "Requested · ⤓ 62%" (a ring). Then a secondary ＋ Watchlist. Then "Also on: Disney+".
  - For series: a season row (pills) and episode cards with stills, progress and watched ticks.
  - A "Next on TV: Tomorrow 18:00 · Channel 4" chip when `airsAs` matches a future listing (OK → guide at that slot).
  - ⤓ Get it switches to "Requested" and adds the title to Up Next with a progress ring that advances slowly.
- [ ] **Step 2: Search.**
  - Query field at the top and the on-screen keyboard (a–z, digit row, space, DEL) on the left.
  - Results on the right in groups: Top result (large), Shows & films, Channels, On TV. Updates on each key with a 150 ms debounce.
  - V fills "simpsons" letter by letter (mock voice).
  - OK on: a title → show page; a channel → play live; a listing → play (now or catch-up) or the guide at that slot.
- [ ] **Step 3: Settings.** A list: UI scale, Subtitles by default, Sources (connected service rows with status dots), Debug › CEC, Debug › A/V (placeholder pages).
- [ ] **Step 4: Verify.** Take screenshots of `#search=simpsons`, `#search=severance` and `#show=loki`. Expected: play vs get look different, and one lit element.

### Task 10: Walk-through, Pi check and README

**Files:**
- Create: `spikes/ux-redesign-prototype/README.md`

- [ ] **Step 1: Run all unit tests.** Run `node --test tests/`. Expected: all pass.
- [ ] **Step 2: Judging walk-through** (from the spec's "How we'll judge it"), in a headed `google-chrome-stable --kiosk` window or an equivalent Chrome DevTools Protocol-driven script that sends the keys and screenshots each state: Home → OK on Loki S1 E4 → `f` → end card → ⏭ → S1 E5 playing → Back → Home → On Now BBC One OK → ↓↓ → rest → tuned → G → ↓ to BBC One 19:00 EastEnders → hold Enter → Next showing → Back → Back → H → Search "simpsons" → OK → show page → Back → "severance" → Get it. At every screenshot, check that exactly one element has `.focused`.
- [ ] **Step 3: Pi readiness.** Load `?lite` at 1920×1080 in headless Chrome with `--disable-gpu`, and record via `performance.now()` that focus moves render within one frame of the transition start (no long tasks > 50 ms in the console log). **Do not touch the Pi.** Before the owner opens it on the Pi, message the other Baird sessions (rule: coordinate Pi tests), since the Pi browser competes with baird.service.
- [ ] **Step 4: README.** Cover how to serve; the keyboard ↔ remote map; debug keys (`k` inspector, `f` fast-forward, `?` legend); lite mode; and the **animation moments for the Lottie vs Rive comparison**: focus lift and sheen, layer slides, end-card countdown ring, LIVE pulse, request-progress ring, channel-tune transition, and the hero cross-fade.
- [ ] **Step 5: Hand over** the URL `http://100.109.40.12:8077/` (and `?lite`) to the owner.
