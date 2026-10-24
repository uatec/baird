/**
 * Injected into the Disney+ player page when Baird draws the HUD itself (Baird's transparent window is on top and keeps
 * keyboard focus). The page then shows only the video and the subtitles: this hides Disney+'s own player controls and the
 * mouse pointer.
 *
 * The controls live in web components with shadow DOM (disney-web-player-ui > main-app-controls-overlay), so page CSS cannot
 * reach them: their host element is made transparent directly (opacity, not display: it stays laid out, so a prompt inside it
 * can still be found and clicked by Baird), and again every 150 ms because the player removes and re-creates the overlay when it wakes. Subtitles are not part of that overlay (measured on the Pi). The loop ends with the document.
 */
(function() {
    if (window.__bairdDisneyStyle) return true;
    window.__bairdDisneyStyle = true;

    const hideControls = (root) => {
        root.querySelectorAll('*').forEach((e) => {
            if (e.tagName === 'MAIN-APP-CONTROLS-OVERLAY') e.style.setProperty('opacity', '0', 'important');
            if (e.shadowRoot) hideControls(e.shadowRoot);
        });
    };

    const styleEl = document.createElement('style');
    styleEl.id = 'baird-page-style';
    styleEl.textContent = '* { cursor: none !important; }';
    document.head.appendChild(styleEl);

    hideControls(document);
    window.__bairdDisneyStyleTimer = setInterval(() => hideControls(document), 150);
    console.log('[Baird] Disney+ page style applied.');
    return true;
})();
