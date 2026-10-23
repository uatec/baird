/**
 * Injected into the Netflix watch page when Baird draws the HUD itself (Baird's transparent window is on top and keeps
 * keyboard focus). The page only shows the video: this hides Netflix's own player UI and the mouse pointer.
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
    `;
    console.log("[Baird] Netflix page style applied.");
    return true;
})();
