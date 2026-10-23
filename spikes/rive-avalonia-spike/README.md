> **Update 2026-10-23:** linux-arm64 is now built (Debian 12 container, `native/build-arm64.sh`) and Rive renders on the Pi, after embedding the `.riv` files as Avalonia resources. See `docs/netflix-spike-findings.md` §3.

# Rive in Avalonia (in-process) - spike

## Verdict: VIABLE on linux-x64 (proven); linux-arm64 (the Pi) NOT yet built or tested

A `.riv` file renders inside an Avalonia control, in-process, on Avalonia's own SkiaSharp canvas, with a
state-machine number input driven from C# visibly changing the output. No browser, webview or HTTP server.

## How it works

- Rive publishes `rive-app/rive-sharp` (MIT, SkiaSharp-based, `Rive.RiveSharp` on NuGet, last 1.0.5-alpha, Dec 2022).
  Its C++ side (`native/RiveSharpInterop.cpp`) wraps `rive-cpp` and calls back into C# for path/paint/draw, where the
  C# side issues SkiaSharp calls. So the native lib does NOT link Skia; it just needs to match SkiaSharp 2.88.x on the C# side
  (Avalonia 11.3 ships SkiaSharp 2.88.9; rive-sharp's own csproj also pins 2.88.9).
- The NuGet package is unusable on Linux: it ships `rive.dll` for win10-x86/x64/arm/arm64 and wasm `rive.a` for Uno only. No `.so`.
  The `Rive` NuGet Avalonia player (11.0.0-preview3..8) is a managed wrapper over that same package, so it has the same gap.
- I built `librive.so` from the rive-sharp sources (pinned rive-cpp submodule `26dd69f`) with plain `clang++` on Linux:
  `-DWASM` flips the export macro away from `__declspec(dllexport)`; no source changes were needed.
  See `native/build.sh`. Build took 5m10s single-threaded (one clang++ invocation) on a 32-core x86-64 host; result 2.0 MB.
- C# sources from `RiveSharp/` (Scene, Renderer, RenderPath/Paint/Image, Factory, RiveAPI, 2D) are copied unmodified into `Baird/Rive/RiveSharp/`.
  `Baird/Rive/RivePlayerControl.cs` is the new Avalonia control (ICustomDrawOperation + `ISkiaSharpApiLeaseFeature`, advance+draw
  under a lock on the render thread, `RequestAnimationFrame` invalidation, `SetNumber/SetBool/FireTrigger`).
- Spike tab: `RiveSpikeViewModel` / `RiveSpikeControl` (rating.riv, input `rating` 0..5 via Left/Right/buttons).
- Native lib committed at `Baird/runtimes/linux-x64/native/librive.so`, copied next to the app by Baird.csproj
  (`DllImport("rive")` resolves `librive.so`). The `.riv` assets are in `assets/` and are copied to `assets/rive/`.

## Measured

- `dotnet test Baird.Tests`: 87 passed, 0 failed (includes 4 new Rive tests).
- `RivePlayerControlTests` (SKBitmap, driven with fixed dt, rating.riv 400x200):
  - non-blank render: PASS (the artboard is 200px wide, 40000 non-black px).
  - rating 0 vs rating 5 set from C#: 1185 pixels differ (`rating0.png` vs `rating5.png`: dim stars vs lit stars).
  - t=0 vs t=0.25s: 1534 pixels differ (animation advances).
  - Full Avalonia pipeline (`UseSkia()`, `UseHeadlessDrawing=false`, Window with `RiveSpikeControl`, `CaptureRenderedFrame`):
    7 Rive frames drawn through Avalonia's Skia canvas, 4793 pixels differ between rating 0 and 5 frames.
- Tests required changing `TestAppBuilder` to `UseSkia()` + `UseHeadlessDrawing = false` so custom Skia draw ops actually execute.
  All pre-existing tests still pass under it.
- Only `rating.riv` / `State Machine 1` / input `rating` was exercised.

## Not tested

- linux-arm64 / Raspberry Pi 5: no cross toolchain here; the C++ is plain C++17 with no x86-specific code, but it is unbuilt and unrun there.
  Needs a native build on the Pi (or an aarch64 sysroot) and a `runtimes/linux-arm64/native/librive.so` entry in the csproj.
- Any performance: CPU, frame time, memory, behaviour at 1080p/4K, Skia GPU vs software on the Pi, alongside mpv video. No numbers measured.
- `digital_music_player.riv` and `vehicles.riv` (the former embeds a font; text rendering path unverified),
  Rive features newer than Dec 2022 rive-cpp (data binding/ViewModels, scripting, layouts, newer file versions may fail to load).
- Bool/trigger inputs, pointer input (`Scene.PointerDown/Move/Up` exist but are not wired), images/meshes, clipping edge cases.
- Real window under X11/Wayland/framebuffer (headless only), thread safety under Avalonia's real render thread (guarded by a lock, not stress-tested).
- Windows/macOS builds of the native lib; CI build/packaging of `librive.so`.
- Input enumeration: the rive-sharp API has no way to list state machine or input names; they must be known from the Rive editor.
- `LoadStateMachine("")` default-selection behaviour; GC finalizer lifetime of `Scene` (native handle freed in finalizer).

## Risks / recommendation

- The runtime is frozen at a Dec 2022 rive-cpp; files authored with the current Rive editor may not load. Verify with real target assets first.
- Upgrade path would be rebuilding against newer rive-runtime with a new interop layer (not attempted; effort unknown beyond "the Skia renderer
  was removed from the current rive-runtime README in favour of its own GPU renderer", per search results).
- Recommend: keep this route if the design team can author Rive files compatible with the 2022 runtime; next step is an arm64 build on the Pi and a
  frame-time measurement. Lottie/Skottie remains the lower-risk fallback.

## Artefacts

- `rating0.png`, `rating5.png`: rendered by the test (SKBitmap).
- `assets/*.riv`: sample files (reused from the previous spike).
- `native/`: interop source, build script, rive-cpp MIT licence. Sources: https://github.com/rive-app/rive-sharp , https://nuget.org/packages/Rive.RiveSharp
