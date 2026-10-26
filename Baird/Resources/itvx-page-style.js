/**
 * Injected into the ITVX title page when Baird draws the HUD itself (Baird's transparent window is on top and keeps keyboard focus).
 * The page then shows only the video: every other element is made invisible (ITVX's controls, header and the rest of the page), the
 * page's scrollbar and the pointer are hidden, and the video that plays fills the window.
 *
 * Measured on the Pi (2026-10-26): the programme is a blob: video and adverts play in other video elements, so the video that is
 * playing (an advert or the programme) is marked every 250 ms and shown full screen. visibility, not display: the page stays laid
 * out, so the player keeps working.
 */
(function() {
    if (window.__bairdItvxStyle) return true;
    window.__bairdItvxStyle = true;

    const styleEl = document.createElement('style');
    styleEl.id = 'baird-page-style';
    styleEl.textContent = [
        '* { cursor: none !important; }',
        'html, body { background: #000 !important; overflow: hidden !important; scrollbar-width: none !important; }',
        '::-webkit-scrollbar { display: none !important; }',
        'body * { visibility: hidden !important; }',
        'video.baird-itvx-video { visibility: visible !important; position: fixed !important; left: 0 !important; top: 0 !important;',
        '  width: 100vw !important; height: 100vh !important; object-fit: contain !important; z-index: 2147483647 !important;',
        '  background: #000 !important; transform: none !important; }',
    ].join('\n');
    document.head.appendChild(styleEl);

    const mark = () => {
        const vids = [...document.querySelectorAll('video')];
        const live = vids.filter((v) => v.readyState > 0 && v.currentSrc && !v.ended);
        const shown = live.find((v) => !v.paused) || live.find((v) => v.currentSrc.startsWith('blob:')) || live[0] || null;
        vids.forEach((v) => v.classList.toggle('baird-itvx-video', v === shown));
    };
    mark();
    window.__bairdItvxStyleTimer = setInterval(mark, 250);
    console.log('[Baird] ITVX page style applied.');
    return true;
})();
