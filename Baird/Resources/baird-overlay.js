/**
 * Baird 10-Foot HUD Proof of Concept for Netflix
 * Injected script that:
 * 1. Suppresses native Netflix mouse-driven player controls
 * 2. Injects Baird's TV-styled HUD (progress bar, timestamps, 10-foot remote hints)
 * 3. Binds D-pad remote controls (Arrow keys, Enter, Space, Escape) directly to the HTML5 video
 * 4. Implements Baird's HUD auto-hide policy (auto-hides during playback, stays pinned when paused)
 */
(function() {
    if (window.__baird_poc_installed) {
        console.log("[Baird PoC] Already installed. Re-initializing...");
        window.__baird_poc_cleanup && window.__baird_poc_cleanup();
    }
    window.__baird_poc_installed = true;

    console.log("[Baird PoC] Initializing Baird Web HUD...");

    // 1. Inject Styles to hide Netflix UI and style Baird HUD
    const styleId = "baird-poc-styles";
    let styleEl = document.getElementById(styleId);
    if (!styleEl) {
        styleEl = document.createElement("style");
        styleEl.id = styleId;
        document.head.appendChild(styleEl);
    }
    styleEl.textContent = `
        /* Hide native Netflix overlays & cursor */
        .watch-video--bottom-controls-container,
        .watch-video--top-controls-container,
        .watch-video--player-view-container > div:not(#baird-hud-container),
        .PlayerControls--control-element,
        .evidence-overlay {
            display: none !important;
            opacity: 0 !important;
            pointer-events: none !important;
        }

        /* Baird Root HUD Container */
        #baird-hud-container {
            position: fixed !important;
            inset: 0 !important;
            z-index: 2147483647 !important;
            pointer-events: none !important;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
            transition: opacity 0.3s cubic-bezier(0.16, 1, 0.3, 1);
            user-select: none;
        }

        #baird-hud-container.hud-hidden {
            opacity: 0 !important;
        }

        /* Top Header */
        .baird-top-bar {
            padding: 36px 48px;
            background: linear-gradient(180deg, rgba(0,0,0,0.85) 0%, rgba(0,0,0,0.4) 60%, rgba(0,0,0,0) 100%);
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 6px;
        }

        .baird-title-row {
            display: flex;
            align-items: center;
            gap: 12px;
        }

        .baird-badge-drm {
            background: #E50914;
            color: white;
            font-weight: 800;
            font-size: 13px;
            padding: 2px 8px;
            border-radius: 4px;
            letter-spacing: 0.5px;
        }

        .baird-title {
            color: #FFFFFF;
            font-size: 26px;
            font-weight: 700;
            text-shadow: 0 2px 8px rgba(0,0,0,0.8);
        }

        .baird-status {
            color: #AAAAAA;
            font-size: 14px;
            font-weight: 500;
            letter-spacing: 0.3px;
        }

        /* Bottom Control Bar */
        .baird-bottom-bar {
            padding: 48px 48px 36px 48px;
            background: linear-gradient(0deg, rgba(0,0,0,0.92) 0%, rgba(0,0,0,0.6) 50%, rgba(0,0,0,0) 100%);
            display: flex;
            flex-direction: column;
            gap: 14px;
        }

        .baird-timeline-row {
            display: flex;
            align-items: center;
            gap: 16px;
        }

        .baird-time {
            color: #FFFFFF;
            font-size: 16px;
            font-weight: 600;
            font-variant-numeric: tabular-nums;
            min-width: 65px;
        }

        .baird-time-remaining {
            color: #CCCCCC;
            font-size: 16px;
            font-weight: 500;
            font-variant-numeric: tabular-nums;
            min-width: 65px;
            text-align: right;
        }

        .baird-progress-track {
            flex: 1;
            height: 8px;
            background: rgba(255, 255, 255, 0.25);
            border-radius: 4px;
            overflow: hidden;
            position: relative;
        }

        .baird-progress-fill {
            height: 100%;
            background: #E50914;
            width: 0%;
            border-radius: 4px;
            transition: width 0.15s linear;
        }

        .baird-info-row {
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        .baird-finishing-at {
            color: #888888;
            font-size: 13px;
        }

        /* 10-Foot Remote Navigation Hints */
        .baird-remote-hints {
            align-self: center;
            display: flex;
            align-items: center;
            gap: 20px;
            background: rgba(15, 23, 42, 0.75);
            border: 1px solid rgba(255, 255, 255, 0.15);
            border-radius: 20px;
            padding: 6px 18px;
            margin-top: 6px;
            backdrop-filter: blur(8px);
        }

        .baird-hint {
            display: flex;
            align-items: center;
            gap: 6px;
            color: #DDDDDD;
            font-size: 13px;
            font-weight: 500;
        }

        .baird-key {
            background: rgba(255, 255, 255, 0.18);
            border: 1px solid rgba(255, 255, 255, 0.3);
            border-radius: 4px;
            padding: 2px 6px;
            font-size: 11px;
            font-weight: 700;
            color: #FFFFFF;
        }
    `;

    // 2. Locate the HTML5 Video element
    function getVideo() {
        return document.querySelector("video");
    }

    // 3. Create Baird HUD DOM Structure
    let hud = document.getElementById("baird-hud-container");
    if (!hud) {
        hud = document.createElement("div");
        hud.id = "baird-hud-container";
        hud.innerHTML = `
            <div class="baird-top-bar">
                <div class="baird-title-row">
                    <span class="baird-badge-drm">WIDEVINE DRM</span>
                    <span class="baird-title" id="baird-title-text">Baird Media Platform</span>
                </div>
                <div class="baird-status" id="baird-status-text">Playing</div>
            </div>

            <div class="baird-bottom-bar">
                <div class="baird-timeline-row">
                    <span class="baird-time" id="baird-current-time">00:00</span>
                    <div class="baird-progress-track">
                        <div class="baird-progress-fill" id="baird-progress-fill"></div>
                    </div>
                    <span class="baird-time-remaining" id="baird-remaining-time">-00:00</span>
                </div>
                <div class="baird-info-row">
                    <span class="baird-finishing-at" id="baird-finishing-at"></span>
                    <div class="baird-remote-hints">
                        <div class="baird-hint"><span class="baird-key">◄ / ►</span> Seek 10s</div>
                        <div class="baird-hint"><span class="baird-key">ENTER</span> Play / Pause</div>
                        <div class="baird-hint"><span class="baird-key">ESC</span> Back</div>
                    </div>
                    <span></span>
                </div>
            </div>
        `;
        document.body.appendChild(hud);
    }

    // HUD Elements
    const titleEl = document.getElementById("baird-title-text");
    const statusEl = document.getElementById("baird-status-text");
    const curTimeEl = document.getElementById("baird-current-time");
    const remTimeEl = document.getElementById("baird-remaining-time");
    const progressFill = document.getElementById("baird-progress-fill");
    const finishAtEl = document.getElementById("baird-finishing-at");

    // Format seconds into HH:MM:SS or MM:SS
    function formatTime(seconds) {
        if (!seconds || isNaN(seconds) || seconds < 0) return "00:00";
        const total = Math.floor(seconds);
        const hrs = Math.floor(total / 3600);
        const mins = Math.floor((total % 3600) / 60);
        const secs = total % 60;
        if (hrs > 0) {
            return `${hrs}:${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
        }
        return `${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
    }

    // Auto-hide Timer management
    let hideTimer = null;
    function showHud() {
        hud.classList.remove("hud-hidden");
        clearTimeout(hideTimer);
        const v = getVideo();
        // Baird HUD rule: only auto-hide if playing. Keep glued if paused or seeking.
        if (v && !v.paused) {
            hideTimer = setTimeout(() => {
                hud.classList.add("hud-hidden");
            }, 3000);
        }
    }

    // Title sync
    function syncTitle() {
        const titleFromDoc = document.title ? document.title.replace(/ - Netflix$/, '') : '';
        const titleFromNetflix = document.querySelector('[data-uia="video-title"]')?.textContent;
        const finalTitle = titleFromNetflix || titleFromDoc || "Netflix Stream";
        if (titleEl.textContent !== finalTitle) {
            titleEl.textContent = finalTitle;
        }
    }

    // Update HUD values from video state
    function updateHud() {
        const v = getVideo();
        if (!v) return;

        syncTitle();

        const cur = v.currentTime || 0;
        const dur = v.duration || 0;
        const progress = dur > 0 ? (cur / dur) * 100 : 0;

        curTimeEl.textContent = formatTime(cur);
        remTimeEl.textContent = "-" + formatTime(Math.max(0, dur - cur));
        progressFill.style.width = `${progress.toFixed(2)}%`;

        if (dur > 0) {
            const endsAt = new Date(Date.now() + (dur - cur) * 1000);
            finishAtEl.textContent = `Ends at ${endsAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`;
        }

        if (v.paused) {
            statusEl.textContent = "Paused";
            statusEl.style.color = "#FFD700";
        } else if (v.seeking) {
            statusEl.textContent = "Seeking...";
            statusEl.style.color = "#38BDF8";
        } else {
            statusEl.textContent = "Playing";
            statusEl.style.color = "#4ADE80";
        }
    }

    // 4. Remote Control / Keyboard Event Interception (D-Pad)
    function onKeyDown(e) {
        const v = getVideo();
        if (!v) return;

        showHud();

        switch(e.key) {
            case "ArrowLeft": // D-pad Left: Seek back 10s
                e.preventDefault();
                e.stopPropagation();
                v.currentTime = Math.max(0, v.currentTime - 10);
                updateHud();
                break;

            case "ArrowRight": // D-pad Right: Seek forward 10s
                e.preventDefault();
                e.stopPropagation();
                v.currentTime = Math.min(v.duration || Infinity, v.currentTime + 10);
                updateHud();
                break;

            case "Enter":
            case " ": // D-pad Select / OK / Space: Play/Pause toggle
                e.preventDefault();
                e.stopPropagation();
                if (v.paused) {
                    v.play();
                } else {
                    v.pause();
                }
                updateHud();
                showHud();
                break;

            case "Escape":
            case "Backspace": // D-pad Back
                console.log("[Baird PoC] Back requested via Remote.");
                // If in fullscreen, exit fullscreen or go back in history
                if (document.fullscreenElement) {
                    document.exitFullscreen();
                } else {
                    window.history.back();
                }
                break;
        }
    }

    // Hook listeners
    window.addEventListener("keydown", onKeyDown, true);

    const checkInterval = setInterval(() => {
        const v = getVideo();
        if (v) {
            v.removeEventListener("timeupdate", updateHud);
            v.removeEventListener("play", showHud);
            v.removeEventListener("pause", showHud);
            v.removeEventListener("seeking", updateHud);
            v.removeEventListener("seeked", updateHud);

            v.addEventListener("timeupdate", updateHud);
            v.addEventListener("play", () => { updateHud(); showHud(); });
            v.addEventListener("pause", () => { updateHud(); showHud(); });
            v.addEventListener("seeking", () => { updateHud(); showHud(); });
            v.addEventListener("seeked", () => { updateHud(); showHud(); });

            updateHud();
            showHud();
        }
    }, 1000);

    // Cleanup hook for reloads
    window.__baird_poc_cleanup = function() {
        window.removeEventListener("keydown", onKeyDown, true);
        clearInterval(checkInterval);
        const el = document.getElementById("baird-hud-container");
        if (el) el.remove();
        const s = document.getElementById("baird-poc-styles");
        if (s) s.remove();
    };

    console.log("[Baird PoC] Baird 10-Foot HUD successfully mounted!");
})();
