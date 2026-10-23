# Netflix (and friends) on the Pi: findings

Status as of 2026-10-23. Written so another agent can continue without repeating the dead ends.
How these were found, and how to test, is in [spike-strategy.md](spike-strategy.md).

## TL;DR

| Thing | State | Evidence |
|---|---|---|
| Netflix playback in Baird on the Pi | **Works** (release 0.0.220) | Unattended test on the installed build: playback started, window revealed over Baird. Screenshots exist (not in repo). |
| Baird HUD over Netflix (Baird keeps all input) | **Works** (test build, 2026-10-23) | Screenshots on the Pi: video shows through Baird's transparent window; real uinput keys OK/Right/Left/Back reached Baird and drove Netflix's player API. See §1 "Baird-drawn HUD". |
| Netflix in the core journey (Search → play → HUD → Back) | **Works** (test build, 2026-10-23) | `BAIRD_AUTOTEST=netflix-core` on the Pi: 48 Netflix results beside BBC iPlayer and YouTube, first result played, HUD keys worked, Back wrote history and returned to the results. Netflix resumed at the saved place on the next run. See §1 "Core journey". |
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

### Baird-drawn HUD (default since 2026-10-23; `BAIRD_NETFLIX_HUD=html` gives the old HTML overlay)

Baird never gives focus to Chromium. All remote keys go to Baird, and Baird sends commands to the page over CDP.

- **Windows:** when playback is confirmed, Baird hides its main window, so Chromium (kiosk, fullscreen) shows. Then it
  opens `NetflixHudWindow`: fullscreen, `Topmost`, transparent (`TransparencyLevelHint = Transparent`, same as the
  Wayland overlay spike). Under XWayland on labwc it reported `transparency=Transparent` and kept focus all the time.
- **Commands:** `ControlNetflixPlayerAsync` uses Netflix's player API
  (`netflix.appContext.state.playerApp.getAPI().videoPlayer`, session id starting `watch`): `seek(ms)`, `play()`,
  `pause()`. It returns `"api"`, `"video"` (fallback to the `<video>` element) or null, and the HUD shows an error for
  null. On the Pi, every command used `"api"`. A direct `video.currentTime` change can cause Netflix error M7375 (reported
  by other projects; not tried here).
- **Status:** the HUD polls `PollVideoPlaybackAsync` every 500 ms. The bar repaints only when a value changes, and hides
  after 5 s while playing (stays while paused).
- **Page:** `Resources/netflix-page-style.js` hides Netflix's own UI and the pointer. The age-rating overlay at the start
  is `.watch-video--advisories-container` (found by probing the live page over CDP).
- **Exit:** Back (`Escape`, `BrowserBack`, `Back`) stops Chromium and closes the HUD. Then the main window comes back.
  Under XWayland, `Hide()`/`Show()` loses fullscreen, and setting the same `WindowState` again does nothing; Baird sets
  `Normal` and then the old state.
- **Live TV** is paused while the HUD is open (`MainViewModel.IsPaused`), and restored after.
- **Test:** `KEYS="ENTER ENTER RIGHT LEFT ESC" bash ~/baird-test/pi_autotest.sh netflix test`. `pi_keys.py` presses keys
  through a uinput device, so they go through labwc like the real remote (xdotool skips the compositor). Motion checked
  by comparing screenshot pairs 0.5 s apart: moving while playing, 0% change while paused.

The page-signal channel (`Runtime.addBinding` `bairdSignal`, `Runtime.bindingCalled`) stays for the HTML overlay mode and
for future page events (progress, ended). Tested in headless Chrome (`OverlayBackKey_SignalsBairdToExit`).

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
- **Show and hide:** the browser never closes. It stays behind Baird; for playback Baird hides its main window (the
  browser shows) and puts its HUD on top, and after Back shows the main window again.
- **Search:** `NetflixProvider` is a normal `IMediaProvider`, added only when NETFLIX_USER/NETFLIX_PASS are configured.
  Items: id `netflix:<titleId>`, source `Netflix`, type Video, stream URL `https://www.netflix.com/watch/<titleId>`. For
  a series, Netflix plays the episode it would play next for the profile, and resumes at its own saved place.
- **Play:** `MainViewModel.PlayItem` sends `Netflix` items to `PlayNetflix`, not to mpv. It pushes
  `NetflixPlaybackViewModel` (title, a simple step text, the reason for a problem), pauses live TV, and runs the proven
  flow (`NetflixSpikeViewModel`, `KeepBrowserOpen = true`) on the shared browser: it navigates the open browser to the
  watch URL, and at the end (Back, error, or Back on the page before the video starts) navigates it back to the idle page
  instead of closing it. If the session is not ready, the page shows why and does not play.
- **History:** when the HUD closes, the last position and duration go to `UpsertHistoryAsync`. `DataService` now also
  puts the item's data in the persistent cache, so Continue Watching finds Netflix items after a restart (Netflix has no
  lookup by id).
- **"Who's watching?" click ignored:** on the Pi the first profile click came while the page was still loading
  (`readyState=interactive`) and Netflix ignored it; the old code then waited until its timeout. The session now clicks
  again after the settle time (max 3), and fails with the reason. It logs each change of session state.
- **Journal:** the shared browser's log is written once, as `[NetflixBrowser]`. The spike screens show it on screen only.

### Known gaps (playback)

- Netflix prompts ("Are you still watching?", "Skip intro", "Next episode") are hidden or not reachable: Chromium never
  has focus. Baird must find them over CDP and show its own buttons.
- Not tested with the real remote by a person yet (only uinput keys). The remote's Back key must arrive as `Escape` or
  `BrowserBack`, as it does in the rest of Baird.
- A brief flash of Chromium can appear before it's lowered behind Baird.
- Not measured: CPU/smoothness over a full episode with the HUD.

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
| Core journey | `Baird/Services/NetflixSession.cs`, `NetflixProvider.cs`, `Baird/ViewModels/NetflixPlaybackViewModel.cs`, `Baird/Controls/NetflixPlaybackControl.*`, `MainViewModel.PlayNetflix` |
| Baird HUD over Netflix | `Baird/ViewModels/NetflixHudViewModel.cs`, `Baird/Controls/NetflixHudWindow.cs`, `MainView.ShowNetflixHud`, `Baird/Resources/netflix-page-style.js` |
| Browser control (CDP) | `Baird/Services/CdpBrowserService.cs` |
| Widevine staging (never downgrades) | `Baird/Services/WidevineStager.cs` |
| Sign-in detection | `Baird/Services/NetflixPageClassifier.cs`, `NetflixCredentials.cs` |
| Profile prefs (no password manager) | `Baird/Services/ChromiumProfilePrefs.cs` |
| Search spike | `Baird/ViewModels/NetflixSearchSpikeViewModel.cs`, `Baird/Services/NetflixSearch.cs`, `Baird/Resources/netflix-search-query.json` |
| Unattended test hook | `MainViewModel.StartAutoTestIfRequested` (`BAIRD_AUTOTEST=netflix`, `netflix-search` or `netflix-core`) |
| Test tooling | `spikes/netflix-tooling/` (see [spike-strategy.md](spike-strategy.md)) |
| labwc rule | `configure_pi.sh` and, on the Pi, `~/.config/labwc/rc.xml` |
| yt-dlp temp fix | `Baird/Mpv/YtDlpTempDir.cs`, `Baird/Mpv/MpvPlayer.cs` |
