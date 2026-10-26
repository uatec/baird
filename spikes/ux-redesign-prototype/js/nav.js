// The spatial model: the video is the ground floor, Home is the bottom page, pages stack above Home (or above the
// video), and one overlay can sit on top. Back retraces the journey.
//
//   top()        'home' | 'video' | Page | Overlay
//   play()       puts the video on screen and remembers the pages it was started from
//   back()       overlay → closes; page → pops; bare video → the pages it came from (or Home)

export function createNavigator() {
  let video = null;        // { kind: 'live'|'vod', ref }
  let atVideo = false;     // is the video the layer under the pages (true) or behind Home (false)?
  let pages = [];
  let overlay = null;
  let originPages = [];
  let place = 'home';      // the tab: 'home' | 'guide' | 'search' | 'settings'
  const listeners = [];
  const changed = () => listeners.forEach(fn => fn());

  const top = () => overlay ?? pages.at(-1) ?? (video && atVideo ? 'video' : place);

  return {
    state: () => ({ video, atVideo, pages: [...pages], overlay, originPages: [...originPages], place }),
    /** Switch tab. Only meaningful with nothing stacked above (the tab bar is only shown then). */
    setPlace(name) {
      if (place === name && !pages.length && !overlay && !atVideo) return;
      place = name; pages = []; overlay = null; atVideo = false; originPages = [];
      changed();
    },
    top,
    onChange(fn) { listeners.push(fn); },

    openPage(page) {
      overlay = null;
      pages.push(page);
      changed();
    },
    openOverlay(o) {
      overlay = o;
      changed();
    },
    closeOverlay() {
      if (!overlay) return;
      overlay = null;
      changed();
    },
    /** Replace the params of the top page (e.g. the guide remembering its focus). */
    updateTop(params) {
      const p = pages.at(-1);
      if (p) p.params = { ...p.params, ...params };
    },
    play(ref, kind, { keepOverlay = false } = {}) {
      if (pages.length) { originPages = pages; pages = []; }
      else if (!atVideo) originPages = [];
      if (!keepOverlay) overlay = null;
      video = { kind, ref };
      atVideo = true;
      changed();
    },
    stop() {
      video = null;
      atVideo = false;
      overlay = null;
      pages = [...originPages, ...pages];
      originPages = [];
      changed();
    },
    back() {
      if (overlay) overlay = null;
      else if (pages.length) pages.pop();
      else if (video && atVideo) { atVideo = false; pages = originPages; originPages = []; }
      else if (place !== 'home') place = 'home';
      else return;
      changed();
    },
    /** Return to the bare video (dismissing pages opened over it). */
    toVideo() {
      if (!video) return;
      overlay = null;
      if (atVideo) pages = [];
      else { atVideo = true; originPages = pages; pages = []; }
      changed();
    },
    home() {
      place = 'home';
      overlay = null;
      pages = [];
      originPages = [];
      atVideo = false;
      changed();
    },
  };
}
