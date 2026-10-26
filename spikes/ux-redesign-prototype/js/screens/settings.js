// Settings: a calm list. ◂ ▸ change a value where there is one; OK opens the rows with a ›.
import { h, clamp } from '../ui.js';
import { icon } from '../icons.js';

export function create(ctx) {
  const st = ctx.state.settings;
  const cycle = (key, values, d) => { const i = values.indexOf(st[key]); st[key] = values[clamp(i + d, 0, values.length - 1)]; };
  const items = () => [
    { group: 'Picture and sound', label: 'Interface size', value: `${st.scale}%`, adjust: d => { st.scale = clamp(st.scale + d * 10, 80, 130); } },
    { label: 'Subtitles by default', value: st.subs ? 'On' : 'Off', adjust: () => { st.subs = !st.subs; } },
    { label: 'Play the next episode automatically', value: st.autoplay ? 'On' : 'Off', adjust: () => { st.autoplay = !st.autoplay; } },
    { label: 'Guide layout', value: st.guideLayout === 'rows' ? 'Channels down the side' : 'Channels across the top',
      adjust: d => { cycle('guideLayout', ['rows', 'columns'], d); ctx.resetPlace('guide'); } },
    { group: 'Sources', label: 'Freesat tuners (TvHeadend)', value: '2 tuners ready', dot: 'ok' },
    { label: 'Jellyfin', value: 'Connected', dot: 'ok' },
    { label: 'Netflix', value: 'Signed in', dot: 'ok' },
    { label: 'Disney+', value: 'Signed in', dot: 'ok' },
    { label: 'BBC iPlayer', value: 'Signed in', dot: 'ok' },
    { label: 'Seerr requests', value: 'Connected', dot: 'ok' },
    { group: 'TV and remote', label: 'TV control (HDMI-CEC)', value: 'Panasonic TV', chevron: true, run: () => ctx.toast('CEC settings open here') },
    { group: 'For troubleshooting', label: 'CEC debug', chevron: true, run: () => ctx.toast('CEC debug opens here') },
    { label: 'A/V debug', chevron: true, run: () => ctx.toast('A/V debug opens here') },
    { label: 'About Baird', value: 'v0.2 prototype' },
  ];
  let i = 0;
  let lit = true;
  const el = h('div', { class: 'page settings' });
  const list = h('div', { class: 'set-list' });
  const track = h('div', { class: 'set-track' });
  list.append(track);
  el.append(h('div', { class: 'settings-side' }, icon('gear', 120), h('h1', { text: 'Settings' })), list);

  function draw() {
    const its = items();
    track.replaceChildren(...its.flatMap((it, j) => [
      it.group ? h('div', { class: 'set-group', text: it.group }) : null,
      h('div', { class: `set-item${lit && j === i ? ' focused' : ''}${it.run || it.adjust ? '' : ' static'}` },
        it.dot ? h('span', { class: `dot ${it.dot}` }) : null,
        h('span', { class: 'set-label', text: it.label }),
        it.adjust ? h('span', { class: 'set-adjust' }, h('span', { class: 'arr', text: '‹' }), h('span', { class: 'set-value', text: it.value }), h('span', { class: 'arr', text: '›' }))
          : it.value ? h('span', { class: 'set-value', text: it.value }) : null,
        it.chevron ? h('span', { class: 'set-chev', text: '›' }) : null),
    ]).filter(Boolean));
    const focused = track.querySelector('.set-item.focused');
    track.style.transform = `translateY(${-Math.max(0, (focused?.offsetTop ?? 0) - 300)}px)`;
  }
  draw();
  return {
    el,
    onFocusOut() { lit = false; draw(); },
    onFocusIn() { lit = true; draw(); },
    onAction(a) {
      if (a === 'up' && i === 0) return false; // the shell lights the tab bar
      if (a === 'up' || a === 'down') { i = clamp(i + (a === 'up' ? -1 : 1), 0, items().length - 1); draw(); return true; }
      const it = items()[i];
      if ((a === 'left' || a === 'right') && it.adjust) { it.adjust(a === 'left' ? -1 : 1); draw(); return true; }
      if (a === 'ok') { (it.run ?? (it.adjust && (() => it.adjust(1))))?.(); draw(); return true; }
      return a === 'left' || a === 'right' || a === 'hold-ok';
    },
  };
}
