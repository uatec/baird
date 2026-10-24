# Netflix in Baird's main player (transparent main window)

Date: 2026-10-24. Status: approved in chat, before the plan.

## Intent

Netflix must feel like any other video in Baird. Today (release 0.0.226) it is a separate mode: Baird hides its main
window, Chromium shows, and a second transparent window (`NetflixHudWindow`) draws a small Netflix-only HUD and takes the
keys. Baird keeps all input, which is good, but none of Baird's normal playback behaviour works. The user found that
**Down does not open the menu** over Netflix, and took that as the sign that Netflix is not integrated.

The new way: Baird's **main window** becomes transparent where the video is, so the Chromium window behind it shows
through. Baird keeps focus the whole time and handles input as it already does. The video player sends commands to the
browser (over CDP, as now); the Netflix page never controls itself.

## Goals

- When Netflix plays, the main window stays open with focus. The video area is transparent, and Chromium (behind
  Baird) shows through. No second window.
- Baird's normal video layer and HUD (`VideoLayerControl`: title, progress bar, time, time remaining, "finishing at",
  remote hints, HUD timeout) work for Netflix as they are.
- Baird's normal playback keys work for Netflix as they are: ◄/► seek 10 s, Space/Play/Pause, **Down opens the
  menu over the video**, digits start a search, G opens the guide, Q/Back as for any other video. Netflix keeps playing
  behind the menu, as other videos do.
- Picking something else from the menu (a live channel, another video) stops Netflix, as it would stop any other video.
  The window is opaque again for mpv.
- History, Continue Watching, resume and "Back returns to where you were" go through the same paths as other videos.
- The browser stays as it is now: one shared Chromium (`NetflixSession`, port 9222), always open behind Baird, signed in,
  idle on `netflix.com/robots.txt`.

## Non-goals (for this change)

- Netflix prompts ("Skip intro", "Next episode", "Are you still watching?"). Still a known gap. They need Baird-drawn
  buttons later.
- mpv-only extras over Netflix: the stats overlay (Tab), audio delay, the A/V debug sampler, the quality settings.
  Over Netflix they must do nothing harmful (no-op), nothing more.
- Subtitles over Netflix (the `Apps` key / SUB marker). No-op for now; wiring Netflix's subtitle API comes later.
- Disney+. Another agent's spike shares the browser profile. The engine seam below should let a Disney engine come later,
  but this change does not touch Disney code.
- The spike tabs ("Netflix Spike", "Netflix Search"). They are not part of the core journey.
- **Netflix search while Netflix plays** (user, 2026-10-24: accept for now). `NetflixSession.SearchAsync` refuses while
  `IsPlaybackActive`. Search must show Netflix as busy, not as a failed provider; picking another Netflix title means
  stopping the current one first. Searching in a second tab comes later.
- **OK to pause** (user, 2026-10-24: leave out for now). OK (remapped to `Key.Enter`) does not pause mpv videos today
  although the hint bar says it does; Netflix behaves the same. Space / Play / Pause pause. Fixing OK for every video
  (careful with "Hold OK → Watchlist") comes later.
- Chromium relaunches outside the Netflix journey (watchdog relaunch during live TV) keep today's behaviour: it may take
  focus briefly and `CdpBrowserService` hands it back (my call; tightening it is separate).

## Anti-goals (failure even if it "works")

- A second window over Netflix, or any focus handoff to Chromium. Chromium must never get the keys. (The user chose
  "Baird hijacks ALL input" on 2026-10-23.)
- Netflix-specific key handling or a Netflix-specific HUD layout in the core journey. If a key does X for other videos, it
  does X for Netflix, or nothing.
- **Worse mpv playback.** The Pi plays live TV with 0 dropped frames today (`profile=fast` + `bwdif`, 25 fps, labwc). If
  making the main window transparency-capable costs frames for mpv, that is a failure. See "Main risk".
- Chromium visibly flashing, or Baird losing fullscreen, when going in or out of Netflix.

## Constraints

- Target: Raspberry Pi 5, labwc (Wayland), Baird under XWayland, Avalonia, mpv rendered through `VideoPlayer`
  (`OpenGlControlBase`), Chromium kiosk under the same compositor. No H.264 hardware decode.
- Under X11/XWayland a window can only be transparent if it was created with an ARGB visual. Avalonia picks that from
  `TransparencyLevelHint` **when the window is created**. Switching the hint on a running window may not work.
- Known XWayland behaviour (from the HUD spike): `Hide()`/`Show()` loses fullscreen, and setting the same `WindowState`
  again does nothing. This design avoids `Hide()` altogether.
- The shipped build is trimmed. Bindings in code use reflection. Use compiled XAML bindings or set values directly.
- Pi testing is shared with other agents. Before any deploy or test on the Pi, message the other Baird agents
  (ListAgents → SendMessage), say what the test touches (baird.service, ~/baird-test, port 9222, browser profile), and
  wait about 5 minutes for replies.
- Netflix player commands: use Netflix's player API (`ControlNetflixPlayerAsync`: `seek`, `play`, `pause`). Don't set
  `video.currentTime` directly (it can cause Netflix error M7375). Status comes from `PollVideoPlaybackAsync`.

## The how: decided

1. **The video player gets a second engine.** `VideoPlayer` drives an engine instead of `MpvPlayer` directly. There are
   two engines: mpv (today's behaviour, unchanged) and Netflix (CDP on the shared browser). `VideoPlayer` chooses the
   engine from the item: `NetflixProvider.IsNetflix(item.Source)` → Netflix, otherwise mpv. Everything above
   `VideoPlayer` (HUD bindings, `IsPaused`, `PositionSeconds`, `DurationSeconds`, `StreamEnded`, `SaveProgress`, key
   events) stays the same.
2. **Netflix plays through the normal play path.** `MainViewModel.PlayItem` stops sending Netflix items to
   `PlayNetflix`. They take the same path as any other video (`ActivateChannel` + `ShowingVideoPlayerViewModel`), so
   menu, history and back-stack behaviour are shared. *(Builder's call: if one Netflix-specific branch is really needed,
   say for resume, since Netflix resumes at its own saved place, keep it small and inside the engine.)*
3. **Transparency.** While the Netflix engine is active, `VideoPlayer` clears its area to fully transparent (alpha 0)
   instead of drawing mpv. Nothing opaque sits between it and the window: the window background, the root grid, and
   the video layer's loading overlay once the video plays. Everything else in Baird (HUD, menu pages, EPG overlay,
   screensaver) draws as normal on top. When the Netflix engine stops, the window is opaque black again.
4. **Netflix engine responsibilities.** Bring in the proven pieces, but no new UI: run the playback flow on the shared
   browser (today `NetflixSpikeViewModel` with `KeepBrowserOpen`, `UseBairdHud`, the page style script that hides
   Netflix's own UI), `NetflixSession.EnsureReadyAsync` / `IsPlaybackActive`, polling (about 500 ms, as
   `NetflixHudViewModel.PollAsync`), and commands (`ControlNetflixPlayerAsync`). Report to `VideoPlayer` the same things
   mpv reports: loading → playing, paused state, duration, position, ended, load failure. On stop, return the browser to
   its idle page.
5. **Loading and failures show in the normal player.** While the flow starts (sign-in check, navigate, buffering), the
   video layer's loading overlay shows the flow's simple step text ("Starting Netflix…", "Loading the video…"; today's
   `NetflixPlaybackViewModel.StatusText`). A failure (not signed in, MFA needed, no video within the timeout) shows its
   reason there, with "Press Back to return". So the `NetflixPlaybackViewModel` page goes away from the core journey.
6. **Remove the separate HUD path from the core journey.** `MainView.ShowNetflixHud`, `MainViewModel.NetflixHudRequested`
   and `PlayNetflix`, and the core use of `NetflixHudWindow`/`NetflixHudViewModel` go. Port their tests
   (`NetflixHudViewModelTests`, `NetflixPlaybackViewModelTests`) to the engine. Keep or delete the classes depending on
   whether the spike tabs still use them (builder's call; deleting the spike tabs' HUD mode is fine).
7. **Live TV.** Netflix replaces the current video like any other item does, so mpv is stopped, not paused. The special
   "pause live TV while Netflix plays, restore afterwards" code goes.
8. **`BAIRD_AUTOTEST=netflix-core`** is updated to the new journey (search → play first Netflix result → keys → Down
   opens the menu over Netflix → Back) and stays the Pi check.

## Main risk: the window's transparency on the Pi

This has to be checked on the Pi **first**, before the rest is built:

- **Can the main window go transparent while running?** The spike proved a window that was transparent from creation,
  not a running window that switches. The expected approach: create the main window with
  `TransparencyLevelHint = Transparent` from the start (so it gets an ARGB visual), give it an opaque black background
  normally, and only make the video area transparent while Netflix plays.
- **Does a transparency-capable main window cost mpv frames?** An ARGB fullscreen window can make the compositor blend
  every frame instead of passing the window through untouched. Measure live TV dropped frames (A/V debug page / mpv
  counters, *and* a screenshot diff or a person watching, not counters alone) with and without the hint, same channel,
  same settings. If it costs frames, don't go ahead with "always ARGB". Bring the numbers back to the user before
  picking another approach. Two fallbacks to put to the user: set the hint only while Netflix plays (if switching works
  on XWayland), or recreate the main window for Netflix (must keep fullscreen, focus and state).
- **Z-order.** Chromium must stay below Baird and never take focus when it navigates to a watch page. Check that no
  Chromium frame flashes over Baird.

Spike with a minimal change (hint plus a key that toggles a transparent video area while Chromium shows a page behind)
before moving `VideoPlayer` onto engines.

## Left to the builder

- Engine interface shape, names, and file layout (e.g. extract an interface from `MpvPlayer`'s surface).
- How `VideoPlayer` swaps engines (one of each kept alive, or created per item), as long as mpv's GL render context is
  not torn down and rebuilt on every switch.
- What the Netflix engine does at video end. Minimum: when the watch page has no video any more, report "ended" so
  `PlayNextEpisodeOrGoBack` runs. Netflix autoplaying the next episode in the same page can count as still playing.
- Whether the HUD's live-TV-only parts need guards for Netflix items (they should hide already, since Netflix items are
  not live).
- How to make the loading overlay show engine status text (new player property or reuse `PlayerState`).
- Updating `docs/netflix-spike-findings.md` to describe the new way and drop the separate-window description.

## How we'll know it's done

- On the Pi, with Netflix playing through the core journey: the video shows under Baird's normal HUD. ◄/► seek, Space
  pauses, **Down opens the menu over the playing video**, Back from the menu returns to Netflix still playing, and
  picking a live channel stops Netflix and plays the channel with an opaque window. Checked with `pi_keys.py`
  (uinput, through labwc) and screenshot diffs, then by a person with the real remote.
- No second window appears (journal and screenshots), and Chromium never gets focus during the Netflix journey.
- Live TV dropped frames are no worse than before the change.
- Unit tests cover engine selection and the Netflix engine against a fake `ICdpBrowserService` (the ported HUD and
  playback tests).
