# Spike: Lottie in Avalonia

Packages: `Avalonia.Labs.Lottie` 11.3.1 (pulls `SkiaSharp.Skottie` 2.88.8), Avalonia 11.3.11, .NET 9.
Spike tab: `LottieSpikeControl` / `LottieSpikeViewModel`. It should sit behind a debug flag (not done here).

## Result
Lottie renders in Avalonia via Skottie. All four sample animations parse and draw non-blank pixels, and frames at different progress values differ (measured in headless Skia render-to-bitmap tests, x64 Linux).

## Root cause of the blank viewport
`Baird.csproj` had no `<AvaloniaResource Include="Assets/**" />`, so `Assets/Lottie/*.json` were never embedded in `Baird.dll`. `AssetLoader.Exists("avares://Baird/Assets/Lottie/...")` returned false for all four files, and the `Lottie` control shows nothing and raises no error when the path cannot be loaded. Fixed by adding the `AvaloniaResource` item. Before the fix the tests reported "asset not embedded" for all 4 files; after it, all 4 parse.

## Load status
The `Lottie` control exposes no load result. The spike now runs `LottieProbe.Probe(path)` (AssetLoader + `Animation.TryParse`) on every selection and shows `Load: Loaded OK - WxH, duration @ fps` (green) or `Load: FAILED - reason` (red) in the viewport. Any future failure is therefore visible.

## Measured: render tests (`Baird.Tests/Controls/LottieRenderTests.cs`)
Test host switched to `UseSkia()` + `UseHeadlessDrawing = false` so frames are really rasterised (this affects all tests in the project; the full suite still passes, 97/97).
Each animation is rendered at 200x200, paused, seeked to progress 0.15 and 0.6, and captured with `CaptureRenderedFrame()`. Pixels differing from the corner (background) pixel, and pixels differing between the two frames:

| asset | non-bg px @0.15 | non-bg px @0.6 | differing px between frames |
|---|---|---|---|
| audio-bars.json | 146 | 1964 | 2087 |
| wave-loader.json | 1684 | 1686 | 2493 |
| play-pause.json | 4768 | 6345 | 2309 |
| loading-ring.json | 1207 | 1750 | 2538 |

No asset needed replacing; all four are the originals from the first pass.

## Observations
- `loading-ring.json` contains an opaque light-grey (#f2f2f2) solid layer, so it draws a full-bleed grey rectangle rather than a transparent spinner. It would need that layer removed or masked before use as an overlay.
- `audio-bars.json` is nearly empty at progress 0.15 (146 px) by design of the animation.
- The `Lottie` control API: `Path`, `Stretch`, `RepeatCount`, `PlayBackRate`, `AutoPlay`, `Start/Stop/Pause/Resume`, `SeekToProgress`, `SeekToFrame`, `AnimationCompleted`, `AnimationCompletedRepetition`. In the headless test `SeekToProgress` only produced a frame after the animation had been started (with `AutoPlay=false` and no `Start()` the output was blank).
- The control needs a `Lottie(IServiceProvider)` or `Lottie(Uri)` constructor; in code (not XAML) use `new Lottie(new Uri("avares://Baird/"))`.

## NOT tested
- Raspberry Pi 5 (or any ARM device): rendering, CPU/GPU load, frame rate, memory. Whether Skottie is GPU-accelerated there is unknown. Earlier README claims about GPU/OpenGL/Vulkan and native binaries on ARM were unmeasured and have been removed.
- Real window / framebuffer (LinuxFramebuffer, labwc/Wayland) output; only headless Skia bitmaps were checked.
- Playback timing, speed and loop behaviour in a live window; the AnimationCompleted events.
- Interaction with the mpv video overlay on the Pi.
