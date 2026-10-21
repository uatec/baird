# Spike 3: Rive Control within the Web View

This technical spike evaluates the feasibility, performance, and developer experience of embedding **Rive vector graphics and state machines** in a browser / Web View context using the official **`@rive-app/canvas` WebAssembly runtime**.

This component can serve as Baird's next-generation 10-foot UI overlay on top of hardware-accelerated video playback (including Widevine DRM streams such as Netflix, BBC iPlayer, and live Freesat broadcast TV).

---

## Architecture Overview

```
 ┌──────────────────────────────────────────────────────────────┐
 │                     Baird Application / Browser              │
 │                                                              │
 │   ┌──────────────────────────────────────────────────────┐   │
 │   │  Rive WASM HUD Layer (HTML5 Canvas, alpha: true)     │   │
 │   │  • Vector rasterization via WebAssembly (Skia/Rive)  │   │
 │   │  • State Machine input bindings (D-Pad, CEC remote)  │   │
 │   │  • Real-time 60fps animations at 4K resolution       │   │
 │   └──────────────────────────┬───────────────────────────┘   │
 │                              │ Blended / Wayland Subsurface  │
 │   ┌──────────────────────────▼───────────────────────────┐   │
 │   │  Video Plane (<video> / Widevine DRM / mpv dmabuf)   │   │
 │   │  • Hardware-accelerated decode (V4L2 M2M / DRM PRIME)│   │
 │   │  • Dedicated hardware overlay scanout plane          │   │
 │   └──────────────────────────────────────────────────────┘   │
 └──────────────────────────────────────────────────────────────┘
```

### Components Included in this Spike

1. **Official Rive WASM Runtime (`@rive-app/canvas` v2.45.0):**
   - Bundled locally (`rive.js` and `rive.wasm`) for offline execution, with automatic CDN fallback.
   - High-DPI canvas scaling (`window.devicePixelRatio`) ensuring crisp vector rendering at 1080p and 4K TV displays.

2. **Interactive Rive Assets with State Machines:**
   - **`digital_music_player.riv` (Primary Baird HUD Model):** Demonstrates an interactive media player HUD with:
     - State machine inputs: `isPlaying` (boolean), `trackProgress` (number 0-100 scrub bar), `volume` (number 0-11 audio level), `nextTrackTrig` / `prevTrackTrig` (triggers).
     - Dynamic text run binding: Live replacement of `track`, `artist`, `timeStart`, and `timeEnd`.
     - Direct canvas hit testing: Clicking buttons and dragging knobs on the canvas triggers state machine transitions and emits Rive events.
   - **`rating.riv`:** Interactive star rating and emotional reaction state machine.
   - **`vehicles.riv`:** Classic Rive vehicle physics simulation with suspension bump triggers.

3. **Widevine DRM Video Overlay Simulation:**
   - Simulates active hardware-accelerated DRM video playback beneath the transparent Rive canvas.
   - Demonstrates zero tearing, transparent vector alpha blending, and seamless D-Pad remote control over moving video frames.

4. **10-Foot TV Remote / D-Pad Keystroke Mapping:**
   - **`Space` / `Enter`**: Toggle Play / Pause (`isPlaying`).
   - **`←` / `→`**: Seek backward / forward 5% (`trackProgress`).
   - **`↑` / `↓`**: Adjust volume (`volume`).
   - **`O`**: Toggle simulated Widevine DRM video background.
   - **`1`, `2`, `3`**: Switch between Rive assets.
   - **`F`**: Enter / exit fullscreen 10-foot mode.

5. **Diagnostic Telemetry & Real-Time Event Stream:**
   - Real-time FPS monitor.
   - Canvas pixel dimensions and display DPI telemetry.
   - Event stream logging events fired by Rive (`EventType.RiveEvent`).

---

## Technical Findings

### 1. Performance on Linux & Wayland (e.g. Raspberry Pi 5 / Manjaro)
- **Compact Asset Footprint:** Rive `.riv` vector files are exceptionally lightweight (`digital_music_player.riv` is ~37 KB; `vehicles.riv` is ~58 KB). By comparison, equivalent raster sprite sheets or video overlays require tens of megabytes.
- **WebAssembly Efficiency:** The `@rive-app/canvas` WASM engine executes the scene graph evaluation, state machine logic, and bone deformation on CPU via WebAssembly with sub-millisecond frame times.
- **Wayland Compositor Integration:** Under modern Wayland compositors (such as `labwc` or `sway` on Raspberry Pi OS), transparent web canvases can be composited directly or mapped to Wayland subsurfaces. When paired with hardware-decoded video (`DRM PRIME`), the video can remain on an overlay plane while the web compositor blends the UI on top.

### 2. Widevine DRM Video Overlay Integration
- In browser contexts (Chromium, Firefox, or WPE WebKit), Widevine DRM video decrypts into protected video buffers.
- By configuring the Rive `<canvas>` with `pointer-events: auto` and a transparent background (`alpha: true`), the Rive UI hovers directly over the `<video>` element without intercepting or disrupting the hardware decode pipeline.
- Suppression of standard desktop player controls (as prototyped in `spikes/baird-netflix-poc`) allows the custom Rive 10-foot HUD to act as the primary interface.

### 3. Comparison: Avalonia UI vs. Rive Web Overlay

| Dimension | Avalonia UI (Current Baird) | Rive Web Overlay (Spike 3) |
|---|---|---|
| **Designer Tooling** | XAML / C# code-behind | Visual state machine editor (rive.app) |
| **Animation Fidelity** | Property transitions, keyframe animations | Skeletal rigging, blend states, bone IK, physics |
| **Asset Size** | Raster assets (MBs) or SVG paths | Microscopic vector binaries (20–60 KB) |
| **Widevine Integration** | Requires XWayland window layering / mpv hack | Native HTML5 transparent canvas over `<video>` |
| **CPU/GPU Usage** | Moderate Avalonia render tree overhead | Highly optimized C++/WASM renderer |
| **Remote Control (CEC)** | C# KeyDown events in Avalonia visual tree | Native JS keyboard listeners bound to SM inputs |

---

## How to Test

### Method A: Standalone Launcher (Recommended)

1. Run the launcher script from the repository root:
   ```bash
   ./spikes/rive-web-spike/run.sh
   ```
   Or run the Python server directly:
   ```bash
   python3 spikes/rive-web-spike/serve.py
   ```
2. The server will start on `http://127.0.0.1:8088/` (or next available port) and open your browser automatically.
3. Test the interactive controls:
   - Click **Play / Pause** or press **`Space`**: Observe the play icon and progress counter activate.
   - Drag the **Track Scrub Bar** or press **`←` / `→`**: Watch the timeline scrub smoothly.
   - Adjust the **Volume** slider or press **`↑` / `↓`**: See the volume knob in Rive rotate.
   - Click directly on the Rive canvas: Click the play/pause button or knobs to test native canvas hit-testing.
   - Toggle **Background: Video Simulation**: Verify transparent alpha blending over moving video.
   - Switch asset to **Star Rating** or **Vehicle Suspension** to test multi-model support.

### Method B: From within Baird Application

A dedicated control and view model have been integrated into Baird:
- ViewModel: `Baird.ViewModels.RiveWebSpikeViewModel`
- Control: `Baird.Controls.RiveWebSpikeControl`
- Tab: Available in Baird's Main Menu under **"Rive Spike"**!
- From Baird, click **"Launch Web Spike in Browser"** to automatically start the embedded HTTP server and open the spike.
