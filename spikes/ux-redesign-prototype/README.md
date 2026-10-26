# Baird UX redesign: working prototype

A remote-driven HTML prototype of the redesigned Baird: Home, Live TV (the guide), Search, show pages, and the live and
on-demand playback layers. Design: `docs/superpowers/specs/2026-10-25-baird-ux-redesign-prototype-design.md`.
Nothing here is wired to real services. The catalogue, schedule and playback are mocked, and the artwork is real
(from TMDB).

## Run it

```bash
bash serve.sh            # http://100.109.40.12:8077/  (Tailscale IP, no caching)
```

Open it full screen (F11, or `chromium --kiosk <url>` on the Pi). On the Pi it switches to **lite mode** automatically
(no live blur, grain or Ken Burns drift), detected from the GPU (Broadcom V3D, or software rendering). `?lite` forces
it anywhere; `?full` turns it off.

Check with the other Baird sessions before opening it on the Pi: its browser competes with `baird.service` for the GPU.

## Remote and keyboard

| Remote | Keyboard |
|---|---|
| Arrows | ↑ ↓ ← → |
| OK / hold OK | Enter / hold Enter (half a second) |
| Back · Home | Esc or Backspace · H |
| ≡ (Guide) | G |
| ⏯ · ⏮ ⏭ | Space · `[` `]` |
| Pg+ / Pg− | Page Up / Page Down |
| 0–9 · DEL | 0–9 · Delete |
| Voice | V (types "simpsons") |

Debug keys: **?** shows this map on screen, **K** shows the key inspector (the `key`/`code` of each press, to check the
real remote's codes on the Pi), **F** jumps to near the end of what's playing (end card / "up next on this channel"),
**R** jumps to just before your next reminder (to see the countdown card).

URL helpers for screenshots: `#guide=c4`, `#search=simpsons`, `#show=loki`, `#play-live=bbc1+ok`, `#play-vod=loki+end`,
plus `?hold` to stop overlays dismissing themselves.

## Things to try

New in revision 2: the tab bar (▴ from the top of any place, then ◂ ▸ switch straight away), the guide's columns layout
(≡ then the last item in the day strip, or Settings › Guide layout), All showings and Remind me (hold OK in the guide;
then R), per-season availability and requests (Andor, Stranger Things), scoped watchlist entries, scrubbing live TV
with ◂ ▸, the tuning transition (Pg±), and artwork fallbacks (BBC One's "Homes Under the Hammer", Talking Pictures TV).

- **Up next:** OK on Loki → F → the end card counts down to S1 E5 (⏭ plays it now).
- **Live TV:** On now → OK. Then ↑↓ flick channels (tunes when you rest), Pg± tunes straight away, type 1 0 4,
  OK shows the info bar (◂ ▸ for subtitles), and hold OK opens the actions panel.
- **Guide:** G. Then ≡ again for the day strip, type a channel number, and hold OK on a programme for Next showing,
  This channel's schedule, or Open the series.
- **Play vs get:** search "simpsons" (▶ Play, plus where it's on TV) vs "dune" (⤓ Get it). Requested titles download in the
  background and land in Up next.
- **Back** always retraces the journey: overlays close back to the video, and pages step back to Home.

## Tests and tools

```bash
npm test                          # node --test: keys, data/schedule, navigation, focus, search
node tools/walkthrough.mjs        # real-time CDP walk of the whole journey → /tmp/baird-walk/*.png
node tools/regress.mjs            # behaviour regressions from the review (overlay keys, catch-up, Back, search…)
SLOW=1 node tools/walkthrough.mjs "http://100.109.40.12:8077/?lite"   # same, no GPU, reports long tasks
tools/shot.sh out.png 'guide=c4' hold                                  # one headless screenshot
node tools/fetch-art.mjs && node tools/fetch-episodes.mjs && tools/make-ambient.sh   # re-fetch art and episodes (curl, no key)
```

## Animation moments: for the Lottie vs Rive comparison

Everything moves with CSS today (transform/opacity, 220–600 ms). These are the moments where a richer animation
runtime would show, and what each needs:

| Moment | Where | What it needs |
|---|---|---|
| Focus lift and sheen | every card, button, key | Respond instantly to input, run hundreds of times an evening. CSS is ideal here. |
| Layer slides | Home ↓, channel list ←, actions →, info bar ↑, guide ↑ | Interruptible, direction-aware. CSS or the app's own transitions. |
| End-card countdown ring | on-demand "Up next" | Progress driven by a number (8 → 0), cancellable. A **Rive** state machine input fits. |
| Download/request progress ring | Up next card, show page button | Driven by real progress %, then a "ready" flourish. **Rive** (number input + trigger). |
| LIVE pulse | live pills | Tiny loop. CSS is enough. |
| Channel-tune transition | flicking channels, Pg± | Brief cover while the tuner locks (seconds on DVB). Lottie or Rive loop, ideally with a "locked" trigger. |
| Hero cross-fade and ambient light | Home, show pages | Image cross-fade. CSS. |
| Voice listening | search field | Reacts to mic level. **Rive** (number input). |

The pattern: decorative one-shots suit Lottie, but the moments this design actually needs are *driven by app state*
(a countdown, a %, a level, a "locked" signal). That favours Rive's state machines, provided its runtime risks (see
`spikes/rive-avalonia-spike/README.md`) are acceptable.
