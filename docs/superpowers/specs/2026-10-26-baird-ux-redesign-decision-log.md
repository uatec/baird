# Baird UX redesign — decision log

How we got to the prototype in `spikes/ux-redesign-prototype/` (served by `serve.sh` on `http://100.109.40.12:8077/`), so
the work can be picked up again. Design: `2026-10-25-baird-ux-redesign-prototype-design.md` (revision 2 at the end).
Plan: `../plans/2026-10-25-baird-ux-redesign-prototype.md`. "Owner" = decided by the owner; "Claude" = decided while
building and open to change.

## Why

Two animation spikes (Lottie, Rive) exist. Before choosing one, design the UI/UX properly and see what the design needs.
The owner wants Baird to feel like Apple TV, with live TV and on-demand as equal partners.

## Direction (owner)

- Power-on shows **Home**. Rows: Up next, On now, Watchlist, Discover. Rejected: a "Now line" (time imposed on
  on-demand) and "everything is a channel". TV and on-demand are different, but getting into them works the same way.
- **One catalogue** across services (TVDB/TMDB, possibly via Seerr). The same search and discovery for watchable and
  requestable titles.
- **Guide = the classic grid** (many channels at once). Make it premium through visuals, not new UX. Rejected: a lit
  tile that grows. **Hold OK = actions** everywhere.
- **Arrows never open panels**; specific buttons do. G20S Pro map: ≡ = Guide, Pg± = channel ±, OK = info bar,
  ▴▾ = channel list, digits tune, DEL corrects, voice = search.
- **Back retraces the journey.** Overlays → video; pages → Home; the bare video → the page it was started from.
- The prototype is tried on the PC and full-screen in the Pi's browser.

## Revision 2 (owner feedback after trying it)

- Focus must be obvious (lift, ring, dim neighbours). Tabs must behave like tabs (move = switch, no OK), visible on
  every top-level place including Settings. Back from inside a place → its tab → Home.
- Guide motion too fast and flickery → a sliding highlight, edge-only scrolling, crossfading header.
- **All showings** replaces "next showing". "This channel's schedule" removed from the guide (circular).
- **Columns experiment** for the guide (channels across, time down), switchable.
- **Remind me** must say what and when, and warn before switching (countdown card; switches unless dismissed).
- Live TV: scrub back with ◂ ▸; channel list shows times and a to-scale timeline; channel changes have a tuning
  transition (DVB takes 1–2 s).
- Artwork: the guide data has no images. Match titles to TMDB/TVDB. Fallbacks: image → series image → branded channel
  card → typographic card. Channels with no guide data are shown honestly.
- Watchlist entries can be a film, series, season or episode.
- Every series has real seasons and episodes (fetched from TMDB).
- **Availability is per source, per season** (owner raised "Not in your library"): "Seasons 1–2 on Netflix · Season 3
  can be requested". Requests go to Seerr and download to Jellyfin, then appear in Up next.

## Revision 3 (owner feedback)

- No flicker: transitions must work **out and between**, not only in. Places crossfade; pages and overlays animate
  out; the picture crossfades only once the new one has decoded; tuning from the channel list keeps the list.
- **The lit item is always fully on screen.** This is one shared capability (`keepInView`), enforced by an automated
  check.
- **One Request… button** opens a sheet (this episode / this season / a season / all missing). Season tabs show a
  *state* ("Not streaming", "Downloading 40%", "Disney+"), never a call to action.
- **Single episodes can be requested**, and show their download percentage like films.

## Claude's calls (check these)

- Font: Instrument Sans, bundled. The one bold visual idea: "ambient light" from the artwork (pre-blurred, cheap on the Pi).
- Guide scale: 2 hours across the screen. Programme titles stay pinned at the window edge.
- OK on a film plays it; OK on a series opens its page, unless it's in progress (then it resumes).
- OK on a future programme sets or cancels a reminder directly.
- ≡ while watching opens the guide **over** the video (Back returns to the picture); elsewhere it switches to the
  Live TV tab.
- Lite mode (Pi) is detected from the WebGL renderer (Broadcom V3D / software rendering). The user agent says x86_64
  on all Linux, and `userAgentData` needs https.
- Up next is data-driven and saves your position as you watch. Playing what's already playing returns to it.
- **Seerr requests whole seasons.** "This episode" is shown in the prototype but would need Sonarr directly. Not verified.
- TMDB artwork is **not committed** (public repo, copyright, 28 MB). Recreate with
  `node tools/fetch-art.mjs && node tools/fetch-episodes.mjs && tools/make-ambient.sh`.

## How it's checked

- `npm test` (node --test): keys, data/schedule/availability, navigation, focus, search, device detection.
- `node tools/regress.mjs`: behaviour checks in a real browser over CDP (port 9333, never 9222). Covers overlay keys,
  catch-up, Back, tabs, reminders, showings, requests, transitions (no flicker), and the lit item always on screen.
- `node tools/walkthrough.mjs`: the whole journey in real time; exactly one lit element and fully on screen at every step.
- Shared PC: run the browser checks only outside other sessions' "isolated" timing windows.

## Open

- **Lottie vs Rive vs Avalonia-native**: the prototype's motion is CSS. See the discussion after revision 3.
- Not yet tried on the Pi's own browser with the real remote (check ≡ with the K key inspector).
