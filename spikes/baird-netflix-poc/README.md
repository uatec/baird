# Baird Netflix 10-Foot HUD Proof of Concept

This Proof of Concept demonstrates:
1. **Suppression of native Netflix controls:** Hiding all mouse-driven Netflix buttons, timelines, and overlays so only the pure hardware-accelerated video stream is shown.
2. **Injection of Baird's 10-Foot TV HUD:** Rendering Baird's TV-styled top bar, bottom timeline, progress bar, time remaining, and finishing time directly on top of the Widevine DRM video.
3. **D-Pad / Remote Navigation:** Capturing TV remote keystrokes (`ArrowLeft`/`Right` to seek, `Enter`/`Space` to play/pause, `Esc` for back).
4. **Baird HUD Auto-Hide Policy:** Auto-hiding after 3 seconds during active playback, but remaining visible when paused or seeking.

---

## How to Test on Manjaro

### Quickest Method: Developer Tools Console

1. Open your browser on Manjaro (Firefox, Chrome, etc.).
2. Navigate to your Netflix video:
   ```
   https://www.netflix.com/watch/80107105
   ```
3. Open Developer Tools: press **`F12`** or **`Ctrl + Shift + I`**, then click the **Console** tab.
4. Open [baird-overlay.js](file:///home/uatec/.paseo/worktrees/0rojp50q/silly-newt/spikes/baird-netflix-poc/baird-overlay.js), copy the entire file content, paste it into the Console, and press **Enter**.
5. Switch the browser to fullscreen (`F11`).

### What to Verify
* **Clean Video Plane:** Notice that Netflix's default desktop player controls, hover gradients, and cursor overlays are completely gone.
* **Baird HUD:** The Baird top title bar, Widevine DRM badge, and bottom progress bar appear.
* **Remote Control:**
  * Press **`ArrowLeft`** / **`ArrowRight`**: Skips 10s backward/forward, updating time and progress bar immediately.
  * Press **`Enter`** or **`Space`**: Toggles Play/Pause. Notice the HUD stays pinned while paused.
  * Resume playback: The HUD automatically fades out after 3 seconds.
