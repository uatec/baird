# Baird new UI — phase 1: foundations, Home, and the animation decision

**Date:** 2026-10-26 · **Status:** design for review · **Builds on:** the prototype (`spikes/ux-redesign-prototype/`), its
design (`2026-10-25-baird-ux-redesign-prototype-design.md`, revision 2) and the decision log
(`2026-10-26-baird-ux-redesign-decision-log.md`).

## Intent

Start building the redesigned UI in the real Avalonia app, with real data and the real players. This is phase 1 of six
(foundations + Home · shell and Home data · playback layers · guide · catalogue, show pages and requests · boot
experience). Phase 1 proves the approach on the Pi before the expensive data work, and settles Lottie vs Rive vs plain
Avalonia with measurements, not opinion.

**The owner's rule: the existing architecture must not distort the new implementation.** The new UI is designed from the
prototype. Existing services and players are reused only through small interfaces that the new UI defines. Where an
existing piece doesn't fit, it is wrapped or changed; the design is not bent to fit it.

## Goals

1. **A switch between the classic and new UI, from the UI itself.**
   - A config setting `Ui = classic | new` in `[Baird]` of `~/.baird/config.ini`, written by the existing safe
     single-key writer (`SettingsViewModel.SaveSetting`, reused or extracted).
   - Both UIs' Settings offer "Interface: Classic / New". Changing it saves the setting and restarts Baird into that mode:
     - under systemd (`INVOCATION_ID` set): `systemctl --user restart baird.service`;
     - otherwise (run by hand on the PC): start a fresh copy of itself with the same arguments, then exit.
   - The default stays **classic** until the owner says otherwise. An environment variable may still override it for
     tests, but the setting is the user-facing control.
2. **A new composition root and shell, separate from `MainView`/`MainViewModel`.**
   - When `Ui = new`, `App` builds `NewShellView` instead of `MainView`, with its own composition root that creates
     the services.
   - Nothing of the old UI is instantiated in new mode: no `MainView`, `MainViewModel`, `TabNavigationControl` or spike
     view models.
3. **A headless playback controller over `IPlaybackEngine`,** with one persistent video surface.
   - `PlaybackController` owns engine selection, load timeout/retry, position/duration/state, live "behind" time,
     next-episode, and saving progress through the new history interface.
   - The video surface is a thin `OpenGlControlBase` that only renders mpv.
   - It is created once and **never detached or re-created**: mpv is destroyed with its GL context. UI layers change
     around it.
   - It is transparent where it isn't drawing, so Netflix/Disney (a Chromium window behind Baird) show through; while a
     browser engine is playing, every ancestor of the video area is transparent too.
   - Old `VideoPlayer` may be reused only as a reference; the new controller is not a fork of it.
4. **The prototype's navigation model, ported with its tests.**
   - Places (Home · Live TV · Search · Settings as tabs), a page stack, one overlay, and the video as ground floor.
   - Back retraces the journey (overlay → video; pages → place; place → Home; bare video → the page it was started from).
   - `nav.js`, `focus.js` and `keys.js` behaviour, and their unit tests, are ported to C# (xUnit) first, unchanged in
     meaning.
5. **The design system in Avalonia.**
   - Tokens and the Instrument Sans font (bundled).
   - The focus treatment: lift ~1.12, a bright ring with soft glow, dimmed neighbours.
   - Motion that animates **in, out and between**: places crossfade, pages and overlays animate out, pictures crossfade
     only after decoding.
   - `KeepInView`: the lit element is always fully on screen.
   - The tab bar on every place.
   - Lite mode (Pi): no blur, no grain, no drift.
6. **The new G20S key map:** ≡ (`Key.Apps`/ContextMenu) = Guide, Pg± = channel ±, OK / hold OK, digits, DEL, ⏯ ⏮ ⏭,
   Back, Home, voice, and the volume/power keys still to CEC. Handled in one input layer in the new shell. This does
   not touch the old UI's key handling.
7. **Home with real data.**
   - Hero plus rows. **Up next** comes from real history, including next-episode where the provider gives seasons.
   - **On Now** comes from TvHeadend channels plus current programmes.
   - **Watchlist** and **Discover** (Seerr trending) appear as rows. Where real data is missing (e.g. no per-season
     availability yet) they show what exists, honestly; phase 5 fills the gaps.
   - OK on a live card plays it (with the tuning transition). OK on an episode or film plays it. Hold OK opens actions
     (a minimal set for phase 1).
8. **Measure the motion budget on the Pi, then decide the animation approach.**
   - **8a — Budget.** With live TV playing (the real DVB path), measure mpv dropped frames and UI frame time for:
     - the bare video;
     - one 420 ms overlay slide-in and slide-out;
     - a 300 ms focus lift on a card row over video;
     - a tab crossfade over video;
     - a 2 s looping element (spinner);
     - the info bar's 1 Hz update.
     The result is a written budget (what may animate over live video, for how long, how big).
   - **8b — Decision bench.** Three moments, each built twice (Rive via the existing `RivePlayerControl`, and plain
     Avalonia animation), shown over live TV and measured against the budget:
     - the request ring that fills and then becomes ▶;
     - the tuning states (searching → locked → picture);
     - one icon morph (＋ → ✓ for the watchlist).
     Lottie is measured for the tuning loop and an illustration (its natural use).
     Output: a short findings doc with numbers and a recommendation per moment kind (icon morphs, progress, loops,
     illustrations).

## Non-goals (phase 1)

- The guide, the full playback overlays (channel list, info bar scrubbing, end cards), search, show pages, requests,
  reminders: these are phases 3–5. Phase 1 has only what Home needs to play something and return.
- Changing the data model of existing services beyond adapters (structured seasons, guide images, per-season Seerr
  data are phase 5).
- The boot splash and Lottie intro (phase 6).
- Removing or refactoring the classic UI. It stays the default and keeps working.

## Anti-goals

- New UI code that references `MainView`, `MainViewModel`, `MediaItemViewModel`, `TabNavigationControl` or spike view
  models.
- Re-creating or detaching the video surface (it kills mpv).
- Continuous animation over live video beyond what the measured budget allows.
- An opaque full-screen root brush in new mode (it hides Netflix/Disney video).
- A switch that only works from an environment variable.

## Constraints and facts (from the code survey)

- **Video.** mpv renders inside Avalonia via `OpenGlControlBase` + `mpv_render_context` (`VideoPlayer.cs:1010–1081`,
  `MpvPlayer.cs:371–456`). mpv commands must not run synchronously on the UI thread. Dispose only on the render thread
  (`MpvPlayer.cs:12–21, 818–839`).
- **Netflix/Disney.** Chromium behind a transparent Baird window, driven over CDP (`BrowserHub`, port 9222). The engines
  implement `IBrowserPlaybackEngine`. Disney's `IPromptingEngine` prompt must be drawn by Baird. The engines currently
  take spike view-model factories in their constructors: the new composition root must construct them without spike
  UI, by adding a constructor or factory that doesn't need it.
- **Repaint cost.** Repainting over live video costs frames on the Pi. A full-screen 50 Hz repaint was ~900 drops/min,
  1 Hz ≈ 0 (`spikes/wayland-overlay/RESULTS.md`, separate-window variant; the in-process cost is unmeasured, hence 8a).
- **Data.** Service interfaces return `MediaItemViewModel` (a ReactiveObject); `EpgProgram` references it. The new UI
  defines its own plain records (Title/Episode/Channel/Programme/HistoryEntry) and adapters that map from
  `MediaItemData`, `EpgProgram` and history. Season ids are the `id|n` convention. History is per item. There are no
  guide images.
- **Startup.** Services are created synchronously in `MainView`'s constructor today. The new composition root creates
  them itself, async where they block (JSON loads, the full Jellyfin listing), so the first frame isn't held up.
- **Platform.** XWayland under labwc on the Pi (`DISPLAY=:0`). Skia on OpenGL. Transparency is the window hint
  (`BAIRD_TRANSPARENT_WINDOW`, default on). Self-contained, single-file, trimmed build with reflection-JSON off: new
  JSON must use the source-generated `BairdJsonContext`.
- **Shared machines.** The PC hosts other sessions' timing tests (isolated windows). The Pi is shared with other Baird
  sessions: message them before deploying or testing on it.

## Architecture (decided)

```
App ──(Ui setting)──▶ classic: MainView (unchanged)
                  └─▶ new:     NewShell composition root
                                ├─ Services (existing, constructed here) ──▶ Adapters ──▶ new plain records
                                ├─ PlaybackController (IPlaybackEngine; mpv + browser engines; history writes)
                                ├─ Navigator (places · pages · overlay · video)   [ported from nav.js]
                                ├─ InputMap (G20S → actions)                       [ported from keys.js]
                                └─ NewShellView
                                     layers: VideoSurface (persistent) · Ambient · Place · Page · TabBar ·
                                             BackChip · Overlay · Toast
```

- Screens and overlays implement one small contract: `OnAction(action) → handled`, `OnShow`, `OnFocusIn/Out`,
  `Destroy`, matching the prototype's.
- Focus is managed by the screens (a lit index), not by Avalonia keyboard focus. The new shell keeps Avalonia focus
  on itself, so the remote always reaches the input map.
- Motion is centralised: one `Motion` helper offers in/out/between transitions using compositor-friendly
  transform/opacity animations and the agreed durations (220/420/600 ms, gentle easing). Lite mode shortens or removes
  them.

## Testing

- Ported unit tests: navigation, focus, keys (xUnit). New unit tests for the adapters (mapping real-shaped data,
  including missing fields), the playback controller (with a fake engine, as `VideoPlayerEngineTests` does), and the
  config switch writer.
- Headless UI tests (`Avalonia.Headless`, real Skia) for: one lit element, lit element fully on screen, places
  switching without an empty frame, and Back rules through the real shell.
- On this PC: run in new mode against the real services (TvHeadend, Jellyfin, Seerr on the network) and screenshot.
- On the Pi (coordinated): 8a and 8b measurements with live TV; the owner tries it with the real remote.

## How we'll judge phase 1

- The owner can switch between Classic and New from Settings in either UI, and Baird restarts into it, on the PC
  and the Pi.
- The new mode boots into Home with real Up next and On now, plays a live channel and a Jellyfin episode, and returns.
- Focus, motion (in, out, between) and Back behave as in the prototype.
- A findings doc gives the measured motion budget and a recommendation for Lottie / Rive / Avalonia per moment kind,
  with numbers from the Pi.

## Left to the builder

File and namespace layout under `Baird/` (e.g. `Baird/NewUi/`). Exact adapter shapes. How the restart is invoked
(within the rule above). Which Rive/Lottie assets are used for the bench (author simple ones if needed; the existing
sample files are acceptable). Measurement tooling, reusing the A/V debug sampler where possible.

## Calls made after the builder review (Claude's; open to change)

- **Legacy types live only in adapters.** The adapter layer (`Baird/NewUi/Adapters/`) is the one place allowed to
  touch `DataService`, `MediaItemViewModel` and `EpgProgram`. Everything above it uses the new plain records. New and
  classic modes share the same history, watchlist and cache files, so Up next survives switching modes. Writing
  history from the new controller goes through the adapter (which builds the `MediaItemViewModel` the legacy
  `IHistoryService` wants).
- **Places in phase 1.** Home is real. Live TV and Search are honest placeholders ("The guide arrives in phase 4",
  with On now as a list that plays). New-mode Settings has: Interface (Classic / New), Interface size, and links to
  the classic debug pages via switching mode. ≡ goes to the Live TV placeholder; voice goes to Search.
- **Pg± change channel** while live TV plays in phase 1 (with the tuning transition). Other playback overlays wait
  for phase 3; OK on the bare video shows a minimal info bar (title, progress, live/behind).
- **Items with no target yet** (a series not in progress, a Discover title not in the library): OK shows a toast
  saying what will happen there in a later phase, never a dead end.
- **Motion now, decision after.** Home's motion is built in plain Avalonia now. 8a runs as soon as the shell plays
  live TV; 8b is a dedicated "Animation bench" screen inside the new shell (reachable from Settings); the winners are
  adopted from phase 3 on.
- **The pass mark for the budget.** Each scenario runs 60 s of live DVB (the deployed defaults: `profile=fast`,
  bwdif 25 fps), after discarding the first 20 s start-up burst. Within budget = 0 steady-state dropped frames for
  anything persistent (loops, 1 Hz updates), and ≤ 2 dropped frames per one-off transition, averaged over 10
  triggers. UI frame time is reported but isn't a pass mark.

## Owner's answer on scope (2026-10-26)

"I will move to live in new mode as soon as possible, but … focus on getting one thing working properly, rather than
everything not working yet."

- **Phase 1 stays narrow and solid:** the switch, the shell, Home with real data, live TV and Jellyfin playback with
  the tuning transition and Back, plus the motion budget and the animation decision. Everything in it works properly
  rather than partly.
- **Phase 2 is now "livable New mode"**, done straight after phase 1 so the owner can switch over. It pulls the
  non-UI jobs out of `MainView` into services both modes share: CEC lifecycle (input switch and resume on TV on,
  pause on input lost or standby), the power toggle, the screensaver, the cursor policy. It also untangles
  Netflix/Disney playback from the spike view models so they play in New mode, and adds the Disney prompt. The old
  "shell and Home data" phase is folded into phases 1 and 2.
