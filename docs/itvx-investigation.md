# ITVX in Baird: investigation

Status as of 2026-10-25. Desk research and probes from the dev PC only. Nothing has run on the Pi yet. Compare
[disney-spike-findings.md](disney-spike-findings.md) and [netflix-spike-findings.md](netflix-spike-findings.md); method in
[spike-strategy.md](spike-strategy.md).

## Decisions (user, 2026-10-25)

- **Premium titles are excluded.** Drop search results with `tier: PAID` and episodes with `premium: true`.
- **Ad-break handling is deferred.** The first version plays the free tier as it is. The spike still records what a break
  looks like (§4 item 5), so the later work has facts to start from.
- Credentials: `ITVX_USER` / `ITVX_PASS` are in `~/.baird/config.ini` locally and on the Pi, with placeholder values
  (`your-itvx-email@example.com`) until the user fills them in. Backups: `config.ini.before-itvx`.

## TL;DR

| Question | Answer | Evidence |
|---|---|---|
| Can mpv play ITVX, like BBC iPlayer? | **No.** Every on-demand title is MPEG-DASH with Widevine. | Search API `variant.featureSet` lists `widevine` for every result tried (The Chase, Paddington, Mr Bates, Vera). The Kodi add-on viwx plays it only through inputstream.adaptive + Widevine. |
| So which route? | **The Disney+ route:** `ItvxService` on the shared `BrowserHub`, `ItvxProvider`, `ItvxPlaybackEngine : IBrowserPlaybackEngine`. Chromium on the Pi already has a working Widevine CDM (Netflix, Disney+). | |
| Search | **Easier than Netflix/Disney+.** A public JSON API answers with no sign-in, no browser and no token, in ~90 ms. Plain `HttpClient`, like `BbcIPlayerService`. | `GET https://textsearch.prd.oasvc.itv.com/search?...&query=vera` → 200 in 0.086 s from the dev PC. |
| Artwork | Public. | `ovp.itv.com/images/...` template with `w=320&h=180&q=80&blur=0&bg=false` → 200 image/jpeg. |
| Episode lists | Must be read **in the browser**. | `www.itv.com` (which serves the programme page and its `__NEXT_DATA__`) stalls for non-browser clients (bot protection at the CDN). The API hosts (`textsearch…`, `magni.itv.com`, `ovp.itv.com`) answer normally. |
| Sign-in | A free ITVX account is needed to play. | viwx signs in with `auth.prd.user.itv.com/v2/auth` and sends the token to the playlist API. Baird would sign in through the web page, as it does for Disney+. |
| Region | UK only. | The Pi is in the UK; not a problem at home. |
| Ads | **Free tier has ad breaks.** ITVX Premium has none. | `supportsAdPods` in the player's playlist request. This is the largest unknown for the engine (see Risks). |
| Live ITV1–4 | Already in Baird through DVB/TvHeadend. ITVX adds catch-up, box sets, films and its FAST channels. | |

## 1. Search API (public)

```
GET https://textsearch.prd.oasvc.itv.com/search
    ?broadcaster=itv&platform=dotcom&onlyFree=false
    &featureSet=mpeg-dash,widevine,outband-webvtt,hd,single-track
    &query=TERM
```

Reply: `results[]`, each with `entityType` (`programme`, `special`, `film`; `simulcast` for live channels) and `data`:

- `programme`: `programmeCCId` (programme id, e.g. `z6wp7kv`), `programmeTitle`, `synopsis`, `genre[]`,
  `totalAvailableEpisodes`, `latestAvailableEpisode.{episodeCCId, imageHref}`, `tier` (`FREE` or `PAID`).
- `special` / `film`: `specialCCId` / `filmCCId`-style id, `specialTitle` / `filmTitle`, `productionId`
  (e.g. `10/0469/0004#004`), `imageHref`, `tier`. `special` can carry `specialProgramme` (its parent programme).
- `imageHref` is a template: `{width}`, `{height}`, `{quality}`, `{blur}`, `{bg}`, sometimes `{treatment}` and `{fallback}`.
- `tier: PAID` means ITVX Premium. Baird drops these (see Decisions).

Maps to Baird: `programme` → `MediaType.Brand` (children = episodes); `special`/`film` → `MediaType.Video`. Ids `itvx:{ccId}`.

## 2. Programme page (episodes): browser only

- URL: `https://www.itv.com/watch/{slug}/{programmeId}`, an episode `…/{programmeId}/{episodeId}`. The slug is the title
  lower-cased, `&` → `and`, spaces → `-`, and `#/?:'` removed (viwx `build_url`). The site redirects a wrong slug, so the
  slug matters less than the id.
- The data is Next.js `__NEXT_DATA__` → `props.pageProps.seriesList[].titles[]` (from viwx `parsex.py`; **not yet seen by us**).
  Per title: `series`, `episode`, `episodeTitle` / `heroCtaLabel`, `longDescription`, `image` (template),
  `notFormattedDuration` (ISO 8601, e.g. `PT46M`, which gives a real length for the HUD, unlike Disney+),
  `premium`, `playlistUrl`, `encodedEpisodeId.letterA` (the id in the episode URL).
- Read it the Disney+ way: navigate the shared tab, or `fetch` the page from inside an `itv.com` tab and parse the
  `__NEXT_DATA__` script. Cache per programme like `DisneyService` does (10 minutes).
- Not tried: fetching `www.itv.com` from Baird's own `HttpClient`. **Don't.** Getting past bot protection with spoofed
  headers is fragile and against the site's intent. The browser is the legitimate client.

## 3. Playback: the browser engine

- The web player asks `POST https://magni.itv.com/playlist/itvonline/ITV/{productionId as 10_0469_0004.004}` (Accept
  `application/vnd.itv.vod.playlist.v4+json`, body with the user's token and `drm: {system: widevine, maxSupported: L3}`).
  It gets a DASH manifest and a Widevine licence URL back. Baird does not need to call this; the page does it.
- So `ItvxPlaybackEngine` opens the episode URL in the shared tab, hides ITVX's controls with a page style
  (`itvx-page-style.js`), polls the playing `<video>` and drives it, as `DisneyPlaybackEngine` and `DisneyPlayerControl` do.
- Widevine L3 in Chromium on Linux: expect SD, as with Disney+ (854 px). viwx asks `hd` with L3, so ITV may serve higher.
  Measure it.
- Prompts: ITVX has "Skip intro" and a next-episode card. `IPromptingEngine` already covers that shape.

## 4. Things to find out on the Pi (spike pass criteria)

1. **Sign-in** with `ITVX_USER` / `ITVX_PASS` in `~/.baird/config.ini`: form selectors, a cookie banner, any "Are you
   16+?" or profile step. Pass: signed-in shell visible, screenshot.
2. **Episode list** read from `__NEXT_DATA__` in the shared tab for one programme. Pass: series, episodes and durations logged.
3. **Playback** of one episode. Pass: `currentTime` advancing between polls, a screenshot of moving picture, the width logged.
4. **Is `video.currentTime` the title position?** Disney+ broke this. ITV's DASH player may run ads in the same `<video>`,
   which would move or reset it.
5. **Ads:** what the page shows in a break (DOM markers, a countdown), whether seeking is blocked in a break, and whether
   `duration` includes ads. Decide what the HUD shows during a break (suggest: "Ad break 0:45" and ignore seeks).
6. **CPU:** the Pi 5 has no H.264 hardware decode in Chromium either. Compare with Disney+ at the same resolution.

## 5. Implementation shape (after the spike)

| Piece | Model it on | Notes |
|---|---|---|
| `ItvxCredentials` | `DisneyCredentials` | `ITVX_USER`, `ITVX_PASS`. |
| `ItvxSearch` (pure parser) + `ItvxProvider` | `DisneySearchParser`, `DisneyProvider` | Search over plain HTTP, so it works **while Netflix/Disney+ plays**: no `ActivePlayback` check for search. |
| `ItvxService` | `DisneyService` | Sign-in, episode-guide read, caching; registers with `BrowserHub` (page owner "ITVX"). |
| `ItvxPageClassifier` | `DisneyPageClassifier` | Pure, tested. |
| `ItvxPlaybackEngine`, `ItvxPlayerControl` | `DisneyPlaybackEngine`, `DisneyPlayerControl` | `CanPlay`: `https://www.itv.com/watch/…`. No ad-break handling yet (deferred). |
| `Resources/itvx-page-style.js` | `disney-page-style.js` | Hide ITVX controls and the pointer. |
| Wiring | `MainView.axaml.cs:107` (service, provider), `MainViewModel.cs:161` (engine), `MainView.axaml.cs:341` (`AddBrowserEngine`) | Warm-up sign-in like `DisneyService.Start`. |
| Autotest | `BAIRD_AUTOTEST=disney-core` | `itvx-core`: search, play the first programme's latest episode. |

Channel 4 (`Channel4Service`, `Channel4Provider`, `Channel4PlaybackEngine`, merged 2026-10-25) is the newest example of the same shape; check it before copying the Disney+ code.

This is less work than Disney+ was: `BrowserHub`, `IBrowserPlaybackEngine`, `IPromptingEngine`, the transparent main window
and the Pi tooling all exist. The new parts are the ITVX selectors, the page data and ad breaks.

## 6. Risks

- **Ads** (free tier): the one thing Netflix and Disney+ never had. They could break position tracking, seeking and the
  "title ended" detection. Premium avoids them.
- Private web structures (`__NEXT_DATA__` shape, `data-testid`s, player DOM) can change without notice. The search API is
  also unofficial, but it is the same one the ITVX website uses.
- Real account from a non-standard browser: one profile, don't churn it (device registrations), as with the others.
- Terms of service not reviewed.

## 7. Sources

- Probes from the dev PC on 2026-10-25 (search API, image CDN, `magni.itv.com` reachable, `www.itv.com` not reachable without a browser).
- viwx, the open-source Kodi ITVX add-on: <https://github.com/dimkroon/itvx-for-kodi> (v1.7.5, 2025-12). Files
  `resources/lib/itvx.py` (playlist request), `parsex.py` (page and search parsing), `itv_account.py` (sign-in).
