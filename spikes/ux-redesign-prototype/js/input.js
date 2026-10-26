// Browser wiring for remote input: keydown/keyup → actions, hold-OK detection, the key inspector, and a history
// trap so the browser's own Back (BrowserBack/Backspace) can never leave the page.
import { mapKey, createHoldDetector } from './keys.js';

export function installInput(onAction) {
  const hold = createHoldDetector({ holdMs: 500 });
  const inspector = document.getElementById('keyinspector');
  let holdTimer = null;

  const show = (e, action) => {
    if (inspector.hidden) return;
    inspector.textContent = `key:  ${JSON.stringify(e.key)}\ncode: ${JSON.stringify(e.code)}\n→     ${action ?? '(unmapped)'}`;
  };

  window.addEventListener('keydown', e => {
    const action = mapKey(e);
    show(e, action);
    if (action || e.key === 'Backspace') e.preventDefault();
    if (!action) return;
    if (action === 'debug-keys') { if (!e.repeat) { inspector.hidden = !inspector.hidden; show(e, action); } return; }
    if (action === 'ok') {
      if (e.repeat) return;
      hold.down();
      clearTimeout(holdTimer);
      holdTimer = setTimeout(() => { if (hold.tick() === 'hold-ok') onAction('hold-ok'); }, 510);
      return;
    }
    onAction(action, { repeat: e.repeat });
  });

  window.addEventListener('keyup', e => {
    if (mapKey(e) !== 'ok') return;
    clearTimeout(holdTimer);
    const r = hold.up();
    if (r) onAction(r);
  });

  history.pushState({ trap: true }, '');
  window.addEventListener('popstate', () => { history.pushState({ trap: true }, ''); onAction('back'); });
}

/** Scales the 1920×1080 stage to fit the window. */
export function installStageScaling(stage) {
  const fit = () => {
    const s = Math.min(window.innerWidth / 1920, window.innerHeight / 1080);
    stage.style.transform = `scale(${s})`;
  };
  window.addEventListener('resize', fit);
  fit();
}
