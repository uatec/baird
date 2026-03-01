# Product Requirements Document: Baird Media Platform

## 1. Executive Summary
**Baird** is a high-performance, embedded smart-TV application designed for the Raspberry Pi 5. Built using Avalonia UI and `.NET 9`, Baird operates entirely via the Linux Framebuffer utilizing DRM/KMS support. This "kiosk-style" design completely negates the need for a heavy desktop environment like X11 or Wayland while providing native graphics pipeline acceleration.

Its primary mission is to aggregate disparate media APIs—Live TV (TvHeadend), Local Media (Jellyfin), On-Demand (BBC iPlayer, YouTube), and Media Requests (Jellyseerr)—into a seamless, unified "10-foot UI" experience controlled via a standard television remote control (HDMI-CEC).

## 2. Target Platform & Architecture
- **Hardware Target:** Raspberry Pi 5 (Linux ARM64).
- **Video Engine:** Embedded `libmpv` utilizing VideoCore hardware decoding (`hwdec=rpi`) and specialized rendering for UK broadcast deinterlacing.
- **UI Framework:** Avalonia UI running securely on the Linux Framebuffer.
- **Backend logic & tooling:** C# .NET 9.0; built using Ahead-of-Time (AOT) compilation natively cross-compiled to minimize startup latency and JIT overhead.
- **Control Input:** Standard D-Pad TV interfaces via HDMI-CEC events, mapped seamlessly to keyboard key strokes inside the UI.

## 3. Core Functional Requirements

### 3.1 Unified Media Aggregation (Providers)
Baird must fetch, normalize, standardize, and present media content from multiple distinct backend services via a single interface contract (`IMediaProvider`):
- **TvHeadend:** Live TV streaming, robustly handling multiple internal streams (e.g. prioritizing standard English audio over audio descriptions), caching of authentication digest headers, and fast channel navigation.
- **Jellyfin:** Personal local media library, querying and tracking progress for movies and TV series.
- **BBC iPlayer:** Native integration for searching and rendering UK Catch-Up TV.
- **YouTube:** API integration for standard web video payloads.
- **Jellyseerr:** A backend request management system for unobtainable media.

### 3.2 Global OmniSearch
- **Unified Querying:** Present a global search field that securely queries all mounted media providers simultaneously.
- **Visual Feedback:** Provide real-time UI statuses (via Provider Search Status pips) indicating loading state, successful fetches, or query timeouts on a per-provider basis.
- **Organization:** Deduplicate shows/movies where logic applies, ensuring episodes are filtered to avoid clutter. 
- **Query History:** Track and render user search history allowing quick resume of previously entered terms.

### 3.3 Advanced Video Playback (LibMpv)
- **Engine Reliability:** The embedded player (`VideoLayerControl`) must securely integrate `libmpv` bindings dynamically per OS (Linux `.so` vs macOS `.dylib` for local testing).
- **Continuous Playback:** Automatically queue and play successive episodes within a series, featuring a cancelable countdown dialog bridging transitions. 
- **Subtitles & Audio Processing:** Natively expose subtitle hints and stream toggles within the UI space directly overlapping the stream context.
- **Focus Authority:** The video player must reliably defer input focus whenever overlay menus (History, Settings, Search) are brought into view, guaranteeing paused playback inputs never trigger background operations.
- **HUD Intelligibility:** A Heads-Up Display (HUD) indicating video title, timeline, and timestamps must auto-hide efficiently during active playback but remain permanently glued to the screen during paused, loading, or aborted play states.

### 3.4 User State, Navigation, and Library Management
- **Directional Navigation:** Application control must be confined to pure directional grids (Up, Down, Left, Right, Enter/OK, Back). 
- **Carousel Tabbing:** Main view navigation must be represented via an infinitely scrolling, top-centered Carousel Tab architecture.
- **History ("Continue Watching"):** Maintain a dedicated view of recently viewed tracking items (resuming exact media timestamps) consistently maintained in cached `MediaItem` instances to prevent UI synchronization lag.
- **Watchlist ("Faves"):** Allow users to execute prolonged "hold-to-add" remote interactions pinning media directly to an instant-access grid view.
- **Programme Details Pane:** Display an information pane carrying detailed synopses, episode runtime markers, and strict hierarchical depth to prevent recursive navigation loops on back-presses.

### 3.5 Media Download Requests (Jellyseerr Integration)
- Connect directly to Jellyseerr allowing users to query non-existent media locally.
- Instantiate "Download Requests" from within the global search results.
- Incorporate a distinct "Requests Tracking" UI segment distinguishing thoroughly completed downloads from in-progress workloads using distinct visual saturation metrics (e.g. grayscale for incomplete metadata, precise progress bars).

### 3.6 Ambient Cinematic Mode (Screensaver)
- Render an Apple TV-style cinematic aerial screensaver leveraging dynamic JSON endpoint feeds.
- Trigger activation automatically after measured user-input inactivity bounds (e.g. 120 minutes of no D-Pad actions).
- Ensure the screensaver halts gracefully with a single input event preventing overlap or interference with global player state logic.

## 4. System Lifecycle & Maintenance
- **Native Execution (Systemd):** Run natively using a structured `baird.service` enforcing proper GPU memory mapping, udev permission elevation (modding `/dev/dri/card0`), and preventing screen blanking.
- **Self-Healing and Auto-Update:** Include `systemd` timers enforcing periodic update checks pulling latest binaries, applying new package iterations via system hooks (e.g., Homebrew/bash updates), and gracefully restarting the UI service offline.

## 5. Non-Functional Testing Requirements
- **Local Dev Workflow:** Must cleanly spawn windowed desktop builds on macOS environments automatically resolving `libmpv` via Homebrew scripts strictly for developer velocity. 
- **Diagnostic Tooling:** Key combos (e.g. 'D' key for Avalonia DevTools, 'S' key for native configuration environment dumps) must remain dynamically attachable.
- **Telemetry logging:** System `Console.WriteLine` metrics must include prefixed Component IDs (e.g., `[VideoPlayer]`, `[TvHeadend]`) for rigorous systemd `journalctl` tracking.
