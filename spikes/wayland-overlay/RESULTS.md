# Wayland overlay spike — results (Pi 5, 2026-10-18/19)

Pi 5, Debian 13 (trixie) Pi OS, kernel 6.12.67-v8-16k, labwc 0.9.2, mpv 0.40.0, Panasonic TV on
HDMI-A-1 at 1920x1080@50. Live source: BBC One NE HD via local TvHeadend (Freesat H.264 1080i,
software-decoded, deinterlaced to 50 fps). Drops are per ~60 s; 50 fps ⇒ 3000 frames/min.

> **Lesson from run 1:** mpv's own counters are not proof that video reaches the screen. The
> first evening's Vulkan runs reported "~200 drops/min" while the TV showed a single frozen frame
> (every swapchain recreation failed). All results below marked ✔ were re-run with
> `motion_check.sh`, which compares real screenshots of the video area (distinct frames out of 10
> captured 200 ms apart). The checker was validated both ways: Baird's screensaver = 10/10, the
> broken Vulkan path = FROZEN 1/10.

## Headline findings

1. **mpv's default render quality is too heavy for the Pi 5 GPU — on today's X11 setup too.**
   Live 1080i50: defaults drop ~1370 frames/min (~45 %, ≈27 fps shown); **`--profile=fast` → 0 drops**
   at the same CPU. ✔ (both moving 10/10). This very likely affects Baird today and fits the
   "subtle A/V" feeling (judder).
2. **The overlay architecture works on the Pi** with OpenGL + `profile=fast`: transparent XWayland
   Avalonia window over mpv's own Wayland window, focus kept, picture moving, **0 drops with a
   realistic HUD (1 Hz repaint), overlay ≈3 % CPU** ✔. labwc scans mpv's buffer out on the primary
   plane and puts the UI on its own hardware plane (display-controller compositing).
   A worst-case overlay repainting full-screen at 50 Hz still costs ~900 drops/min ✔ — so Baird's
   UI must not animate continuously over video.
3. **mpv's Vulkan path (its default `gpu-api`) is broken under labwc on the Pi**: swapchain
   recreation fails ~100×/s (`VK_ERROR_OUT_OF_HOST_MEMORY`), the TV shows one frozen frame ✔.
   Use `--gpu-api=opengl` (clean, 0 errors; picks a 10-bit XR30 framebuffer).
4. **HDMI colour range regresses under labwc**: after a real boot `Broadcast RGB = Automatic`
   (the washed-out-picture bug). Fix proven: set the connector property before labwc starts —
   `modetest -M vc4 -w <HDMI-A-1 id>:"Broadcast RGB":2` with lightdm stopped — and it persists.
5. **No zero-copy video planes with this labwc**: `dmabuf-wayland` rejects software-decoded frames
   (all live H.264); for hardware-decoded HEVC labwc 0.9.2 rejects the decoder's NV12 + Broadcom
   SAND128 modifier (`0x0700000000065404`).

## Verified live-TV results (✔ = picture motion confirmed from screenshots)

| Stack | mpv config | Overlay | Picture | VO drops/min | mpv CPU |
|---|---|---|---|---|---|
| X11 (today) | gpu, OpenGL, defaults | – | ✔ 10/10 | **1368** | 85 % |
| X11 (today) | gpu, OpenGL, `profile=fast` | – | ✔ 10/10 | **0** | 88 % |
| labwc | gpu-next, Vulkan (default) | – / 50 Hz | ✘ **FROZEN 1/10** | (141–150, meaningless) | – |
| labwc | gpu, OpenGL, `profile=fast` | – | ✔ 10/10 | 0 | 89 % |
| labwc | gpu, OpenGL, `profile=fast` | 50 Hz worst case | ✔ 10/10 | 911 | – |
| labwc | gpu, OpenGL, `profile=fast` | **1 Hz realistic** | ✔ 10/10 | **0** | 95 % (+3 % overlay) |

Earlier counter-only runs (not motion-verified; Vulkan ones known to be frozen) are in the
per-run `phases.md` files on the Pi under `~/baird-wayland-spike/`.

## Recommendations

1. **Now, in Baird (X11):** apply `profile=fast` in `MpvPlayer` (and drop the 10-bit/high-quality
   scaler defaults). Verify with the A/V Debug page (VO drops → 0) and by eye. Independent of Wayland.
2. **Wayland move: viable.** Requirements: `gpu-api=opengl`, `profile=fast`, a boot-time
   Broadcast RGB unit before lightdm, hide the cursor plane, and keep the UI from repainting
   continuously over video. Still to judge by eye: lip sync under labwc vs X11.
