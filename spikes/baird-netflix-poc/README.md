# Baird Netflix spike: measured findings

Netflix is driven through a Chrome/Chromium process controlled over the DevTools Protocol (CDP) by
`Baird/Services/CdpBrowserService.cs`. The flow is orchestrated by `NetflixSpikeViewModel`.
This sandbox had no Netflix credentials, so nothing below was run against netflix.com.

## What the app does now

| Step | Behaviour |
|---|---|
| Sign in (visible browser) | Launches Chrome on screen with the persistent profile `~/.config/baird/browser-profile` at `https://www.netflix.com/login`. The user signs in by hand and presses "I've signed in - verify". The app re-classifies the page; if it is a signed-in page it closes Chrome through CDP `Browser.close` (so the profile is flushed) and shows `SignedIn`. |
| Launch hidden | Same profile, window at (-4000,-4000). |
| Login page detected | State `NotLoggedIn`, red on-screen banner, browser stopped, buffering never entered. |
| Launch/CDP failure | State `Failed` with the reason (`LastError`). |
| No playback seen | State `Failed` after the buffering timeout. "Playing" requires `video.currentTime` > 0.1 and strictly increasing between two polls. |
| DRM check | Separate line on screen: result of `navigator.requestMediaKeySystemAccess('com.widevine.alpha', ...)` in the launched browser. It reports key-system access only, not playback. |
| MFA | The "Simulate MFA" checkbox is a UI test only and is labelled so. The real PIN submission guesses at form field selectors and is unverified. |

Page classification is a pure function, `NetflixPageClassifier.Classify(url, hasLoginInput, hasMfaInput, hasVideo, currentTime)`,
covered by unit tests (login URL, `authURL`, password input, browse page, mfa URL, pin input, video playing, below-threshold, marketing page, blank).

## Measured

- `dotnet test --filter Netflix`: 29 passed, 0 failed (classifier theory cases, VM state-machine tests with a mock browser service).
- Local CDP run with `/usr/bin/google-chrome-stable` 152.0.7977.82 (headless, x86_64 Linux desktop, temp profile) against local test pages on `localhost`, via `Baird.Tests/Services/CdpBrowserLocalIntegrationTests.cs` (opt-in, needs `BAIRD_CDP_IT_URL_BASE`):
  - A local page with a password input was classified `LoginRequired` through the real CDP path.
  - `requestMediaKeySystemAccess('com.widevine.alpha')` resolved in that Chrome build.
  - A local `<video>` fed from `canvas.captureStream()` was polled twice: `currentTime` 1.48 then 2.52, classified `VideoPlaying`.
- Bug found by that run and fixed: `video.duration` is `Infinity`/`NaN` for streams, which serialises to JSON `null`; `GetDouble()` threw and aborted polling.
- The old `baird-overlay.js` hardcoded a "WIDEVINE DRM" badge; it now shows the measured result.

## Not verified

- Any interaction with netflix.com: login page URL/field names, the real redirect for an unauthenticated `/watch/...` request, the `/browse` and `/watch` signed-in heuristics, and the MFA URL/field heuristics.
- That a manual sign-in persists across restarts (cookie lifetime, "remember me" behaviour, Netflix device checks). The app only claims `SignedIn` after seeing a signed-in-looking page; the proof is a later hidden launch not hitting the login page.
- That Widevine works on the Raspberry Pi 5 (arm64 Chromium). Only a desktop x86_64 Chrome was tested. Key-system access resolving is not proof of decrypted playback, and Netflix may require L1 or specific robustness levels for higher resolution.
- Netflix video playback of any kind, including `currentTime` advancing on netflix.com, and the Baird HUD/overlay on a live Netflix player.
- Hidden-window behaviour on the target compositor (labwc/Wayland): `--window-position=-4000,-4000` is an X11-style hint, and `wmctrl`/`xdotool` reveal calls do nothing on Wayland. `Browser.setWindowBounds` was not tested.
- Headed (non-headless) Chrome launch; the integration run was headless.
- Whether Netflix tolerates or blocks automation via `--remote-debugging-port`.
- Hardware decode / CPU cost on the Pi (H.264/HEVC/VP9 via Netflix is untested).
