/**
 * Injected into the Netflix watch page when Baird's player has the controls (Baird's transparent window is on top and
 * keeps keyboard focus). The page only shows the video and its subtitles: this hides Netflix's own UI and the pointer.
 */
(function() {
    const styleId = "baird-page-style";
    let styleEl = document.getElementById(styleId);
    if (!styleEl) {
        styleEl = document.createElement("style");
        styleEl.id = styleId;
        document.head.appendChild(styleEl);
    }
    styleEl.textContent = `
        .watch-video--bottom-controls-container,
        .watch-video--top-controls-container,
        .watch-video--back-container,
        .watch-video--evidence-overlay-container,
        .watch-video--advisories-container,
        .watch-video--player-view-container > div,
        .PlayerControls--control-element,
        .evidence-overlay {
            display: none !important;
            opacity: 0 !important;
            pointer-events: none !important;
        }
        * { cursor: none !important; }
        /* Anything else Netflix draws (e.g. the report flag at the top right) stays hidden too: only the video and the
           subtitles are visible. A visible child shows even inside a hidden parent. */
        body * { visibility: hidden !important; }
        video, .player-timedtext, .player-timedtext * { visibility: visible !important; }
    `;
    console.log("[Baird] Netflix page style applied.");
    return true;
})();
