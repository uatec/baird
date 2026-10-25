/**
 * Injected into the Channel 4 on-demand page when Baird draws the HUD itself (Baird's transparent window is on top and keeps
 * keyboard focus). The page then shows only the video: every other element is made invisible (Channel 4's controls, header and
 * the rest of the page), the pointer is hidden, and the video that plays fills the window.
 *
 * Channel 4's player DOM has not been measured yet, so this relies only on standard elements: the video with a source that is
 * playing (an advert or the programme) is marked every 250 ms and shown full screen. visibility, not display: the page stays laid
 * out, so the player keeps working. Subtitles drawn by the page (not by the video element) are hidden too.
 */
(function() {
    if (window.__bairdChannel4Style) return true;
    window.__bairdChannel4Style = true;

    const styleEl = document.createElement('style');
    styleEl.id = 'baird-page-style';
    styleEl.textContent = [
        '* { cursor: none !important; }',
        'html, body { background: #000 !important; overflow: hidden !important; }',
        'body * { visibility: hidden !important; }',
        'video.baird-c4-video { visibility: visible !important; position: fixed !important; left: 0 !important; top: 0 !important;',
        '  width: 100vw !important; height: 100vh !important; object-fit: contain !important; z-index: 2147483647 !important;',
        '  background: #000 !important; transform: none !important; }',
    ].join('\n');
    document.head.appendChild(styleEl);

    const mark = () => {
        const vids = [...document.querySelectorAll('video')];
        const live = vids.filter((v) => v.readyState > 0 && v.currentSrc && !v.ended);
        const shown = live.find((v) => !v.paused) || live[0] || null;
        vids.forEach((v) => v.classList.toggle('baird-c4-video', v === shown));
    };
    mark();
    window.__bairdChannel4StyleTimer = setInterval(mark, 250);
    console.log('[Baird] Channel 4 page style applied.');
    return true;
})();
