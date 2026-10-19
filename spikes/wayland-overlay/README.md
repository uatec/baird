# Wayland overlay spike

**Question:** can mpv present video in its own Wayland window, so it gets real presentation
timing (and ideally a hardware plane), while Baird's UI sits on top in a separate transparent
window? If so, mpv presenting its own frames should remove the constant A/V offset caused by
today's embedding (mpv → Avalonia FBO → Avalonia compositor → X11).

```
 Baird UI   — transparent XWayland window (Avalonia), always on top, has keyboard focus
 ─────────
 video      — mpv's own Wayland window (--wayland-app-id=baird-video), always at the bottom
            ↓ stacked by labwc (window rules), ideally scanned out on a hardware plane
```

`OverlayTest` stands in for Baird: a translucent HUD bar with a moving dot (repaints at ~50 Hz),
and an opaque "menu" panel that toggles every 10 s.

## Already established (dev machine, headless labwc 0.20, mpv 0.41)

- Layering works: video visible through the translucent HUD, opaque panel hides it, overlay on top.
- Avalonia through XWayland gets `ActualTransparencyLevel=Transparent`; the overlay keeps focus.
- **`--vo=dmabuf-wayland` does not accept software-decoded frames** ("hwupload: failed to upload
  frame"), for H.264 and HEVC alike. It needs hardware-decoded (DRM PRIME) frames. On the Pi 5
  that means HEVC via `--hwdec=drm` *might* work, but Freesat H.264 (software-decoded) will need
  `--vo=gpu` or `gpu-next`, which still present their own frames with Wayland presentation feedback.

## What only the Pi can answer

1. Does HEVC work with `dmabuf-wayland` + `hwdec=drm`, and does the video land on a hardware plane?
2. Does H.264 1080i (live TV) play smoothly with `gpu`/`gpu-next` under labwc, at what CPU cost?
3. Is the HDMI colour range limited (16-235) under labwc? Under X11 this was pinned in xorg.conf.
4. Does lip sync feel right with no audio-delay tweak?

## Running it on the Pi

1. Build the kit on a dev machine and copy it over:

   ```sh
   spikes/wayland-overlay/make_kit.sh
   scp spikes/wayland-overlay/kit/baird-wayland-spike.tar.gz baird:
   ```

2. On the Pi, switch the desktop to labwc (reversible), install helpers, reboot:

   ```sh
   sudo apt install -y socat jq drm-info grim
   sudo raspi-config nonint do_wayland W3   # W3 = labwc. Revert later with W1 (X11).
   sudo reboot
   ```

3. Run the test (from a terminal in the session, or over SSH — it finds the session itself).
   It stops `baird.service` while running and restarts it afterwards, and temporarily adds two
   window rules to `~/.config/labwc/rc.xml` (restored on exit).

   ```sh
   tar xzf baird-wayland-spike.tar.gz && cd baird-wayland-spike
   ./run_test.sh --channel "BBC ONE HD" --source /path/to/some-hevc-file.mkv
   ```

   About 2 × 3 × 70 s per source. Watch the TV during the "overlay" phases (checklist at the end
   of the report). Repeat with `--video-sync display-resample` to compare sync modes.

4. Results land in `~/baird-wayland-spike/<time>/report.md` (plus per-second CSVs, mpv logs,
   DRM plane snapshots and screenshots).

5. Switch back to X11 for normal use until we decide: `sudo raspi-config nonint do_wayland W1 && sudo reboot`.

## Reading the result

- **Go** if: overlay `Transparent`, focus never lost, live H.264 plays without drops on `gpu`/`gpu-next`
  at similar or lower CPU than Baird today, colour range is limited (or can be forced), and lip sync
  feels right without an offset.
- **No-go / fallback** (keep one Avalonia window, move mpv rendering to its own thread) if
  transparency or stacking can't be made reliable, or the colour range can't be fixed under labwc.
