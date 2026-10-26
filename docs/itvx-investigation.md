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

## 8. Measured on the Pi (2026-10-26)

A probe Chromium (`spikes/netflix-tooling/itvx_harness.sh`: Baird's flags, muted, a `/tmp` copy of the browser profile, CDP 9557)
driven with `itvx_probe.py`. `baird.service` kept running. Screenshots were checked by eye.

| Question (§4) | Answer |
|---|---|
| Pages in a real browser | **Load.** `www.itv.com` refuses curl and headless Chrome (`ERR_HTTP2_PROTOCOL_ERROR`), but the Pi's normal Chromium gets every page. |
| Episode list | `__NEXT_DATA__` → `props.pageProps.seriesList[]` (`seriesNumber` as a string, `seriesLabel`, `titles[]`) lists **every** series and episode on one page (Vera: 14 series, 56 episodes, matching search). Per episode: `ccid`, `series`, `episode`, `episodeTitle`, `description`, `notFormattedDuration` (`PT1H29M`), `image` (template), `premium`, `tier`, `adRule`, `encodedEpisodeId.letterA`. `pageProps.programme` has `titleSlug`, `ccid`, `tier`. A film page has one entry, series `"others"`. |
| URLs | Programme `/watch/{slug}/{programme ccid}`; episode `/watch/{slug}/{programme ccid}/{encodedEpisodeId.letterA}` (e.g. `/watch/vera/mz0gq64/1a7314a0001`, from the page's own links); film and special `/watch/{slug}/{own ccid}`. A special under a parent programme also has its own page; the parent URL with the special's id falls back to the parent's page. A wrong slug redirects to the right one. |
| Sign-in | **Automated sign-in works, no captcha.** Play signed out → `/watch/user/signin` → email (`input[type=email]`) → CONTINUE → `/watch/user/email-pair` (a code is **emailed by default**) → link "Use password instead" (`/watch/user/signin?step=password`) → `input[type=password]` → SIGN IN → back on the title's page. Signed in: an `Itv.Session` cookie (visible to JS) and a button `aria-label="Open account menu"`. |
| Cookie banner | Buttons ACCEPT / REJECT / MANAGE; a script click on REJECT works. |
| Starting a play | Pages do not autoplay: press `button.videoPlayer__play-btn` (WATCH NOW / PLAY). A title with guidance ("G") then shows a dialog "Parental Controls are off" in `div.dialogs-container-wrapper`: press its WATCH NOW. |
| Video | Like Channel 4: the programme is a `blob:` `<video>` whose `duration` is the real length (4990.8 s for a 1 h 23 m film, 5360 s for a 1 h 29 m episode) and `currentTime` the real position. Adverts play in OTHER `<video>` elements (`ar.prd.content.itv.com`) while the programme waits paused at 0. |
| Resolution | 1024×576 (film), **1280×720** (Vera). Better than Disney+ (854 px). |
| Seek, pause | `currentTime += 120` and `-= 60` resumed at once (buffer rebuilt from the new point, no stall). `pause()` / `play()` work. |
| Adverts | The film (adRule not checked): ~2 min of pre-roll (4 s, 60 s, 30 s, 10 s, 10 s clips). Vera (`adRule: NEVER`): only ~9 s of ITV idents. Mid-rolls not seen (not watched long enough). Ad handling is deferred; the engine only has to show the picture and not count an advert as the programme. |
| CPU | Probe Chromium ~80–110 % of one core while playing. The Pi was also short of memory with the probe running (Baird 3.6 GB resident, kswapd busy). |

**Not measured:** the end of a programme and ITVX's next-episode behaviour, subtitles, mid-roll adverts, what the controls overlay looks
like when woken (none showed while playing untouched; the page shows a scrollbar, which the page style must hide).

**Device registrations:** the probe signed in once in a profile copy, which was then deleted. Baird's own profile will register a second
device when it first signs in.

## 9. Built (branch `itvx-integration`, 2026-10-26)

| Piece | Where | What it does |
|---|---|---|
| Search | `ItvxCatalogue.SearchAsync`, `ItvxProvider.SearchAsync` | Public API over plain HTTP, so it works while anything plays. Drops Premium (`tier: PAID`) and live channels. A programme is a container (`itvx:{slug}/{ccid}`); a film or special plays from its own page (`itvx:ep:{ccid}`). |
| Episode lists | `ItvxService.GetProgrammeAsync`, `ItvxCatalogue.ParseProgramme`, `ItvxProvider.GetChildrenAsync` | An in-page `fetch` of the programme page from an itv.com page in the shared browser (`ItvxPlayerControl.ProgrammeFetchScript`), which returns the `__NEXT_DATA__` fields Baird reads. Works while ITVX plays (the fetch runs in the playing page); refused while another service plays. Series folders in ITVX's order and labels (`itvx:{slug}/{ccid}\|{series}`); Premium episodes dropped. Reused for 10 minutes. |
| Sign-in | `ItvxService.SignInAsync`, `ItvxCredentials` (`ITVX_USER`, `ITVX_PASS`) | Only when a page asks (Play lands on `/watch/user/...`). Email, CONTINUE, "Use password instead" (pressed again every 3 s while the code page stays: a press 2 s after it appeared did nothing on the Pi), password, SIGN IN. After a refused password it does not try again in the same run. The session lasts in Baird's browser profile. |
| Player | `ItvxPlaybackEngine`, `ItvxPlayerControl` | Opens the title page, presses Play, answers "Parental Controls are off", signs in if asked (then opens the title again), waits for a video. Programme = the `blob:` video; advert = any other playing video (the HUD position stays put, seeks are refused). Native seek, pause and play. Leaving the title's URL or the video ending ends the stream. No ad-break handling beyond that (deferred). |
| Page style | `Resources/itvx-page-style.js` | Only the playing video shows, full screen; no scrollbar, no pointer. |
| Wiring | `MainView.axaml.cs`, `MainViewModel` (`ItvxEngine`), `VideoPlayer.AddBrowserEngine` | ITVX is always in search; it plays through Baird's normal player in the shared browser. |
| Autotest | `BAIRD_AUTOTEST=itvx-core` (`pi_autotest.sh itvx-core test 180`) | Search "Vera" (`BAIRD_AUTOTEST_ITVX_QUERY`), first programme, first series, first episode; waits out the adverts (up to 4 min), samples the position and seeks 60 s. |

**On the Pi (test build, real profile):**
- Run 1: search (7 results), episode list (14 series), Play pressed; the sign-in stopped at the emailed-code page (the "Use password instead" press came too early). Fixed with the retry.
- Run 2: signed in automatically in 6 s, answered the parental prompt, video 20.7 s after Play. Then a minute of adverts for Vera (so `adRule: NEVER` does not mean no adverts), shown full screen with Baird's overlay on top; Baird's "Opening the programme…" covers the sign-in pages, so the viewer never sees them.
- Run 3: already signed in; episode list in 372 ms; video 8.2 s after Play.
- Run 4: failed. The first Play press came before the page was ready, the parental prompt only came on the third press, and after it
  was answered the page went back to its Play button with no presses left. Fixed: Play is pressed every 5 s while the page shows it
  and nothing plays (up to 8), and the press after the parental prompt waits a full gap.
- Run 5: **passed.** Episode list in 344 ms; Play, parental prompt, video 13.9 s after Play (no adverts this time); position 4.8 → 9.8
  → 14.8 s; a 60 s seek landed at 77.3 s and kept playing (82.3, 87.3 s). Screenshots after the seek show the episode full screen,
  moving, with no ITVX page showing. Browser CPU while playing: ~63–75 % (top, all Chromium processes).

**Not done yet:** subtitles (`SetSubtitle` does nothing), ITVX's "Skip intro" / next-episode prompts (`IPromptingEngine`), what
happens at the end of an episode (the engine ends the stream if the URL changes or the video ends; not seen on the device), mid-roll
adverts, and live channels.
