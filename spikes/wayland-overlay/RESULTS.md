# Wayland overlay spike — results (Pi 5, 2026-10-18/19)

Pi 5, Debian 13 (trixie) Pi OS, kernel 6.12.67-v8-16k, labwc 0.9.2, mpv 0.40.0, Panasonic TV on
HDMI-A-1 at 1920x1080@50. Live source: BBC One NE HD via local TvHeadend (Freesat H.264 1080i,
software-decoded, deinterlaced to 50 fps). VO drops are per ~60 s; 50 fps ⇒ 3000 frames/min.

## Headline findings

1. **The overlay architecture works on the Pi.** A transparent Avalonia window (via XWayland) on
   top of mpv's own Wayland window: `ActualTransparencyLevel=Transparent`, keyboard focus never
   lost, video visible through the HUD and hidden by opaque panels. labwc **scans out mpv's buffer
   directly on the primary plane and puts the UI on its own hardware overlay plane** (plane 105,
   AR24, zpos 1) — display-controller compositing, no GPU composition. The overlay costs ~7 % CPU
   itself plus ~5 % across labwc/XWayland/mpv.
2. **The biggest problem isn't the display stack — it's mpv's default render quality.** Standalone
   mpv on today's X11 session drops ~44 % of frames on live 1080i50 (GPU-bound). `--profile=fast`
   alone takes that to **0 drops** with no CPU increase. This very likely affects Baird today
   (same mpv renderer, plus Avalonia compositing) and could be the "subtle A/V" feeling (judder).
3. **HDMI colour range regresses under labwc**: after a real boot `Broadcast RGB = Automatic`
   (the washed-out-picture bug). Fix proven: set the connector property *before* labwc starts —
   `modetest -M vc4 -w <connector-id>:"Broadcast RGB":2` with lightdm stopped — and it persists.
4. **Zero-copy video planes are not available with this labwc**: `dmabuf-wayland` rejects
   software-decoded frames (all live H.264), and for hardware-decoded HEVC labwc 0.9.2 doesn't
   accept the decoder's NV12 + Broadcom SAND128 modifier (`0x0700000000065404`).
5. **mpv's Vulkan path is broken under labwc on the Pi**: `CreateSwapchainKHR` fails with
   `VK_ERROR_OUT_OF_HOST_MEMORY` ~100×/s (6000+ errors/min), and Vulkan can't import SAND HEVC
   frames either. `--gpu-api=opengl` is clean (0 errors) but picks a 10-bit (XR30) framebuffer.

## Live TV numbers (VO drops / 60 s, mpv CPU % of one core)

| Stack | Config | Drops alone | Drops with overlay | mpv CPU |
|---|---|---|---|---|
| X11 (today) | gpu, OpenGL | **1310** | – | 89 % |
| X11 (today) | gpu, Vulkan | 838 | – | 99 % |
| X11 (today) | gpu, OpenGL, `--profile=fast` | **0** | – | 94 % |
| X11 (today) | gpu, OpenGL, yadif to 25 fps | 0 | – | 127 % |
| labwc | gpu / gpu-next, Vulkan (swapchain errors) | ~190 | ~200 | 72–88 % |
| labwc | gpu, OpenGL | 885 | 1763 | 92 % |
| labwc | gpu, OpenGL, display-resample | 904 | 1823 | 97 % |
| labwc | gpu-next, OpenGL, display-resample | 3 | 327 | 101–110 % |

mpv's own A/V offset stayed within a few ms whenever frames weren't being dropped heavily; with
heavy drops + display-resample it wandered up to ~290 ms.

## Not yet measured

- labwc + OpenGL + `--profile=fast` (expected: drops gone, like X11) and whether the overlay then
  still costs drops.
- Lip sync / judder by eye under labwc vs X11.

## Recommendations

1. **Now, in Baird (X11):** apply `profile=fast` (or the expensive parts of it) in `MpvPlayer` and
   verify with the A/V Debug page: VO drops should go to ~0. This is independent of Wayland.
2. **Wayland move: viable, not urgent.** Layering, focus and hardware-plane composition of the UI
   work. Prerequisites: OpenGL (`gpu-api=opengl`) not Vulkan, `profile=fast`, a pre-labwc
   Broadcast RGB unit, hide the cursor plane. Re-run this spike with `--extra`-style
   `profile=fast` on labwc before committing to it.
