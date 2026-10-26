# Baird New UI — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A New mode in the real Avalonia app — switchable from Settings — that boots to a real Home, plays live TV and Jellyfin with the prototype's focus/motion/Back, plus a measured motion budget and a Lottie/Rive/Avalonia recommendation.

**Architecture:** `App` reads `Baird:Ui`; `new` builds a separate composition root (`Baird/NewUi/`) that creates the existing services, wraps them in adapters returning plain records, and drives one persistent `VideoSurface` through a headless `PlaybackController`. Navigation, focus and key mapping are ported from the prototype's JS (with their tests) into pure C#. Screens implement a small contract; the shell owns layers, motion and the tab bar.

**Tech Stack:** .NET 9, Avalonia 11.3, xUnit + Avalonia.Headless (real Skia), existing mpv/libmpv, `Avalonia.Labs.Lottie`, the in-repo Rive control.

**Spec:** `docs/superpowers/specs/2026-10-26-baird-new-ui-phase1-design.md` (and the prototype design + decision log it builds on)

## Global Constraints

- No code under `Baird/NewUi/` (except `Baird/NewUi/Adapters/`) may reference `MainView`, `MainViewModel`, `MediaItemViewModel`, `TabNavigationControl`, `EpgProgram`, `DataService` or any `*SpikeViewModel`.
- The classic UI stays the default (`Baird:Ui` missing or `classic`) and its behaviour must not change except for the new "Interface" setting row.
- One `VideoSurface` instance for the app's lifetime; never removed from the visual tree; mpv calls never synchronous on the UI thread; dispose only in `OnOpenGlDeinit`.
- New mode's window and every ancestor of the video area stay transparent (no opaque root brush).
- JSON only through the source-generated `BairdJsonContext` (trimmed build, reflection-JSON off).
- Motion durations: 220 ms (focus), 420 ms (place/page/overlay), 600 ms (ambient/crossfade); easing `0.25,0.1,0.25,1`. Lite mode: no blur, no grain, no Ken Burns; transitions ≤ 220 ms.
- Focus lift 1.12, ring 4 px white + soft glow, neighbours brightness 0.55. Lit element always fully inside 64–1030 px vertically (1920×1080 design space).
- Shared PC: no build/test/browser runs during other sessions' announced isolated windows. Shared Pi: message the other Baird sessions before deploying or running anything there.
- Tests: `dotnet test Baird.Tests` must stay green; new tests live under `Baird.Tests/NewUi/`.

## Review Focus

1. **Switching mode mid-playback / restarting under systemd** — the restart must not leave two Baird processes or a stuck service. Pinned by `AppRestarterTests` (systemd path issues one `systemctl --user restart`, no re-exec) in Task 1.
2. **Providers that are down or slow at boot** (Jellyfin unreachable, TvHeadend timeout) — Home must still render with what loaded, never block the first frame. Pinned by `HomeDataTests.Home_loads_with_a_failing_provider` in Task 3.
3. **A stream that never loads** — the controller must time out, retry, then surface a failure state rather than a black screen forever. Pinned by `PlaybackControllerTests.Load_timeout_retries_then_fails` in Task 4.
4. **Rapid Pg± presses** — only the last channel tunes; the tuning ident shows the latest channel. Pinned by `PlaybackControllerTests.Rapid_channel_changes_play_only_the_last` in Task 4.
5. **Netflix/Disney items in history** — New mode can't play them yet (phase 2); OK on them must toast, not crash or black-screen. Pinned by `HomeScreenTests.Browser_items_toast_until_phase_2` in Task 6.

---

### Task 1: The Interface setting, the restart, and the root switch

**Files:**
- Create: `Baird/Services/IniSettingWriter.cs`, `Baird/Services/UiMode.cs`, `Baird/Services/AppRestarter.cs`
- Modify: `Baird/ViewModels/SettingsViewModel.cs` (use `IniSettingWriter`; add `UiMode` property + `ToggleUiModeCommand`), `Baird/Controls/SettingsControl.axaml(.cs)` (an "Interface: Classic / New" row), `Baird/App.axaml.cs` (choose root by `UiMode`)
- Test: `Baird.Tests/NewUi/IniSettingWriterTests.cs`, `UiModeTests.cs`, `AppRestarterTests.cs`

**Interfaces:**
- Produces: `static class IniSettingWriter { static void Save(string path, string section, string key, string value); static string DefaultPath { get; } }` — behaviour identical to today's `SaveSetting` (update in place, insert into section, create section).
- Produces: `enum UiMode { Classic, New }`; `static class UiModes { static UiMode Read(IConfiguration c); }` — `BAIRD_UI` env/config key wins, else `Baird:Ui`, else Classic; case-insensitive; unknown → Classic.
- Produces: `interface IProcessRunner { void Start(string file, string args); }`, `class AppRestarter(IProcessRunner runner, Func<string?, string?> env, Action exit) { void Restart(); }` — `INVOCATION_ID` set → `systemctl --user restart baird.service`; else start `Environment.ProcessPath` with the original args, then `exit()`.

- [ ] Step 1: Write failing tests: writer updates an existing key, inserts into an existing `[Baird]` before the next section, creates the section, preserves other lines; `UiModes.Read` for missing/`new`/`NEW`/`garbage`/env override; restarter under systemd runs exactly one `systemctl --user restart baird.service` and does not call exit; without systemd starts the process path then calls exit.
- [ ] Step 2: Run `dotnet test Baird.Tests --filter "FullyQualifiedName~NewUi"` → FAIL (types missing).
- [ ] Step 3: Implement; move `SettingsViewModel.SaveSetting` onto `IniSettingWriter.Save`; add the Interface row to classic Settings (OK toggles, saves `Baird:Ui`, then `AppRestarter.Restart()`); in `App.OnFrameworkInitializationCompleted`, `UiMode.New` → `Content = new NewShellView(Configuration)` (stub view for now: black, "New UI" text) and keep the window transparent hint.
- [ ] Step 4: Run the filter → PASS; run full `dotnet test Baird.Tests` → all green.
- [ ] Step 5: On this PC: `Baird:Ui=new` in a temp `~/.baird/config.ini` copy is NOT allowed (don't touch the owner's config) → instead run with `BAIRD_UI=new dotnet run --project Baird` and screenshot the stub; flip via the classic Settings row in a run with a temporary HOME (`HOME=/tmp/baird-home`), confirm the file written and the re-exec.
- [ ] Step 6: Commit.

### Task 2: Port the prototype's navigation, focus and input logic (pure C#)

**Files:**
- Create: `Baird/NewUi/Core/Navigator.cs`, `Baird/NewUi/Core/FocusMath.cs`, `Baird/NewUi/Core/InputMap.cs`, `Baird/NewUi/Core/HoldDetector.cs`, `Baird/NewUi/Core/NumberEntry.cs`
- Test: `Baird.Tests/NewUi/NavigatorTests.cs`, `FocusMathTests.cs`, `InputMapTests.cs`

**Interfaces:**
- Produces: `enum Place { Home, LiveTv, Search, Settings }`; `record PageRef(string Name, object? Params)`; `record OverlayRef(string Name, object? Params)`; `record VideoRef(string Kind, object Ref)`.
- Produces: `class Navigator { NavState State; object Top; void SetPlace(Place); void OpenPage(PageRef); void OpenOverlay(OverlayRef); void CloseOverlay(); void Play(VideoRef, bool keepOverlay = false); void Stop(); void Back(); void Home(); void ToVideo(); event Action Changed; }` — semantics exactly `spikes/ux-redesign-prototype/js/nav.js`.
- Produces: `static FocusMath.MoveInRows(int[] counts, (int Row,int Col) pos, Dir dir, Dictionary<int,int> memory)`.
- Produces: `enum UiAction { Up, Down, Left, Right, Ok, HoldOk, Back, Home, Guide, PlayPause, Prev, Next, PageUp, PageDown, Del, Voice, Digit0…Digit9, Help, DebugKeys }`; `static InputMap.Map(Key key, PhysicalKey physical) → UiAction?` covering keyboard stand-ins and G20S codes (`Key.Apps` → Guide, `Key.MediaPlayPause`, `Key.MediaNextTrack`/`Previous`, `Key.BrowserBack`, `Key.BrowserHome`, `Key.PageUp/Down`, digits, `Key.Delete`, configured `VOICE_COMMAND_KEY`); `HoldDetector` (500 ms) as in `keys.js`; `NumberEntry` as in `keys.js`.

- [ ] Step 1: Port `tests/nav.test.mjs` (all 15 cases), `focus.test.mjs` (row cases), `keys.test.mjs` to xUnit with the same names/assertions; add G20S-code cases (`Key.Apps` → Guide, `Key.MediaPlayPause` → PlayPause, `Key.BrowserBack` → Back).
- [ ] Step 2: Run → FAIL.
- [ ] Step 3: Implement.
- [ ] Step 4: Run → PASS; full suite green.
- [ ] Step 5: Commit.

### Task 3: Plain records and adapters over the existing services

**Files:**
- Create: `Baird/NewUi/Model/Records.cs` (`Channel`, `Programme`, `TitleRef`, `EpisodeRef`, `HomeCard`, `HeroInfo`), `Baird/NewUi/Adapters/LegacyServices.cs` (constructs services like `MainView` does, minus UI), `Baird/NewUi/Adapters/HomeData.cs`, `Baird/NewUi/Adapters/HistoryWriter.cs`
- Test: `Baird.Tests/NewUi/HomeDataTests.cs`

**Interfaces:**
- Produces: `record Channel(string Id, string Number, string Name, string? LogoUrl, string StreamUrl)`; `record Programme(string Id, string ChannelId, string Title, string? Subtitle, string? Synopsis, DateTimeOffset Start, DateTimeOffset End)`; `record HomeCard(CardKind Kind, string Id, string Label, string? Sublabel, string? ImageUrl, double? Progress, string? Badge, string Source, object Payload)`; `enum CardKind { Episode, Film, Series, Channel }`.
- Produces: `class HomeData(IDataService data, IEpgService epg, IJellyseerrService seerr) { Task<IReadOnlyList<HomeCard>> UpNextAsync(); Task<IReadOnlyList<HomeCard>> OnNowAsync(DateTimeOffset now); Task<IReadOnlyList<HomeCard>> WatchlistAsync(); Task<IReadOnlyList<HomeCard>> DiscoverAsync(); }` — each catches provider failures and returns what it has.
- Produces: `class HistoryWriter(IDataService data) { Task SaveAsync(string itemId, TimeSpan position, TimeSpan duration); }` — the only place that builds a `MediaItemViewModel` for history.
- Produces: `class LegacyServices(IConfiguration config) { IDataService Data; IEpgService Epg; IJellyseerrService Seerr; TvHeadendService Tvh; ICecService Cec; }` (Netflix/Disney not constructed in phase 1).

- [ ] Step 1: Write failing tests using `Baird.Tests/Mocks` + a real `DataService`: Up next maps unfinished history to episode/film cards with progress; On now maps channels + current programme (and a channel with no programme → Sublabel null, no crash); `Home_loads_with_a_failing_provider` (a provider throwing on `GetListingAsync` still yields the others' cards); Discover maps Seerr trending; HistoryWriter round-trips through `JsonHistoryService` (temp file).
- [ ] Step 2: Run → FAIL. Step 3: Implement. Step 4: Run → PASS; full suite green. Step 5: Commit.

### Task 4: PlaybackController and VideoSurface

**Files:**
- Create: `Baird/NewUi/Playback/PlaybackController.cs`, `Baird/NewUi/Playback/VideoSurface.cs`
- Test: `Baird.Tests/NewUi/PlaybackControllerTests.cs` (fake `IPlaybackEngine`, fake clock/scheduler)

**Interfaces:**
- Consumes: `IPlaybackEngine` (`MpvPlayer`), `HistoryWriter`.
- Produces: `class PlaybackController { PlaybackState State; NowPlaying? Current; TimeSpan Position; TimeSpan Duration; TimeSpan BehindLive; bool IsLoading; string? Failure; void PlayLive(Channel); void PlayItem(string itemId, string url, TimeSpan resume, bool isLive=false); void TogglePause(); void SeekRelative(TimeSpan); void Stop(); event Action Changed; event Action Ended; }` — load timeout 15 s, 3 retries, then `Failure`; saves history on stop/switch/end (≥10 s rule as today); marshals engine events to the UI thread through an injected `Action<Action> post`.
- Produces: `class VideoSurface : OpenGlControlBase { MpvPlayer? Mpv; bool ShowingBrowser; }` — init/render/deinit exactly as `VideoPlayer.cs:1010–1081`, nothing else; reports `GL_RENDERER` once via an event (for lite-mode detection).

- [ ] Step 1: Failing tests: play → Loading → FileLoaded → Playing; `Load_timeout_retries_then_fails`; `Rapid_channel_changes_play_only_the_last` (three PlayLive within 300 ms → engine.Play called once with the last URL after a 250 ms debounce); pause/seek update Position/BehindLive; Stop saves history once; Ended raised on StreamEnded.
- [ ] Step 2: FAIL. Step 3: Implement. Step 4: PASS; full suite green. Step 5: Commit.

### Task 5: The shell and the design system

**Files:**
- Create: `Baird/NewUi/NewShellView.axaml(.cs)`, `Baird/NewUi/Shell/Layers.cs` (place/page/overlay hosts with in/out/between), `Baird/NewUi/Shell/Motion.cs`, `Baird/NewUi/Shell/KeepInView.cs`, `Baird/NewUi/Shell/TabBar.axaml(.cs)`, `Baird/NewUi/Shell/BackChip.axaml(.cs)`, `Baird/NewUi/Shell/Toast.axaml(.cs)`, `Baird/NewUi/Theme/Tokens.axaml` (colours, type scale, radii), `Baird/NewUi/Theme/Focus.axaml` (card/button focus styles), `Baird/Assets/Fonts/InstrumentSans.ttf` (bundled, OFL), `Baird/NewUi/Screens/IScreen.cs`
- Test: `Baird.Tests/NewUi/ShellTests.cs` (headless)

**Interfaces:**
- Produces: `interface IScreen { Control View { get; } bool OnAction(UiAction a, bool repeat); void OnShow(); void OnFocusIn(); void OnFocusOut(); void Destroy(); Control? LitElement { get; } }`.
- Produces: `Motion.Enter(Control, Direction)`, `Motion.Exit(Control, Direction, Action onDone)`, `Motion.Crossfade(Control from, Control to)` — `TransformOperationsTransition`/`DoubleTransition` on opacity and RenderTransform only; lite mode via `Motion.Lite`.
- Produces: `KeepInView.Ensure(Control scroller, Control lit, double lift)` — same rule as `keepInView` in the prototype's `ui.js`.
- Consumes: `Navigator`, `InputMap`, `PlaybackController`, `VideoSurface`.

- [ ] Step 1: Failing headless tests: shell builds with `VideoSurface` first child and it is never removed when places/pages/overlays change; switching places keeps the old place until the new one has entered (child count 2 mid-switch); Back from a page shows the place beneath (no frame without either); exactly one lit element after each action in a scripted sequence; the lit element's bounds lie within 64–1030 after `KeepInView`.
- [ ] Step 2: FAIL. Step 3: Implement (tab bar on all places; back chip; toast; key handling: one `KeyDown/KeyUp` handler on the shell → `InputMap` → `HoldDetector` → shell/screen; Avalonia focus kept on the shell). Step 4: PASS; full suite green. Step 5: Commit.

### Task 6: Home, the placeholders, and New-mode Settings

**Files:**
- Create: `Baird/NewUi/Screens/HomeScreen.axaml(.cs)` (hero + rows + cards; async images with crossfade; ambient from a pre-blurred small copy generated at load), `Baird/NewUi/Screens/PlaceholderScreen.cs` (Live TV: On now as a playable list; Search: "coming" note), `Baird/NewUi/Screens/SettingsScreen.axaml(.cs)` (Interface Classic/New with ◂▸ + restart; Interface size)
- Test: `Baird.Tests/NewUi/HomeScreenTests.cs` (headless, with `HomeData` over mocks)

- [ ] Step 1: Failing tests: Home renders four rows from `HomeData`; ▴ from row 0 returns false (shell lights the tab bar); OK on a channel card calls `PlaybackController.PlayLive`; OK on an episode calls `PlayItem` with the resume point; `Browser_items_toast_until_phase_2` (a Netflix/Disney card → toast, no PlayItem); posters lit are fully in view; Settings ◂▸ on Interface writes `Baird:Ui` (temp path) and calls the restarter.
- [ ] Step 2: FAIL. Step 3: Implement (match the prototype's look: tokens, card sizes, hero with logo or title, focus treatment). Step 4: PASS; full suite green. Step 5: Commit.

### Task 7: Playing from Home — tuning, Pg±, minimal info bar, Back

**Files:**
- Create: `Baird/NewUi/Overlays/TuneIdent.axaml(.cs)`, `Baird/NewUi/Overlays/InfoBar.axaml(.cs)`
- Modify: `NewShellView` (video key handling: OK → info bar; Pg± → next/prev channel via the On-now channel order; Back → origin), `HomeScreen` (hero "Now playing" over video)
- Test: `Baird.Tests/NewUi/PlayFlowTests.cs` (headless, fake engine)

- [ ] Step 1: Failing tests: OK on a channel → `Navigator.Top == video`, ident shown then hidden after the engine reports FileLoaded (or 1.5 s, whichever later); Pg+ plays the next channel and keeps the overlay state as the design says; Back from bare video → Home with the video still playing (hero shows Now playing); OK on video opens the info bar; Back closes it with an exit animation.
- [ ] Step 2: FAIL. Step 3: Implement. Step 4: PASS; full suite green.
- [ ] Step 5: **Real run on this PC** (outside isolated windows): `BAIRD_UI=new` against the real TvHeadend/Jellyfin; play a channel, Pg± twice, play a Jellyfin episode from Up next, Back; screenshots to `/tmp/newui-phase1/`. Look at them.
- [ ] Step 6: Commit.

### Task 8: Motion budget and the animation decision (Pi)

**Files:**
- Create: `Baird/NewUi/Screens/AnimationBenchScreen.axaml(.cs)` (reachable from New-mode Settings: runs scenarios 8a and the 8b pairs over whatever is playing, each triggered N times, logging `[Bench]` lines), `Baird/NewUi/Bench/Scenarios.cs`, `Baird/NewUi/Bench/RiveRing.riv` etc. (simple authored or existing sample assets), `docs/new-ui-motion-budget-findings.md`
- Modify: `spikes/netflix-tooling/pi_autotest.sh` usage only via a new `BAIRD_AUTOTEST=bench` hook in the new shell (no changes to classic)

**Interfaces:**
- Consumes: mpv drop counters (`MpvPlayer.SampleAvStatsAsync`), `PlaybackController`.
- Produces: `[Bench] scenario=<name> n=<N> drops_total=<d> drops_per_trigger=<x> steady_drops_per_min=<y> ui_frame_ms_p95=<z>` journal lines.

- [ ] Step 1: Implement the bench screen and scenarios (8a: bare video, overlay slide in/out, focus lift on a row, tab crossfade, 2 s loop, 1 Hz info bar; 8b: request ring→▶, tuning states, ＋→✓ — each in Rive and in Avalonia; Lottie for the tuning loop and an illustration). Unit-test the drop-rate arithmetic (`BenchMathTests`).
- [ ] Step 2: Run the bench on this PC (sanity; numbers not representative).
- [ ] Step 3: **Coordinate** with the other Baird sessions; deploy a test build to the Pi (`deploy_test_build.sh`), run `BAIRD_AUTOTEST=bench` with live DVB per the pass mark (60 s each, first 20 s discarded, 10 triggers), collect the journal.
- [ ] Step 4: Write `docs/new-ui-motion-budget-findings.md`: the budget table and the per-moment-kind recommendation, with the numbers. Commit.

### Task 9: Owner hand-over

- [ ] Step 1: Full suite green; classic mode smoke (`smoke_test.sh`) still passes.
- [ ] Step 2: README section in `docs/` on switching modes and what phase 1 covers.
- [ ] Step 3: With the owner's go-ahead and the other sessions informed: deploy to the Pi as a normal release (classic stays default); the owner switches to New from Settings.
