# Netflix (and friends) on the Pi: findings

Status as of 2026-10-23. Written so another agent can continue without repeating the dead ends.
How these were found, and how to test, is in [spike-strategy.md](spike-strategy.md).

## TL;DR

| Thing | State | Evidence |
|---|---|---|
| Netflix playback in Baird on the Pi | **Works** (release 0.0.220) | Unattended test on the installed build: playback started, window revealed over Baird. Screenshots exist (not in repo). |
| Netflix in Baird's normal player (transparent main window, Baird keeps all input) | **Works** (test build, 2026-10-24) | `BAIRD_AUTOTEST=netflix-core` on the Pi: Netflix video under Baird's normal HUD; uinput keys Right/Left/Space/Down/Esc/PgUp drove Netflix's player API, opened the menu over the video, and switched to live TV. Focus never left Baird. See §1 "Netflix in the main player". |
| Transparency-capable main window vs. live TV frames | **No cost** (2026-10-24) | Same channel, alternating main and this build, 90 s each: VO drops 3442/3344 vs 3422/3408, CPU 114–120% both. But main itself drops ~38 frames/s today (Netflix browser on or off), not the 0 measured on 2026-10-19: a separate regression to find. |
| Netflix sign-in with configured credentials | **Works** | Signed in on the Pi from a signed-out profile; Netflix reported `CURRENT_MEMBER`. |
| Netflix search via Netflix's internal API | **Works** (branch `spike/netflix-search`, not deployed) | 48 results for "One Piece" in ~360 ms. Self-heals a stale query id (tested by planting one). |
| Lottie animations | Work on the Pi | User-confirmed on the TV. |
| Rive animations (native, in-process) | Work on the Pi | User-confirmed on the TV after the asset fix. |
| `/tmp` filling up on the Pi | **Fixed** (0.0.220) | 60 × 85 MB leaked yt-dlp folders cleared; no new ones after the fix. |

## 1. Netflix playback

### Architecture (what the code does)

- `Baird/Services/CdpBrowserService.cs` launches the system Chromium (`/usr/bin/chromium`) with a persistent profile
  (`~/.config/baird/browser-profile`) and drives it over the Chrome DevTools Protocol (CDP), on port **9222**.
- `Baird/ViewModels/NetflixSpikeViewModel.cs` runs the flow:
  1. Launch on the target URL.
  2. Check auth (`NetflixPageClassifier`).
  3. Sign in with configured credentials if needed.
  4. Wait for `video.currentTime` to advance.
  5. Reveal the window, with the HUD overlay injected from `Baird/Resources/baird-overlay.js`.
- Netflix error pages (e.g. E100) are read from the page text and shown in Baird's UI with code and message. One recovery
  attempt clears the Netflix origin's site data (keeping cookies) and reloads.

### Root cause of E100 ("This title is not available to watch instantly")

**A Widevine downgrade.**
- **What happened:** Chromium's component updater installs a newer Widevine CDM into the profile itself
  (`<profile>/WidevineCdm/4.10.3112.0`). The OS package in `/opt/WidevineCdm` is the 2023 **4.10.2662.3**.
  Baird's `WidevineStager` re-pointed Chromium's hint file (`WidevineCdm/latest-component-updated-widevine-cdm`) at the
  old copy on **every launch**. Netflix refuses the old CDM, and the user sees E100.
- **How it was isolated:** a switch matrix in the real app. Only the run with staging disabled played.
- **Fix:** `WidevineStager.Stage` stages only when the profile has no Widevine, or only an older one. It also repairs a
  hint left pointing at an older copy by earlier Baird versions. Unit tests: `Baird.Tests/Services/WidevineStagerTests.cs`.

Why the staging exists at all: on a brand-new profile, Pi OS Chromium logs "Widevine enabled but no library found" and
denies `com.widevine.alpha` until its component updater has downloaded a CDM. Staging the system copy bootstraps that.

**Red herrings.** All were measured, and none was the cause. Don't redo them.

| Hypothesis | Result |
|---|---|
| The title isn't available to the account | False. It plays in the user's browser and on the Pi once the CDM is right. |
| Kids or restricted profile | False. The profile is an adult one, `isKids:false`, GB. |
| User-agent / "Linux ARM" detection | False. Pi Chromium already reports `X11; Linux x86_64`, and overrides changed nothing. |
| Widevine capability ("warm-up" probes) | Not the cause. EME grants SW_SECURE_CRYPTO and SW_SECURE_DECODE; HW_SECURE_* is denied (expected on L3). |
| Stale site data (service worker, IndexedDB) | Coincidental. Clearing sometimes "helped" only because the updater swapped CDMs mid-session. |
| Netflix MSL error `7/205064 "Invalid device state for renewal"` | Seen in one phase (after heavy profile copying), but E100 also happens without it. Don't copy a live profile and use both copies. |
| `logs.netflix.com` refused (the user's DNS ad-blocker) | Harmless. Playback works with it blocked. |
| Full `/tmp` | A real problem (see §4), but not the E100 cause. |
| Inherited env (`LD_LIBRARY_PATH`, .NET vars) | Not the cause. The browser env is stripped anyway. |
| Pi clock skew | No. NTP is synced. |

### Window handling on the Pi (labwc, Wayland session, XWayland)

- **Must use `--ozone-platform=x11`.** In the real service environment (`XDG_SESSION_TYPE=wayland`), Chromium otherwise
  runs as a native Wayland client, and `xdotool` can't see or move it. Tests run over ssh have `XDG_SESSION_TYPE=tty`
  and silently fall back to X11, which hides this. This is why early "it works in my test" results were misleading.
- **`--kiosk`** means fullscreen with no frame. A normal or `--app` window gets a labwc title bar. `--window-position` is
  ignored by labwc, so the old "off-screen" hiding never worked.
- **Hide behind Baird:** a labwc `windowRule` (`identifier="baird-netflix"`, `<action name="Lower"/>`) in
  `~/.config/labwc/rc.xml`. `configure_pi.sh` writes it; the updater does not re-run `configure_pi.sh`. After launch,
  Baird hands focus back to its own window (`xdotool windowactivate <baird>`). At reveal it activates Chromium's window.
  Don't add `ignoreFocusRequest`: it blocks the reveal.
- Chromium also creates a 10×10 helper X window with the same class, so always pick the **largest** window.
- Chromium keeps rendering while covered (`visibilityState=visible`, rAF running), so playback isn't throttled.

### Sign-in, profiles and popups

- Config keys in `~/.baird/config.ini` (flat keys at the top, mode 600):
  - `NETFLIX_USER` and `NETFLIX_PASS`: the account credentials.
  - `NETFLIX_TARGET_URL`: the default title.
  - `NETFLIX_PROFILE`: which profile to pick on "Who's watching?". If unset, the first one is used.
- **Signed-out detection** comes from page signals, not the URL or cookies:
  - Netflix sends logged-out `/watch/<id>` to a public `/gb/title/<id>?fromWatch=true` page.
  - It sets `NetflixId` cookies for **anonymous** visitors too.
  - The reliable signals are `netflix.reactContext.models.userInfo.data.membershipStatus` (`ANONYMOUS` vs
    `CURRENT_MEMBER`) and the `[data-uia="header-signin-link"]` element.
- **The login form:** fields `input[name=userLoginId]` and `input[name=password]`, submit
  `[data-uia="login-submit-button"]`. Values are typed with CDP `Input.insertText`. One attempt per run, and the password
  is never logged (tested).
- **Popups:**
  - Notifications: suppressed with `--deny-permission-prompts`.
  - Cookie banner: auto-clicked (`#onetrust-accept-btn-handler`).
  - Save-password bubble: the `--disable-save-password-bubble` switch **does not exist** in this Chromium. Instead,
    `ChromiumProfilePrefs` seeds `credentials_enable_service=false` and `profile.password_manager_enabled=false`.
- **Every fresh profile or sign-in registers a new Netflix device.** Don't churn profiles. During testing Netflix once
  signed the Pi out entirely.

### Trimming gotcha

The shipped build is trimmed with reflection-based System.Text.Json **disabled**. Any `JsonSerializer.Serialize(new {...})`
throws on the Pi. Use `JsonObject` or the source-generated `BairdJsonContext`. The test project now sets
`JsonSerializerIsReflectionEnabledByDefault=false`, so this fails in CI.

### Netflix in the main player (since 2026-10-24)

Netflix plays through Baird's normal `VideoPlayer`, like any other video. Baird never gives focus to Chromium and never hides
its main window. Design: `docs/superpowers/specs/2026-10-24-netflix-in-main-player-design.md`.

- **Engines:** `VideoPlayer` plays through an `IPlaybackEngine`: mpv (`MpvPlayer`) for most sources, and
  `NetflixPlaybackEngine` for Netflix watch URLs (`https://www.netflix.com/watch/...`). The HUD, keys, history and
  Continue Watching are the same for both. mpv-only extras (Tab stats, audio delay, A/V Debug, subtitles) do nothing over
  Netflix.
- **Transparency:** the main window is created with `TransparencyLevelHint = Transparent` and a black background. While
  Netflix plays, `VideoPlayer` clears its area to alpha 0 and raises `IsShowingBrowser`, and `MainView` makes the window
  background transparent, so the browser behind Baird shows through. It turns on only when the video plays (the loading
  overlay is opaque until then, so Chromium never flashes), and turns off on stop, failure, end, or another item.
  `BAIRD_TRANSPARENT_WINDOW=false` drops the hint, but under XWayland on labwc the window still reports
  `actual=Transparent`.
- **Commands and status:** `ControlNetflixPlayerAsync` (Netflix's player API: `seek`, `play`, `pause`) and
  `PollVideoPlaybackAsync` every 500 ms. Two failed polls in a row (no reply, error, or 5 s timeout) show "No contact with
  the Netflix browser."; three polls without a video end playback (next episode / history as for any video).
- **Loading and failures:** the normal loading overlay shows the step ("Starting Netflix…", "Signing in to Netflix…",
  "Loading the video…"), or the reason and "Press Back to return."
- **Switching:** another item stops Netflix (the browser goes back to its idle page) before mpv plays. Live TV is not
  paused and restored any more; it's replaced like for any item.
- **Search while Netflix plays:** Netflix gives no results (not shown as a failure). Searching in a second tab may come later.
- **Measured on the Pi (2026-10-24, `netflix-core`, keys `RIGHT LEFT SPACE SPACE DOWN ESC PAGEUP`):** play to video
  6.6 s from the idle browser; every command used `api`; screenshot pairs 0.5 s apart: moving while playing, 0% change
  while paused; Down opened the menu (opaque, as over mpv), Esc returned to the moving video; PgUp switched to live TV and
  the window went opaque again. The active window was Baird in every shot.
- **Test:** `BAIRD_TEST_DIR=~/baird-final KEYS="RIGHT LEFT SPACE SPACE DOWN ESC PAGEUP" bash pi_autotest.sh netflix-core test`.
  Live TV cost: `pi_transparency_ab.sh 90 compare` (main build in `~/baird-baseline`, this one in `~/baird-final`).

### Core journey (Netflix in Baird's normal search and playback)

- **One browser, always open:** `NetflixSession` owns the only Netflix Chromium (port 9222, the shared profile).
  `Start` opens it hidden behind Baird 10 s after Baird starts, signs in and picks the profile, then leaves it on
  `https://www.netflix.com/robots.txt`. That page is on Netflix's origin (the search API is a same-origin `fetch` with the
  cookies) and has no video: covered Chromium keeps rendering, so idling on `/browse` would decode its trailer all the
  time. A watchdog (every 60 s) opens it again if it stopped, unless something plays. During playback, search answers
  "Netflix is playing" and does not touch the browser.
- **Measured on the Pi (2026-10-23, two runs):** browser ready 6.5–14 s after it opens (in the background, 10 s after
  Baird starts); Baird kept focus while it opened. First search: Netflix results after 1.8 s (API call ~0.5 s), from the
  `robots.txt` idle page; before this change ~6 s (cold start and profile pick). Play to HUD: 6.6–6.9 s, reusing the open
  browser. After Back: 11 Chromium processes still open, 0–8 % CPU (mostly ~1 %) over 5 s windows on the idle page.
  Not measured: memory.
- **Show and hide:** the browser never closes. It stays behind Baird; for playback Baird's window goes transparent over it
  (see "Netflix in the main player").
- **Search:** `NetflixProvider` is a normal `IMediaProvider`, added only when NETFLIX_USER/NETFLIX_PASS are configured.
  Items: id `netflix:<titleId>`, source `Netflix`, type Video, stream URL `https://www.netflix.com/watch/<titleId>`. For
  a series, Netflix plays the episode it would play next for the profile, and resumes at its own saved place.
- **Play:** `MainViewModel.PlayItem` plays `Netflix` items like any other item; `VideoPlayer` hands the watch URL to
  `NetflixPlaybackEngine`, which runs the proven flow (`NetflixSpikeViewModel`, `KeepBrowserOpen = true`) on the shared
  browser: it navigates the open browser to the watch URL, and at the end (stop, another item, failure, end) navigates it
  back to the idle page instead of closing it. If the session is not ready, the loading overlay shows why.
  (Until 2026-10-24 this was a separate playback page and a separate HUD window.)
- **History:** saved by `VideoPlayer` as for any video (on pause, item switch, end). `DataService` now also
  puts the item's data in the persistent cache, so Continue Watching finds Netflix items after a restart (Netflix has no
  lookup by id).
- **"Who's watching?" click ignored:** on the Pi the first profile click came while the page was still loading
  (`readyState=interactive`) and Netflix ignored it; the old code then waited until its timeout. The session now clicks
  again after the settle time (max 3), and fails with the reason. It logs each change of session state.
- **Journal:** the shared browser's log is written once, as `[NetflixBrowser]`. The spike screens show it on screen only.

### Known gaps (playback)

- Netflix prompts ("Are you still watching?", "Skip intro", "Next episode") are hidden or not reachable: Chromium never
  has focus. Baird must find them over CDP and show its own buttons.
- Not tested with the real remote by a person yet (only uinput keys).
- OK does not pause (for any video: OK arrives as Enter, which `VideoPlayer` doesn't handle although the hint says
  "OK / Space"). Space and Play/Pause do.
- No Netflix search results while Netflix plays.
- A small Netflix control (a flag icon, top right) still shows over the video; `netflix-page-style.js` should hide it.
- A brief flash of Chromium can appear when the browser first opens (before it's lowered behind Baird).
- Not measured: CPU/smoothness over a full episode; whether Netflix's next-episode autoplay briefly removes the `<video>`
  (three empty polls, 1.5 s, end playback).

## 2. Netflix search (branch `spike/netflix-search`, not yet merged)

- **Tab:** "Netflix Search". Code is `Baird/ViewModels/NetflixSearchSpikeViewModel.cs` and
  `Baird/Services/NetflixSearch.cs`.
- **Endpoint:** `POST https://www.netflix.com/graphql`, operation **`SearchPageQueryResults`**, sent from inside the
  signed-in hidden browser with `fetch(..., {credentials:'include'})`. Only `content-type: application/json` is needed.
  The browser uses CDP port **9223**.
- **The request:** a **persisted query** `extensions.persistedQuery = {id, version}` plus `variables`. Required
  variables are `searchTerm`, `pageSize` (48), `endCursor` (paging) and a large required `options` object. The template
  is stored in `Baird/Resources/netflix-search-query.json`; it contains no account data.
- **The reply:** `data.page.sections.edges[].node` with `__typename == "PinotGallerySection"` ("Films & Series").
  - Per entity: `displayString` (title); `unifiedEntity.{__typename (Show/Movie), videoId,
    contentAdvisory.maturityLevel}`; `contextualArtwork.artwork.url`.
  - `entities.pageInfo.{endCursor, hasNextPage}` gives paging.
  - The `PinotListSection` "More to explore" holds suggestions, which are ignored.
- **Errors:** `errors[0].extensions.code`.
  - `PERSISTED_QUERY_NOT_FOUND` means Netflix rotated the query id.
  - `INVALID_VARIABLES` / `ValidationError` means the variables changed.
  - On either, the spike opens the real search page, captures the web app's own request via CDP `Network.requestWillBeSent`,
    caches it at `~/.config/baird/netflix-search-query.json` and retries once. This was tested on the Pi with a planted
    stale id.
- **Important:** the API also answers **signed-out** browsers, with the general country catalogue. Results are only
  "filtered by this account" after the session is confirmed as `CURRENT_MEMBER` on a chosen profile, which the spike
  checks first, from `/browse`.
- **Cost:** ~7 s per search end to end (browser start + session check), of which the API call is ~0.4 s. Keeping the
  browser alive between searches would remove most of that.
- **Not done:** paging beyond 48 results; integration with Baird's own search; playing a result.
- **Alternatives considered:** Netflix has had no public API since 2014. TMDB "watch providers" (also via the existing
  Jellyseerr) is stable but country-level only, with no Netflix ids. Third-party catalogues (Watchmode, uNoGS) are paid
  or rate-limited and unverified. A suggested future shape: TMDB/Jellyseerr for discovery, and this internal API only to
  resolve a Netflix id at play time.

## 3. Lottie and Rive spikes

- **Lottie** (`spikes/lottie-avalonia-spike/README.md`): the blank viewport was missing
  `<AvaloniaResource Include="Assets/**"/>`. It now renders. `loading-ring.json` has an opaque background layer.
- **Rive** (`spikes/rive-avalonia-spike/README.md`, whose "arm64 not tested" section is now out of date):
  - **Runtime:** in-process via `rive-sharp` with natively built `librive.so`. Builds for both architectures are in
    `Baird/runtimes/*/native/` and are produced in Debian 12 containers (`native/build-*.sh`, glibc ≥ 2.29), so they run
    on CI's Ubuntu, Manjaro and the Pi.
  - **Blank-screen fix:** on the Pi the `.riv` files must be embedded as Avalonia resources. The shipped exe runs from
    `~/.cache/baird` (the brew wrapper gunzips it there), so loose files next to it don't exist. This is why Rive was blank.
  - **Old runtime:** the rive-sharp runtime is frozen at Dec 2022, so new editor files may not load.

## 4. Pi platform findings (not Netflix-specific)

- **`/tmp` is a 4 GB RAM-backed tmpfs.**
  - **The leak:** Baird's managed yt-dlp (`~/.local/share/baird/bin/yt-dlp`) is a PyInstaller one-file binary that
    unpacks ~85 MB into `$TMPDIR/_MEI*` each run, and leaks it when mpv kills it. mpv fell back to yt-dlp on every failed
    TV-channel retry, so `/tmp` hit 100% and used half the RAM.
  - **The fix (0.0.220):** mpv's `ytdl_hook-exclude` skips `127.0.0.1`/`localhost`, and yt-dlp's native `TMPDIR` points
    at `~/.cache/baird/ytdlp-tmp`, which is cleaned at startup (`Baird/Mpv/YtDlpTempDir.cs`, set via libc `setenv`
    before any threads).
  - **Interaction with Chromium:** `/etc/chromium.d/dev-shm` makes Chromium use `/tmp` for shared memory when
    `/dev/shm` has under 3.8 GB free, so a full `/tmp` also hurts Chromium.
- **Updater:**
  - **Deferring:** the updater defers when the TV looks in use (ALSA playback running, or Baird CPU ≥ 30%). Force it with
    `systemd-run --user --unit=baird-force-update --setenv=FORCE=1 --collect
    /home/linuxbrew/.linuxbrew/opt/baird/libexec/update_baird.sh`.
  - **Config:** it does not re-run `configure_pi.sh`. Changes there (e.g. labwc rules) must also be applied by hand on
    the existing Pi.
- **CI:** the Release workflow's "Install Dependencies" (`apt-get`) step sometimes hangs. Cancelling and re-running fixes
  it, and the CI workflow's identical step usually passes.
- **Chromium** on the Pi: 155, Debian trixie build, wrapper `/usr/bin/chromium` (adds `/etc/chromium.d/*` flags, runs in
  its own systemd scope).

## 5. Where things are

| What | Where |
|---|---|
| Playback spike | `Baird/ViewModels/NetflixSpikeViewModel.cs`, `Baird/Controls/NetflixSpikeControl.*` |
| Core journey | `Baird/Services/NetflixSession.cs`, `NetflixProvider.cs`, `NetflixPlaybackEngine.cs` |
| Netflix in the main player | `Baird/Playback/IPlaybackEngine.cs`, `Baird/Controls/VideoPlayer.cs` (engine choice, `IsShowingBrowser`), `MainView` (window background), `Baird/App.axaml.cs` (transparency hint), `Baird/Resources/netflix-page-style.js` |
| Browser control (CDP) | `Baird/Services/CdpBrowserService.cs` |
| Widevine staging (never downgrades) | `Baird/Services/WidevineStager.cs` |
| Sign-in detection | `Baird/Services/NetflixPageClassifier.cs`, `NetflixCredentials.cs` |
| Profile prefs (no password manager) | `Baird/Services/ChromiumProfilePrefs.cs` |
| Search spike | `Baird/ViewModels/NetflixSearchSpikeViewModel.cs`, `Baird/Services/NetflixSearch.cs`, `Baird/Resources/netflix-search-query.json` |
| Unattended test hook | `MainViewModel.StartAutoTestIfRequested` (`BAIRD_AUTOTEST=netflix`, `netflix-search` or `netflix-core`) |
| Test tooling | `spikes/netflix-tooling/` (see [spike-strategy.md](spike-strategy.md)) |
| labwc rule | `configure_pi.sh` and, on the Pi, `~/.config/labwc/rc.xml` |
| yt-dlp temp fix | `Baird/Mpv/YtDlpTempDir.cs`, `Baird/Mpv/MpvPlayer.cs` |
