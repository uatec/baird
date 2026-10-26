// Small DOM helpers shared by every screen.

export function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props ?? {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'style' && typeof v === 'object') for (const [sk, sv] of Object.entries(v)) { if (sk.startsWith('--')) el.style.setProperty(sk, sv); else el.style[sk] = sv; }
    else if (k === 'text') el.textContent = v;
    else if (k === 'html') el.innerHTML = v;
    else if (k.startsWith('data-')) el.setAttribute(k, v);
    else if (k in el) el[k] = v;
    else el.setAttribute(k, v);
  }
  for (const c of children.flat()) if (c !== null && c !== undefined && c !== false) el.append(c instanceof Node ? c : String(c));
  return el;
}

/** Where an element sits on the 1920×1080 stage (the stage is scaled to the window). */
export function stageRect(el) {
  const stage = document.getElementById('stage');
  const s = stage.getBoundingClientRect(), r = el.getBoundingClientRect();
  const k = s.width / 1920;
  return { left: (r.left - s.left) / k, top: (r.top - s.top) / k, width: r.width / k, height: r.height / k };
}

const hue = key => [...String(key ?? 'x')].reduce((a, c) => (a * 31 + c.charCodeAt(0)) % 360, 7);

/**
 * Artwork with the agreed fallback order: the image → a branded channel card (wordmark on the channel colour)
 * → a typographic card (the title set large on a tint). Never a flat grey box.
 * opts: { title, channel: { name, colour } }
 */
export function art(src, key, cls = '', opts = {}) {
  if (src) return h('img', { src, class: cls, decoding: 'async', alt: '' });
  const { title, channel } = opts;
  if (channel) {
    return h('div', { class: `art-fallback brand ${cls}`, style: { '--c': channel.colour } },
      channel.name ? h('span', { class: 'fb-mark', text: channel.name }) : null, title ? h('span', { class: 'fb-title', text: title }) : null);
  }
  return h('div', { class: `art-fallback ${title ? 'type' : ''} ${cls}`, style: { '--h': hue(key ?? title) } }, title ? h('span', { class: 'fb-title', text: title }) : null);
}

export const hhmm = ms => new Date(ms).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
export const minsLeft = (endMs, nowMs) => Math.max(0, Math.round((endMs - nowMs) / 60_000));
export const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

/** Card element for Home rows, search results and episode lists. */
export function cardEl(c, shape = 'wide') {
  const fb = c.type === 'channel' ? { title: c.programme?.noInfo ? null : c.sublabel, channel: { name: '', colour: c.colour } } : { title: c.label };
  const artEl = h('div', { class: 'card-art' }, art(c.art, c.titleId ?? c.channelId, '', fb));
  if (c.type === 'channel') {
    artEl.append(h('div', { class: 'card-shade' }), h('span', { class: 'card-badge pill live', text: 'Live' }),
      h('div', { class: 'card-on-art', text: c.label }));
  }
  if (c.badge === 'get') artEl.append(h('span', { class: 'card-corner pill get', text: '⤓ Request' }));
  if (c.badge === 'partial') artEl.append(h('span', { class: 'card-corner pill', text: 'Partly available' }));
  if (c.scope) artEl.append(h('span', { class: 'card-scope pill', text: c.scope }));
  if (c.badge === 'requested') artEl.append(h('div', { class: 'card-corner ring', style: { '--p': Math.round(c.progress * 100) } }, h('span', { text: `${Math.round(c.progress * 100)}%` })));
  if (c.progress != null && c.badge !== 'requested' && (c.progress > 0 || c.type === 'channel') && !c.programme?.noInfo) {
    artEl.append(h('div', { class: 'card-progress' }, h('i', { style: { width: `${Math.round(c.progress * 100)}%` } })));
  }
  const label = c.type === 'channel' ? c.sublabel : c.label;
  const sub = c.type === 'channel' ? c.timeText ?? '' : c.sublabel;
  return h('div', { class: `card card--${shape}` }, artEl,
    h('div', { class: 'card-text' }, h('div', { class: 'card-label', text: label }), sub ? h('div', { class: 'card-sub', text: sub }) : null));
}

/** replaceChildren that skips null/false children (replaceChildren would render them as the text "null"). */
export function fill(el, ...kids) { el.replaceChildren(...kids.flat().filter(k => k !== null && k !== undefined && k !== false)); }

/**
 * Keep the lit element fully on screen: move `scroller` vertically (a translateY it owns) just enough that `el`,
 * including the room its focus lift and ring take, sits inside the safe area. Every screen uses this.
 */
export function keepInView(scroller, el, { top = 64, bottom = 1030, lift = 0.07 } = {}) {
  if (!scroller || !el) return;
  const r = stageRect(el);
  const grow = r.height * lift + 8;
  const y = parseFloat(scroller.dataset.y ?? '0');
  let next = y;
  if (r.top - grow < top) next = y + (top - (r.top - grow));
  else if (r.top + r.height + grow > bottom) next = y - (r.top + r.height + grow - bottom);
  if (next !== y) {
    scroller.dataset.y = String(next);
    scroller.style.transform = `translateY(${next}px)`;
  }
}
