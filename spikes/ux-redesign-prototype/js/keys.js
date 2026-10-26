// Remote/keyboard input. The G20S Pro remote is a USB HID keyboard, so its buttons arrive as ordinary key events;
// each action accepts the keyboard stand-in and the codes the remote is likely to send.

const KEY_ACTIONS = {
  ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right',
  Enter: 'ok', Select: 'ok',
  Escape: 'back', Backspace: 'back', BrowserBack: 'back', GoBack: 'back',
  h: 'home', H: 'home', BrowserHome: 'home', HomePage: 'home',
  g: 'guide', G: 'guide', ContextMenu: 'guide', Guide: 'guide', Menu: 'guide',
  ' ': 'playpause', MediaPlayPause: 'playpause', MediaPlay: 'playpause', MediaPause: 'playpause',
  '[': 'prev', MediaTrackPrevious: 'prev', MediaRewind: 'prev',
  ']': 'next', MediaTrackNext: 'next', MediaFastForward: 'next',
  PageUp: 'pageup', ChannelUp: 'pageup',
  PageDown: 'pagedown', ChannelDown: 'pagedown',
  Delete: 'del',
  v: 'voice', V: 'voice', MicrophoneToggle: 'voice', LaunchAssistant: 'voice',
  f: 'debug-ff', F: 'debug-ff',
  k: 'debug-keys', K: 'debug-keys',
  r: 'debug-reminder', R: 'debug-reminder',
  '?': 'help',
};

export function mapKey(e) {
  if (/^[0-9]$/.test(e.key)) return `digit:${e.key}`;
  return KEY_ACTIONS[e.key] ?? KEY_ACTIONS[e.code] ?? null;
}

/** Turns OK down/up into 'ok' (short press, on release) or 'hold-ok' (fires once at the threshold). */
export function createHoldDetector({ holdMs = 500, now = () => performance.now() } = {}) {
  let downAt = null;
  let fired = false;
  return {
    down() {
      if (downAt !== null) return; // key repeat
      downAt = now();
      fired = false;
    },
    tick() {
      if (downAt === null || fired) return null;
      if (now() - downAt >= holdMs) { fired = true; return 'hold-ok'; }
      return null;
    },
    up() {
      if (downAt === null) return null;
      const result = fired ? null : (this.tick() ?? 'ok');
      downAt = null;
      return result === 'hold-ok' ? 'hold-ok' : result;
    },
  };
}

/** Typed channel numbers: '1 0 4' → Channel 4. */
export function numberEntry(channels) {
  let text = '';
  const state = () => {
    if (!text) return { text, match: null, error: null };
    const match = channels.find(c => String(c.number) === text) ?? null;
    const anyPrefix = channels.some(c => String(c.number).startsWith(text));
    const error = !match && !anyPrefix ? `No channel ${text}` : null;
    return { text, match, error };
  };
  return {
    push(d) { if (text.length < 4) text += String(d); return state(); },
    del() { text = text.slice(0, -1); return state(); },
    clear() { text = ''; return state(); },
    state,
  };
}
