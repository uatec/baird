# One data layer: no hangs, instant pages, everything reachable already warm

## Intent
Opening a series or film from Home often takes ~10s or never finishes. The owner wants pages to open at once with fully populated data, loaded before they get there, without disturbing video playback. Their priority queue / low-priority thread idea was a suggestion; the final design below does include a refresh queue because they asked for stale data to be refreshed by priority.

## Goals
- Every page loads through one cached data layer with two lifetimes, serve-stale, and a prioritised refresh queue.
- Cold (no data at all): a spinner; fully populated target 3s; hard error at 8s. Never an error when stale data exists.
- Warm: the page is **fully populated** (title, art, synopsis, every episode's name, still, synopsis) immediately. A spinner, skeleton or shell does not count as ready.
- Whatever a page links to directly is warmed before the viewer gets there.

## Non-goals / anti-goals
- No OS thread-priority work.
- Warming never competes with playback or navigation.
- Never persist secrets (Jellyfin token in image URLs) or stream URLs. Never warm stream resolution (yt-dlp): see the home-return stall fixed in `ce6ff50`.
- Prewarming is one hop only: never the pages reachable from a warmed page.

## Cause found (recon)
`ShowData.ComputeAsync` runs Seerr lookup -> status -> library seasons in sequence. Only the Seerr search has a timeout (`CatalogueSearch.Within`); `StatusAsync` and `SeasonsFromAsync` have none, so one hung call stalls the page. Seerr-only titles get no real episode data (`ShowData.Merge` fills "N. Episode N" placeholders); full data needs a new Seerr season-details call.

## Design
**1. Two lifetimes, both serve-stale.**
- *Near-real-time* (availability: library sources, Seerr status, download progress): cached 60s, in memory only. Past 60s it is still served while refreshed. There is no upper limit on its staleness (owner's decision; accepted risk: a hung provider can leave an old Play/Request state up). Watched/resume state is local history, always live.
- *Non-real-time* (metadata and images: names, synopses, art URLs, seasons, episodes, Seerr season details): cached 7 days, persisted under `~/.baird/show_cache/`, one small file per title (secrets tokenized as `JsonMediaDataCache` does, versioned so a format change just discards it), saves debounced to about every 30s to spare the SD card, bounded by LRU at about 5,000 titles. The Pi has 9.8 GB free; the cache stays well under 100 MB. Past 7 days it is still served while refreshed.
- A refresh that times out or fails never replaces, evicts or blanks what's there.

**2. Loading states, one rule everywhere.** No data at all -> a spinner (repo's `loading-ring`) with the card's own title/art/synopsis where known; 3s target for fully populated; at 8s with still nothing, an error with retry. Stale data present -> shown at once, no spinner, no error. Availability not yet known -> a neutral "–" (no wording; the old "Checking your library…" text is removed). Cards that already carry a source can Play immediately; Seerr-only cards wait on the "–".

**3. Lazy availability, per-piece timeouts.** The page draws from cache first. Availability fills in as each piece answers (Seerr status, each library source, the season in view), each with its own 8s timeout; a hung one leaves its badge on "–".

**4. Refresh and warm through one prioritised queue.** One worker, one job at a time with a short gap, deduplicated by key, bounded (about 200 entries). Stale-served data is queued for refresh rather than left stale. Work is submitted in **batches**; a newer batch always runs ahead of older ones. Within a batch the order is: (1) the current page's own data; (2) its children (the pages it links to directly); (3) its siblings (the other pages reachable from the page you came from; these may already be queued, so they merge). Opening a page or lighting a season tab that isn't loaded jumps ahead of everything. Going back submits a new batch, so pages forward of you are warmed before pages behind you.

**5. What each page warms (one hop).** Home: the pages its cards open, in row order (continue watching, up next, downloads, then per-service trending); channels and play-only cards are skipped. Show page: its seasons, starting with the season containing the next episode (first available for an unwatched show, by the existing `OpeningSeason` rule using local history), then nearest-first outward; episode stills. Search: its result pages. Artwork: the in-memory `ImageCache` stays, backed by an on-disk image cache capped at about 1 GB, oldest dropped first, so stills survive restarts (builder to check whether the image loader has a disk-cache option, else add a small one).

**6. Don't touch playback.** The worker pauses entirely if the player reports dropped frames, and throttles to one job per 1-2s while video plays. It stops completely when Home is closed during full-screen playback. Builder to find the playing/dropped-frames signals in `PlaybackController`.

## Staging (ship separately)
1. Timeouts, per-piece availability, spinner + 3s/8s rule, hang fixed.
2. The two-lifetime cache with serve-stale, disk persistence, Seerr season details, refresh queue.
3. One-hop warming from every page.

## Success (measured on the Pi, before/after reported)
Warm titles are fully populated within 0.5s of opening. Cold titles fully populated within 3s, hard error at 8s. No page waits on a hung provider beyond 8s. Video playback shows no new dropped frames while warming.

## Open reading to confirm
"Siblings" = the other pages reachable from the page you came from (e.g. neighbouring cards in the Home row).

## Left to the builder
Queue/gap/batch constants (tune on the Pi); the playing signals; tests alongside `PrefetchTests` and the `DataService*` tests (hung provider; cache-miss spinner then error at 8s; stale served and refreshed; failed refresh keeps old data; secrets never written; newer batch runs first; one hop only; paused on dropped frames).

## Decisions recorded
Stale served always, 7-day refresh age for metadata and 60s for availability, no shorter life for airing shows, LRU-only eviction, silent failed refresh, Play immediate for cards carrying a source, "–" on season state labels and the Request/Play button text only, Seerr season-details call added, video rule: one job at a time with a gap, stop on dropped frames, staged delivery (1 hang/spinner, 2 cache+queue, 3 one-hop warming).
