# Disney+ on the Pi: findings

Status as of 2026-10-23. Two spikes, built like the Netflix ones: playback and search/listings. Method and tooling:
[spike-strategy.md](spike-strategy.md). Netflix comparison: [netflix-spike-findings.md](netflix-spike-findings.md).

## TL;DR

| Thing | State | Evidence |
|---|---|---|
| Disney+ sign-in with configured credentials | **Works** | Unattended test on the Pi: email, "Enter a password instead", password, "Who's watching?", home. |
| Disney+ playback in Baird on the Pi | **Works** | Unattended test: `video.currentTime` advanced (20.12 s to 20.63 s), window revealed over Baird, screenshots show moving picture with the Baird HUD. 854 px wide (Widevine L3 gives standard definition). |
| Disney+ search via Disney's internal API | **Works** | 6 results for "Mandalorian" in ~2.3 s (including browser sign-in check and token capture), with artwork, movie/series, rating and year. Screenshot shows the result grid on the TV. |
| Playing a search result | Movie: works by URL. Series: play URL found and proven | See §3 "Series and episodes". |
| Remote-control exit from the player | Not done | Same gap as Netflix. |
| HUD end time / remaining time | Shows `--:--` | Disney+ gives no real duration in the page; needs `runtimeMs` from the API (see §2). |
| CPU / smoothness over a full title | Not measured | |

Everything was found with the account in `DISNEY_USER` / `DISNEY_PASS` (region GB, locale `en-gb`).

## 1. Sign-in

Config keys in `~/.baird/config.ini`: `DISNEY_USER`, `DISNEY_PASS`, optional `DISNEY_PROFILE` (name on "Who's watching?", first
profile if unset) and `DISNEY_TARGET_URL` (title for the playback spike).

The flow (selectors observed on the live site; code in `Baird/Services/DisneySession.cs`):

1. `https://www.disneyplus.com/identity/login/enter-email`: `input#email` (type email), then `[data-testid=continue-btn]`.
2. `/identity/login/passwordless` ("We need to confirm you're you"): Disney+ offers an emailed one-time code **by default**, but
   `[data-testid=passwordless-login-with-pwd-btn]` ("Enter a password instead") switches to a password. Without that button
   the credential flow would not work.
3. `/identity/login/enter-password`: `input[type=password]`, then the submit button whose text is "Log In".
4. `/profiles/loading`, then `/select-profile` ("Who's watching?"): tiles are `[data-testid^="profile-avatar-"]` with
   `aria-label="Sign in to NAME's profile."`. No PIN was asked for the profile used.
5. `/home`. The signed-in app shell has `[data-testid^="navigation-item-"]` links.

One sign-in attempt per run. The password is typed with CDP `Input.insertText` and is never logged (tested).

### Traps found while testing (each one failed a Pi run)

- **A signed-out visitor is not redirected to the login page.** Opening `/browse/search` signed out shows a 404 page
  ("The page you were looking for cannot be found."). A URL-only classifier said "signed in" and the flow then timed out.
  The classifier now needs the signed-in app shell (the navigation bar), and the 404 page, or 30 s of unrecognised pages,
  counts as "signed out" and starts the sign-in.
- **A signed-in session that opens a login URL is redirected on, so `/identity/...` alone does not mean "signed out".**
  After the Pi was power-cycled, the playback test landed on an `/identity/...` URL, started a sign-in, and failed with
  "the email field did not appear on the login page", although the session was valid (the login URL redirects to
  `/profiles/loading`, then `/select-profile`). Now only a visible email or password field counts as the login flow, and the
  sign-in step stops without typing if the login page redirects away. Re-tested on the Pi: sign-in, picker, playback and search all pass.
- **The `/login` URL can hang on a spinner** ("Content is loading") on a brand-new profile. `/identity/login/enter-email` is reliable.
- **Disney+ navigates itself during sign-in.** CDP then answers `Inspected target navigated or closed` to a page script.
  Polling calls treat that as "no answer, try again".
- **`/select-profile` appears before its tiles render.** Clicking then finds nothing. The picker step retries until tiles exist.
- **Cookie banner**: the same OneTrust `#onetrust-accept-btn-handler` as Netflix; it is clicked on every state read.

## 2. Playback

- Code: `Baird/ViewModels/DisneySpikeViewModel.cs`, tab "Disney Spike", control `Baird/Controls/DisneySpikeControl.axaml`.
  It shares the browser profile, the `baird-netflix` window class and the labwc rule with the Netflix spikes, so no new
  Pi configuration is needed. CDP port **9224**. Never run it together with a Netflix spike (profile lock).
- Play URL: `https://www.disneyplus.com/en-gb/play/{entityId}`. For a movie the entity id from search is the play id (the
  page's PLAY button links to it). The default target is the movie "Star Wars: The Mandalorian and Grogu"; set
  `DISNEY_TARGET_URL` for another title.
- **Two `<video>` elements.** The first stays empty (`readyState 0`, no source), and the second plays (`blob:` source).
  `document.querySelector('video')` therefore reads time 0 forever. The spike picks the video with a `currentSrc`, and
  `baird-overlay.js` (the HUD) now does too. Before this fix the HUD showed "Paused 00:00" over a playing picture.
- **`video.duration` is not the length of the title.** It starts as `Infinity`, then becomes the end of the *buffered* range and
  grows while playing (measured on an episode: 23 s, 36 s, 44 s, 52 s, 60 s), and `seekable.end()` has the same value. The
  HUD therefore showed "-Infinity:NaN:NaN" and "Ends at Invalid Date". The real length is only in the API (`runtimeMs`, e.g.
  8062084 ms for the film). `baird-overlay.js` now shows `--:--` and no end time on `disneyplus.com`, unless the page sets
  `window.bairdRuntimeSeconds` (Baird can set it over CDP once it knows the runtime). Elapsed time and Playing/Paused are right.
- Widevine: the same trap as Netflix. A **brand-new profile** has no CDM until Chromium's component updater downloads one
  and the browser is restarted; until then the player spins for ever. Baird's profile already had the newer CDM
  (4.10.3112.0), so `WidevineStager` left it alone and playback worked at once.
- The page chain that was observed (all HTTP 200): `disney.playback.edge.bamgrid.com/v7/playback/ctr-regular`, the Widevine
  certificate `playback-certs.bamgrid.com/static/v1.0/widevine.bin`, the manifest on `*.media.dssott.com`, then
  `.../widevine/v1/obtain-license`.
- Playback is confirmed only when `currentTime` is above 0.1 s **and** increases between two polls. A failure ends in a
  red FAILED state with the last page URL and text, and in the journal.
- **Resume**: a title the account has started shows "CONTINUE", and `/play/{id}` resumes from the saved position. The
  test therefore started at 20 s, not 0 s.
- Subtitles are on, following the profile's setting.

## 3. Search (listings)

- Code: `Baird/ViewModels/DisneySearchSpikeViewModel.cs`, `Baird/Services/DisneySearch.cs`, `DisneySession.SearchAsync`,
  tab "Disney Search". CDP port **9225**.
- **Endpoint:** `GET https://disney.api.edge.bamgrid.com/explore/v1.21/search?query=TERM`, a plain REST call (not GraphQL).
- **Auth:** a bearer token (`authorization` header) plus `x-application-version`, `x-bamsdk-client-id`, `x-bamsdk-version`,
  `x-bamsdk-platform`, `x-dss-edge-accept: vnd.dss.edge+json; version=2`. The web app keeps the token in memory (no cookie
  or storage entry was used), so the spike opens the app's own search page, reads the headers of the first
  `/explore/` request with CDP `Network.requestWillBeSent`, and replays them with `fetch` from inside the page. The
  token is never logged (tested). The device/session token refresh (`graph/v1/device/graphql?op=refreshToken`) is done by
  the web app itself.
- **Reply:** `data.page.containers[].items[]` where `type == "view"`. Per item:
  - `id`: the entity id.
  - `visuals.title`, `visuals.description.{brief,medium,full}`.
  - `visuals.metastringParts.ratingInfo.rating.text` (e.g. "12+"), `releaseYearRange.startYear`, `runtime.runtimeMs`, `genres.values`.
  - `visuals.artwork.standard.tile["1.78"].imageId`. Image URL, copied from the web app's own `<img>` tags:
    `https://disney.images.edge.bamgrid.com/ripcut-delivery/v2/variant/disney/{imageId}/compose?format=webp&width=320`.
  - Movie or series is **only** in the base64url `infoBlock` (a protobuf containing `urn:ds:cmp:eva:movie` or `...:series`).
    `DisneySearchParser.KindOf` reads it; this is a heuristic and returns null if the shape changes.
- **Cost:** ~2.3 s end to end on the Pi when the profile is already signed in (browser start, session check, token capture,
  call). The API call alone is much faster. Keeping the browser alive between searches would remove most of the cost.
- The search result **includes trailers, podcasts and extras** (e.g. "Disney+ Insider ..."), not only films and series.
- `/en-gb/search?q=` is **not** a route (404). The search page is `/en-gb/browse/search`.
- Failure handling: 401/403 and any other HTTP error end in `FAILED: ... HTTP 401`; a missing captured request ends in
  `FAILED: could not capture the Disney+ web app's API request`. Neither returns an empty list silently.
- **Series and episodes** (probed on the Pi, then one episode played with `currentTime` advancing at 854 px):
  - A search result of kind `series` has a *series* id. `/play/{seriesId}` is not the way to play it.
  - `GET /explore/v1.21/page/entity-{seriesId}?limit=15` (same headers as search) returns the page. Its `actions[]` has a
    `type: "playback"` action whose `deeplinkId` is the id of the episode to play next ("CONTINUE", else episode 1). Its
    `resourceId` is base64 JSON (`mediaId`, `availId`, `contentType: "vod"`), not needed for the URL.
  - Episodes are in `containers[type == "episodes"].seasons[].items[]`. Each item has `id`, which is the play id:
    `https://www.disneyplus.com/en-gb/play/{episodeId}`. A season has `visuals.name` ("Season 1") and
    `visuals.episodeCountDisplayText` ("8 Episodes"). The same page also lists SUGGESTED and EXTRAS sets.
  - The web page's own links agree: its PLAY, CONTINUE and RESTART buttons and each episode tile link to `/en-gb/play/{id}`.
  - Not done: reading episode titles and numbers from `visuals` (the structure of an item was only checked for `id` and `actions`),
    and paging seasons (`pagination` is present on the other sets).
- **Not done:** paging of search, filters, integration with Baird's own search, playing a result from the grid, keeping the
  token between searches.
- **Not tested:** token expiry in the middle of a session. The in-page `fetch` is always made right after capturing a fresh token,
  so expiry is unlikely to matter for a single search.

## 4. For the implementer (suggested direction)

This is advice from the spikes, not a design. Check each point against the current code before relying on it.

**What to keep**
- `DisneyPageClassifier`: pure, tested, and it encodes the traps in §1 (needs the app shell, 404 page = signed out).
- `DisneySearchParser`: pure and tested against the real reply shape.
- `DisneySession` sign-in, profile and token-capture logic. Move it behind whatever service the app uses for streaming sites.
- `ICdpPageDriver`: the small set of generic page primitives. Prefer growing this over adding site-specific methods to `ICdpBrowserService`.

**What to throw away**
- The two spike tabs and view models, their autotest hooks, and the Netflix-flavoured HUD colours. They exist to prove things.
- The hard-coded port numbers (9222 to 9225) and the "never run two spikes together" rule. Replace them with one owner of the browser.

**Suggested shape**
- One browser and session service for all sites (Netflix, Disney+), owning the profile lock, the CDP port and the window. Today
  every spike starts and stops its own Chromium, which costs seconds on each search and makes the spikes exclusive.
- **Conflict to plan for:** another session (branch `integrate-netflix-search-playback`, not merged when this was written) adds
  `NetflixSession`, which keeps one Netflix Chromium open all the time on CDP port 9222 with the shared profile
  `~/.config/baird/browser-profile`. Once that is deployed, a Disney spike that starts its own Chromium on that profile will
  fail on the Chromium profile lock. Quick fix: give Disney its own profile directory (for example
  `~/.config/baird/browser-profile-disney`; it needs a new Disney sign-in and registers another device). Better: one browser
  owner for all streaming services, with one tab per service (CDP `Target.createTarget`). `NetflixSession` could become that owner.
- Keep the browser alive between searches, hidden. Re-capture the Disney+ token when the API answers 401/403 (the parser already
  reports `NeedsNewToken`; no code retries yet). Netflix's search self-heals the same way with a captured request.
- Send results into Baird's own search, with a "Disney+" source. The play URL is `/play/{entityId}` for a movie.
  For a series, read the entity page and play an episode id (§3, "Series and episodes").

**Risks**
- Everything here uses Disney's private web API (`explore/v1.21`), headers (`x-application-version`) and page structure
  (`data-testid` values). Any of them can change without notice. The tests pin the shapes seen on 2026-10-23.
- This signs in as a real account from a non-standard browser. Each new profile registers a device. Keep one profile.
- Widevine L3 limits the picture to 854 px wide. That is a platform limit, not a bug.
- Terms of service of both services were not reviewed.

## 5. Red herrings and decisions

| Item | Result |
|---|---|
| Playback spinner on the first test of a fresh profile | Widevine CDM not downloaded or not yet loaded (restart fixes it). Not a Disney block. |
| Playing the TRAILER id (`/play/{trailerId}`) | Stayed on the spinner in a fresh profile, so it proved nothing. The full title was used for the real test. |
| Disney+ asking for an emailed code | Offered by default, but "Enter a password instead" avoids it. No code was needed. |
| User agent | Pi Chromium reports `X11; Linux x86_64`, and Disney+ accepted it. |
| One shared browser profile for Netflix and Disney+ | Works: both sessions live in `~/.config/baird/browser-profile`. Netflix and Disney spikes must not run at the same time. |
| Device registrations | Each fresh browser profile that signs in registers a device on the Disney+ account. Two were made (a throwaway probe profile, since deleted, and Baird's own). Don't churn profiles. |
| Rewriting the Netflix code into a shared layer | Not done. Disney has its own classifier, session and view models. New generic browser primitives are in `ICdpPageDriver` (`CdpBrowserService`): evaluate, type, Enter, and request capture. |

## 6. Where things are

| What | Where |
|---|---|
| Playback spike | `Baird/ViewModels/DisneySpikeViewModel.cs`, `Baird/Controls/DisneySpikeControl.axaml` |
| Search spike | `Baird/ViewModels/DisneySearchSpikeViewModel.cs`, `Baird/Controls/DisneySearchSpikeControl.axaml`, `Baird/Services/DisneySearch.cs` |
| Sign-in, state reading, search call | `Baird/Services/DisneySession.cs` |
| Page classifier (pure) | `Baird/Services/DisneyPageClassifier.cs` |
| Credentials | `Baird/Services/DisneyCredentials.cs` |
| Generic page driver | `ICdpPageDriver` in `Baird/Services/CdpBrowserService.cs` |
| Tests | `Baird.Tests/Services/DisneyTests.cs` |
| Unattended test hook | `MainViewModel.StartAutoTestIfRequested`: `BAIRD_AUTOTEST=disney` or `disney-search` |
| Pi runner | `spikes/netflix-tooling/pi_autotest.sh disney\|disney-search test` |

## 7. How to re-run

```bash
spikes/netflix-tooling/deploy_test_build.sh                                              # ~5 min
ssh -o HostKeyAlias=baird baird.local 'bash ~/baird-test/pi_autotest.sh disney-search test'
ssh -o HostKeyAlias=baird baird.local 'bash ~/baird-test/pi_autotest.sh disney test 80'
scp -o HostKeyAlias=baird -r baird.local:baird-test/shots ./shots                         # then look at the PNGs
```

The runner stops `baird.service` for the test and restores it at the end.
