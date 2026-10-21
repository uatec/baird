# Spike 1: Native Lottie Vector Animations in Avalonia UI

## Overview & Goal
The objective of this spike is to evaluate and demonstrate high-fidelity vector animation running natively inside Avalonia on Linux (Manjaro / Raspberry Pi 5 & 4) using Lottie.

## Technology Stack & Packages
- **Avalonia UI**: `11.3.11` (.NET 9)
- **Lottie Package**: `Avalonia.Labs.Lottie` version `11.3.1` (maintained by the official AvaloniaUI project team)
- **Native Vector Engine**: `SkiaSharp.Skottie` version `2.88.8` via SkiaSharp's `libSkiaSharp.so` rasterizer.

## Key Findings & Raspberry Pi / Linux Performance
1. **Full Linux Compatibility**:
   - `Avalonia.Labs.Lottie` 11.3.1 binds directly against Avalonia 11.x and SkiaSharp 2.88.8.
   - SkiaSharp's native binaries (`libSkiaSharp.so`) are already bundled for `linux-arm`, `linux-arm64`, and `linux-x64` in the Avalonia Skia pipeline.
   - Skottie is built into `libSkiaSharp.so`, meaning no external C++ dependencies or browser runtimes are required.

2. **Rendering Performance vs Web/GIF Alternatives**:
   - Unlike GIF or APNG sequences that consume megabytes of frame buffers and degrade when scaled to 4K / 1080p, Lottie JSON files are small vector files (3 KB – 12 KB).
   - Skottie evaluates parametric Bézier curves on each frame directly onto Avalonia's drawing context (`SKCanvas`), keeping memory footprint minimal and rendering crisp vector graphics at any DPI or UI scale factor.
   - On Raspberry Pi 5 under Linux DRM/KMS or Wayland, Skottie renders through GPU-accelerated OpenGL / Vulkan backends.

3. **Runtime & Animation Capabilities**:
   - Controls support `Play()`, `Pause()`, `Resume()`, `Stop()`, `SeekToProgress(0.0 .. 1.0)`, `SeekToFrame(frame)`.
   - Speed can be varied dynamically (`PlayBackRate` 1x, 2x, 3x, etc.).
   - Looping supports infinite repetition (`RepeatCount = -1`) or finite repetitions (`RepeatCount = 1, 2, ...`).
   - Events `AnimationCompleted` and `AnimationCompletedRepetition` allow seamless synchronization with application state (e.g. tuning timeout or playback state).

## Sample Vector Assets Provided
Four clean sample vector animations have been added under `Baird/Assets/Lottie/` and compiled as Avalonia resources:
1. `loading-ring.json` (`avares://Baird/Assets/Lottie/loading-ring.json`): Smooth vector loading spinner for buffering, channel tuning, and background sync.
2. `audio-bars.json` (`avares://Baird/Assets/Lottie/audio-bars.json`): Dynamic dancing equalizer bars with gradient vector fill for radio / music playback.
3. `play-pause.json` (`avares://Baird/Assets/Lottie/play-pause.json`): Interactive vector play-pause transition animation for transport controls and CEC remote feedback.
4. `wave-loader.json` (`avares://Baird/Assets/Lottie/wave-loader.json`): Geometric undulating wave animation for speech-to-text (Whisper.net) listening feedback.

## Interactive UI & Verification
- **ViewModel**: `Baird/ViewModels/LottieSpikeViewModel.cs`
- **Control**: `Baird/Controls/LottieSpikeControl.axaml` and `Baird/Controls/LottieSpikeControl.axaml.cs`
- **Tab Integration**: Embedded as the "Lottie Spike" tab in Baird's main navigation menu (`TabNavigationControl`), allowing live interaction directly in the app.
- **Controls Available**:
  - Live Animation Viewport (rendered at 320x320 with uniform scaling)
  - Interactive Animation Selector (switch between 4 vector animations)
  - Transport Controls: Play/Pause toggle, Restart, Infinite Loop toggle
  - Playback Speed Adjuster: 1x (Normal), 2x (Fast), 3x (Turbo), 4x (Max)
  - Scrubber: Seek slider (0% to 100%)
  - Live technical stats (status pill, repeat count, loop iterations, Skottie native info)

## Build Verification
- Project builds cleanly with `dotnet build Baird/Baird.csproj`.
- Dedicated unit tests are in `Baird.Tests/ViewModels/LottieSpikeViewModelTests.cs` and `Baird.Tests/Controls/LottieSpikeControlTests.cs`.
