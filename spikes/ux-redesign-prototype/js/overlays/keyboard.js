// The on-screen keyboard: a digit row, QWERTY letters, then space / delete / voice. Arrow keys move; edges report out.
import { h } from '../ui.js';
import { icon } from '../icons.js';

const ROWS = [
  ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'],
  ['q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p'],
  ['a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', "'"],
  ['z', 'x', 'c', 'v', 'b', 'n', 'm', '-', '&', '.'],
  ['space', 'del', 'voice'],
];
const WIDE = { space: 5, del: 3, voice: 2 };

export function createKeyboard({ onKey, onExit }) {
  let r = 1, c = 0;
  const el = h('div', { class: 'kbd' });
  const keyEls = ROWS.map(row => row.map(k => {
    const label = k === 'space' ? 'Space' : k === 'del' ? null : k === 'voice' ? null : k.toUpperCase();
    const e = h('div', { class: `key${WIDE[k] ? ' wide' : ''}`, style: WIDE[k] ? { gridColumn: `span ${WIDE[k]}` } : null },
      k === 'del' ? icon('del', 30) : k === 'voice' ? icon('mic', 26) : null, label);
    return e;
  }));
  keyEls.forEach(row => el.append(...row));

  function draw(active) { keyEls.forEach((row, i) => row.forEach((e, j) => e.classList.toggle('focused', active && i === r && j === c))); }
  // map a column across rows of different widths by horizontal position (0..10)
  const xOf = (row, col) => ROWS[row].slice(0, col).reduce((a, k) => a + (WIDE[k] ?? 1), 0);
  const colAt = (row, x) => { let acc = 0; for (let j = 0; j < ROWS[row].length; j++) { acc += WIDE[ROWS[row][j]] ?? 1; if (x < acc) return j; } return ROWS[row].length - 1; };

  return {
    el,
    draw,
    onAction(a) {
      if (a === 'left') { if (c === 0) return onExit('left'); c -= 1; }
      else if (a === 'right') { if (c === ROWS[r].length - 1) return onExit('right'); c += 1; }
      else if (a === 'up') { if (r === 0) return onExit('up'); const x = xOf(r, c); r -= 1; c = colAt(r, x); }
      else if (a === 'down') { if (r === ROWS.length - 1) return onExit('down'); const x = xOf(r, c); r += 1; c = colAt(r, x); }
      else if (a === 'ok') { onKey(ROWS[r][c]); }
      else return false;
      draw(true);
      return true;
    },
  };
}
