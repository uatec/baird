# Netflix in Baird's Main Player Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Netflix plays through Baird's normal `VideoPlayer`. While it plays, the main window is transparent where the
video is, so the Chromium window behind it shows through. Baird's normal HUD, keys and menu work over it, and there's no
second window.

**Architecture:** `VideoPlayer` drives an `IPlaybackEngine` instead of `MpvPlayer` directly. `MpvPlayer` implements the
interface unchanged. A new `NetflixPlaybackEngine` runs the existing Netflix flow on the shared browser and polls and
commands it over CDP. While the Netflix engine is active, `VideoPlayer` clears its GL area to alpha 0 and raises
`IsShowingBrowser`. `MainView` then makes the window background transparent; the main window is created
transparency-capable from the start.

**Tech Stack:** C# / .NET 10, Avalonia (OpenGlControlBase, XWayland on labwc), ReactiveUI, xUnit, CDP (Chromium).

**Spec:** `docs/superpowers/specs/2026-10-24-netflix-in-main-player-design.md`

## Global Constraints

- Never `Hide()` the main window or give Chromium focus during the Netflix journey. No second window.
- Keys over Netflix do exactly what they do for other videos, or nothing (`VideoPlayer.OnKeyDown` is the only key handler).
- Netflix commands go through `ControlNetflixPlayerAsync` (player API); never set `video.currentTime` directly.
- Status comes from `PollVideoPlaybackAsync`, every 500 ms.
- Live TV dropped frames must not get worse. Measure with the A/V Debug page **and** a screenshot motion check, not counters alone.
- Trimmed build: no reflection bindings from code; XAML uses compiled bindings (`x:CompileBindings="True"`).
- Before **any** Pi deploy or test: ListAgents → SendMessage to the other Baird agents saying what the test touches
  (baird.service, ~/baird-test, port 9222, browser profile) and how long it takes; wait about 5 minutes for replies.
- Pi access: `ssh -o HostKeyAlias=baird baird.local`; logs: `journalctl -t baird`.
- Logging style: `Console.WriteLine("[Component] Sentence.")`, like the existing code.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Switching from Netflix to a live channel from the menu:** Netflix must stop (browser back on its idle page,
   `IsPlaybackActive` false) and the window must be opaque again before mpv's first frame. → Task 3 test
   `PlayAfterNetflix_StopsNetflixAndUsesMpv` + Task 6 Pi check.
2. **Netflix fails to start (not signed in, MFA, no video within the flow's timeout):** the loading overlay shows the
   reason and "Press Back to return", the window stays opaque (no stray Chromium visible), and Back leaves.
   → Task 2 test `FlowFails_RaisesFailedWithReason_AndFreesTheBrowser`.
3. **The browser dies mid-playback (poll returns null):** Failed is raised once, not on every poll. → Task 2 test
   `PollNull_RaisesFailedOnce`.
4. **mpv's 15 s load timeout and retries must not fire for Netflix** (the flow takes 6–14 s and has its own timeouts).
   → Task 3 test `NetflixPlay_DoesNotUseMpvLoadTimeout`.
5. **Search while Netflix plays:** no "failed" marker on the Netflix provider. → Task 4 test
   `Search_WhilePlaying_ReturnsNoResultsWithoutThrowing`.

---

### Task 1: Transparency-capable main window (Pi gate)

The spec's main risk. **Nothing past this task starts until the Pi numbers are back and the user has seen them if they
are bad.**

**Files:**
- Modify: `Baird/App.axaml.cs` (desktop `MainWindow` creation)
- Modify: `Baird/Controls/VideoPlayer.cs` (temporary F9 toggle, removed in Task 3)

**Interfaces:**
- Produces: the main window is created with `TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }`
  and `Background = Brushes.Black`, unless config `BAIRD_TRANSPARENT_WINDOW=false` (A/B and escape hatch; kept).
  Startup log: `[App] Main window transparency: requested=<bool> actual=<ActualTransparencyLevel>` (log in `Opened`).

- [ ] **Step 1:** In `App.OnFrameworkInitializationCompleted`, set the hint and black background on the new `Window`, gated on `Configuration["BAIRD_TRANSPARENT_WINDOW"]` (default true), and log the line above on `Opened`.
- [ ] **Step 2:** Temporary spike key in `VideoPlayer.OnKeyDown`: `F9` flips a private `_spikeTransparent` flag. When it's true, `OnOpenGlRender` does `gl.ClearColor(0,0,0,0); gl.Clear(GL_COLOR_BUFFER_BIT)` and returns, and the top-level window's `Background` is set to `Brushes.Transparent` (black again when it's false). Mark it `// SPIKE: removed in Task 3`.
- [ ] **Step 3:** Build: `dotnet build Baird.sln` → succeeds with 0 errors.
- [ ] **Step 4:** Coordinate (see Global Constraints), then deploy: `bash spikes/netflix-tooling/deploy_test_build.sh`.
- [ ] **Step 5: Pi check A, transparency.** Start Chromium behind Baird with any page (`pi_harness.sh <profile> https://example.com`), run the test build, press F9 with `sudo python3 ~/baird-test/pi_keys.py F9`, and screenshot. Expected: the Chromium page shows through the video area while Baird's version text still draws, focus stays on Baird (journal shows no focus change), and F9 again is black.
- [ ] **Step 6: Pi check B, mpv cost.** Same live channel, 2 minutes each, `BAIRD_TRANSPARENT_WINDOW=true` vs `false` (use `spikes/wayland-overlay/baird_ab.sh` or the A/V Debug page plus a screenshot-pair motion check). Expected: dropped frames are the same (0 today) and CPU within a few %.
- [ ] **Step 7: Gate.** Write the numbers in `docs/netflix-spike-findings.md` §1 (new subsection "Transparent main window"). If check A failed or check B shows more drops, **stop and report to the user** with the numbers and the two fallbacks from the spec. Don't continue.
- [ ] **Step 8:** Commit: `Main window: transparency-capable (BAIRD_TRANSPARENT_WINDOW), Pi measurements`.

### Task 2: `IPlaybackEngine` and `NetflixPlaybackEngine`

**Files:**
- Create: `Baird/Playback/IPlaybackEngine.cs`
- Modify: `Baird/Mpv/MpvPlayer.cs` (add `: IPlaybackEngine`; `PlaybackState` stays where it is)
- Create: `Baird/Services/NetflixPlaybackEngine.cs`
- Modify: `Baird/ViewModels/NetflixSpikeViewModel.cs` (replace `HudRequested(NetflixHudViewModel)` with `event Action? PlaybackConfirmed`; don't create a `NetflixHudViewModel` there)
- Test: `Baird.Tests/Services/NetflixPlaybackEngineTests.cs` (reuse `MockCdpBrowserService` from `Baird.Tests/ViewModels/NetflixSpikeViewModelTests.cs`, set up like `NetflixPlaybackViewModelTests.Page()`)

**Interfaces:**
- Produces, in `Baird.Playback`:
  ```csharp
  public interface IPlaybackEngine
  {
      PlaybackState State { get; }
      double TimePositionSeconds { get; }
      double DurationSeconds { get; }
      event EventHandler? FileLoaded;            // video is playing
      event EventHandler? StreamEnded;
      event EventHandler<bool>? PauseStateChanged;
      event EventHandler<double>? DurationChanged;
      void Play(string url, double? startSeconds = null);
      void Pause();
      void Resume();
      void Seek(double seconds);
      void SeekRelative(double seconds);
      void SetSubtitle(bool enabled);
      void Stop();
  }
  ```
  Events can fire on any thread; `VideoPlayer` marshals them.
- Produces, `Baird.Services.NetflixPlaybackEngine : IPlaybackEngine`:
  - `NetflixPlaybackEngine(NetflixSession session, Func<NetflixSpikeViewModel> createFlow)`
  - `static bool Handles(string? url)`: true when the URL starts with `https://www.netflix.com/watch/`
  - `event EventHandler<string>? Failed`: the reason, for the loading overlay; raised once per play
  - `string StatusText { get; }` + `event EventHandler? StatusChanged`: the step text, using exactly the strings in
    today's `NetflixPlaybackViewModel.StatusText` ("Starting Netflix…", "Signing in to Netflix…", "Loading the video…")
  - `TimeSpan PollInterval { get; set; } = 500 ms`; `Task PollOnceAsync()` (public, for tests)
  - Ignores `startSeconds` (Netflix resumes at its own saved place); `SetSubtitle` is a no-op.

Behaviour (port from `NetflixPlaybackViewModel.StartAsync` and `NetflixHudViewModel.PollAsync/SeekByAsync/TogglePauseAsync`):
`Play` → `State = Loading`, `session.IsPlaybackActive = true`, `EnsureReadyAsync` (2 min) → flow with `UseBairdHud = true`,
`KeepBrowserOpen = true`, `TargetUrl = url` → `LaunchPlaybackFlowAsync`. `PlaybackConfirmed` → `Playing`, `FileLoaded`,
start polling. A poll updates position, duration (raise `DurationChanged` on change), paused (raise `PauseStateChanged`
on change). After `Playing`, 3 polls in a row with `Found == false` → stop the flow, `Idle`, `StreamEnded`. A poll
returning null → `Failed("No contact with the browser.")` once, then `Idle`. `Stop` → cancel, `flow.StopPlaybackAsync()`
(browser back to `NetflixSession.IdleUrl`), `IsPlaybackActive = false`, `Idle`. `Play` while already playing stops
first.

- [ ] **Step 1: Write the failing tests** in `NetflixPlaybackEngineTests`:
  - `Handles_OnlyNetflixWatchUrls`: true for `NetflixProvider.WatchUrl("80107103")`; false for `null`, `""`, `https://www.netflix.com/browse`, `http://tvh/stream/1`.
  - `Play_ReachesPlaying_RaisesFileLoaded_AndMarksSessionBusy`: after `Play(watchUrl)` and waiting for `FileLoaded`, `State == Playing`, `session.IsPlaybackActive`, `svc.RevealWindowCalled == false`.
  - `Poll_ReportsPositionDurationAndPause`: with the video at `CurrentTime 42, Duration 1500, Paused true`, after `PollOnceAsync`: `TimePositionSeconds == 42`, `DurationSeconds == 1500`, `DurationChanged` raised with 1500, `PauseStateChanged` raised with true, `State == Paused`.
  - `SeekRelative_And_Pause_UseThePlayerApi`: `svc.PlayerActions` contains `(SeekBy, 10)` and then `(Pause, 0)`.
  - `Stop_ReturnsBrowserToIdle_AndFreesSession`: `svc.LastNavigatedUrl == NetflixSession.IdleUrl`, `!session.IsPlaybackActive`, `State == Idle`.
  - `VideoGone_ThreePolls_RaisesStreamEndedOnce`.
  - `FlowFails_RaisesFailedWithReason_AndFreesTheBrowser`: with `VideoSequence = () => null` and a 300 ms buffering timeout, `Failed` is raised once with a non-empty reason, `!session.IsPlaybackActive`, `State == Idle`.
  - `PollNull_RaisesFailedOnce`: after `Playing`, two `PollOnceAsync` calls returning null → `Failed` count == 1.
- [ ] **Step 2:** Run `dotnet test Baird.Tests --filter NetflixPlaybackEngineTests` → FAIL (types missing).
- [ ] **Step 3:** Add `IPlaybackEngine`, make `MpvPlayer` implement it (signatures already match; add `SeekRelative`/`Seek` if needed), implement `NetflixPlaybackEngine`, and switch `NetflixSpikeViewModel` to `PlaybackConfirmed`. Fix the compile errors in `MainViewModel`/`NetflixPlaybackViewModel` minimally (Task 4 deletes them).
- [ ] **Step 4:** Run `dotnet test Baird.Tests --filter "NetflixPlaybackEngineTests|NetflixSpikeViewModelTests"` → PASS.
- [ ] **Step 5:** Commit: `Playback engines: IPlaybackEngine, MpvPlayer implements it, NetflixPlaybackEngine`.

### Task 3: `VideoPlayer` drives engines; transparency while Netflix plays

**Files:**
- Modify: `Baird/Controls/VideoPlayer.cs`
- Modify: `Baird/Controls/VideoLayerControl.axaml` (loading overlay text)
- Modify: `Baird/MainView.axaml.cs` (window background follows `IsShowingBrowser`; hand the engine to the player)
- Test: `Baird.Tests/Controls/VideoPlayerEngineTests.cs` (headless, like the other `Baird.Tests/Controls` tests via `TestAppBuilder`)

**Interfaces:**
- Consumes: `IPlaybackEngine`, `NetflixPlaybackEngine` (Task 2).
- Produces, on `VideoPlayer`:
  - `NetflixPlaybackEngine? NetflixEngine { get; set; }` (set by `MainView`, like `DataService`)
  - `IPlaybackEngine? MpvEngineOverride { get; set; }`: test seam only; when set, it's used instead of creating `MpvPlayer`
  - `bool IsShowingBrowser`: DirectProperty, true from Netflix `Play` until the Netflix engine is `Idle`
  - `string LoadingText`: DirectProperty, default `"Loading..."`; the Netflix `StatusText` while it loads; on `Failed`, `"<reason>\nPress Back to return."`, with `IsLoading` kept true
- `VideoLayerControl.axaml` loading overlay binds its text to `#Player.LoadingText`.
- `MainView`: when `player.IsShowingBrowser` changes, set the top-level `Window.Background` to `Brushes.Transparent` / `Brushes.Black`, and log `[MainView] Showing the browser through the main window: <bool>`.

Rules: `Play(url)` picks `NetflixEngine` when `NetflixPlaybackEngine.Handles(url) && NetflixEngine != null`, otherwise mpv.
If the engine changes, `Stop()` the old one first. The load timeout/retry (`_loadTimeoutTimer`, `HandleLoadFailure`) and
`ConfigureForSource` apply to mpv only. mpv-only calls (render, stats, A/V sampling, quality settings, `Command`) keep
using the `MpvPlayer` field. `OnOpenGlRender`: when `IsShowingBrowser`, clear to (0,0,0,0) and return; call
`RequestNextFrameRendering()` when it changes. The `IsLoading` overlay stays opaque black while Netflix loads (it hides the
browser navigating) and goes away on `FileLoaded`. `UpdateHud` reads position, duration and state from the active engine.
Remove the Task 1 F9 spike code.

- [ ] **Step 1: Write the failing tests** (fake `IPlaybackEngine` for mpv via `MpvEngineOverride`; `NetflixPlaybackEngine` over `MockCdpBrowserService`):
  - `NetflixUrl_UsesNetflixEngine_AndShowsBrowserOnceLoaded`: `Source = WatchUrl(...)` → the fake mpv got no `Play`; after `FileLoaded`, `IsShowingBrowser` is true and `IsLoading` is false.
  - `NetflixLoading_ShowsStepText`: before playback, `LoadingText` is one of the Task 2 step strings.
  - `PlayAfterNetflix_StopsNetflixAndUsesMpv`: then `Source = "http://tvh/stream/1"` → `svc.LastNavigatedUrl == NetflixSession.IdleUrl`, `IsShowingBrowser == false`, and the fake mpv got `Play("http://tvh/stream/1")`.
  - `NetflixPlay_DoesNotUseMpvLoadTimeout`: with a Netflix play still loading after the mpv timeout would have fired (inject a short timeout via an internal `LoadTimeout` property), the fake mpv got no `Play`.
  - `KeysOverNetflix_GoToNetflixEngine`: raise `KeyDown` Right then Space on the player → `svc.PlayerActions` has `(SeekBy, 10)` then `(Pause, 0)`; Down raises `HistoryRequested`.
- [ ] **Step 2:** Run `dotnet test Baird.Tests --filter VideoPlayerEngineTests` → FAIL.
- [ ] **Step 3:** Implement the rules above in `VideoPlayer`, the overlay binding, and the `MainView` wiring.
- [ ] **Step 4:** Run `dotnet test Baird.Tests` → all PASS (existing `PlaybackControllerTests`/`VideoIntegrationTests` included).
- [ ] **Step 5:** Commit: `VideoPlayer: play Netflix through the Netflix engine, transparent while it plays`.

### Task 4: Netflix goes through the normal play path; remove the separate HUD

**Files:**
- Modify: `Baird/ViewModels/MainViewModel.cs` (create the engine; drop `PlayNetflix`, `NetflixHudRequested`, the live-TV pause/restore around Netflix, the `NetflixSpike.HudRequested` handler; rewrite `RunNetflixCoreAutoTestAsync`)
- Modify: `Baird/MainView.axaml.cs` (drop `ShowNetflixHud`; `player.NetflixEngine = _viewModel.NetflixEngine`)
- Modify: `Baird/MainView.axaml` (drop the `NetflixPlaybackViewModel` DataTemplate)
- Modify: `Baird/Services/NetflixProvider.cs` (busy instead of failed)
- Delete: `Baird/Controls/NetflixHudWindow.cs`, `Baird/ViewModels/NetflixHudViewModel.cs`, `Baird/ViewModels/NetflixPlaybackViewModel.cs`, `Baird/Controls/NetflixPlaybackControl.axaml(.cs)`, `Baird.Tests/ViewModels/NetflixHudViewModelTests.cs`, `Baird.Tests/ViewModels/NetflixPlaybackViewModelTests.cs` (their behaviour is now covered in Tasks 2–3)
- Test: `Baird.Tests/Services/NetflixProviderTests.cs`, `Baird.Tests/ViewModels/MainViewModelNetflixTests.cs` (create; build `MainViewModel` the way existing tests do, e.g. `ContinuousPlaybackTests`)

**Interfaces:**
- Consumes: `NetflixPlaybackEngine` (Task 2), `VideoPlayer.NetflixEngine` (Task 3).
- Produces: `MainViewModel.NetflixEngine : NetflixPlaybackEngine?`, created in the constructor when `netflixSession != null`,
  with a flow factory `() => new NetflixSpikeViewModel(netflixSession.Browser, _netflixCredentials) { LogBrowserToJournal = false }`.

- [ ] **Step 1: Write the failing tests:**
  - `PlayItem_NetflixItem_ActivatesItLikeAnyVideo`: `PlayItem(netflixItem)` → `ActiveItem == netflixItem` and `CurrentPage is ShowingVideoPlayerViewModel`.
  - `Search_WhilePlaying_ReturnsNoResultsWithoutThrowing` (NetflixProviderTests): `session.IsPlaybackActive = true` → `SearchAsync("One Piece")` returns empty, does not throw, and makes no GraphQL call.
- [ ] **Step 2:** Run `dotnet test Baird.Tests --filter "MainViewModelNetflixTests|NetflixProviderTests"` → FAIL.
- [ ] **Step 3:** Implement. In `NetflixProvider.SearchAsync`, return empty when `_session.IsPlaybackActive` and log `[NetflixProvider] Netflix is playing; no Netflix results until it stops.`. Rewrite `RunNetflixCoreAutoTestAsync`: same search, then `PlayItem(first)`, await `NetflixEngine.FileLoaded` or `Failed` (120 s), and log `[AutoTest] RESULT core=PLAYING title="..." play_after=<s>s` or `core=FAILED reason="..."`. The spike tabs keep working without the Baird HUD (their `UseBairdHud` path now just plays in the hidden browser). That's acceptable per the spec; note it in the spike tab's status text.
- [ ] **Step 4:** Run `dotnet test Baird.Tests` → all PASS; `dotnet build Baird.sln` → 0 errors, and no remaining references to `NetflixHud` (`git grep -n NetflixHud Baird` → only `docs/`).
- [ ] **Step 5:** Commit: `Netflix plays through Baird's normal player; remove the separate HUD window`.

### Task 5: On the Pi, end to end

**Files:**
- Modify: `spikes/netflix-tooling/pi_autotest.sh` only if the `netflix-core` key sequence needs updating
- Modify: `docs/netflix-spike-findings.md` (replace §1 "Baird-drawn HUD" and "Show and hide" with the new way; Known gaps: OK doesn't pause any video, no Netflix search while playing, prompts)

- [ ] **Step 1:** Coordinate (Global Constraints), then `bash spikes/netflix-tooling/deploy_test_build.sh`.
- [ ] **Step 2:** `KEYS="RIGHT LEFT SPACE SPACE DOWN ESC" bash ~/baird-test/pi_autotest.sh netflix-core test shots` (or the script's equivalent for the core journey). Expected in the journal: `RESULT core=PLAYING`, `Showing the browser through the main window: True`, no `[NetflixHud]` lines, no focus-lost lines. Screenshots: Netflix video under Baird's normal HUD; pairs 0.5 s apart move while playing and show 0% change while paused; after DOWN, the menu over the still-moving video.
- [ ] **Step 3:** From the menu, pick the first live channel (keys as needed). Expected: journal `Showing the browser through the main window: False`, Netflix idle (`robots.txt`), live TV playing; 2 minutes of A/V Debug show the same drops as Task 1 check B.
- [ ] **Step 4:** Update `docs/netflix-spike-findings.md` with the results and timings.
- [ ] **Step 5:** Commit: `Netflix in the main player: Pi results and docs`.
- [ ] **Step 6:** Ask the user to try it with the real remote (Down → menu over Netflix, Back, pick a channel) before calling it done.
