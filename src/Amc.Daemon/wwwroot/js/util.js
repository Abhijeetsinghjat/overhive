// DOM + formatting helpers. Untrusted text only ever reaches the DOM through
// textContent / attribute setters — never innerHTML.

const PROPS = new Set(['value', 'checked', 'disabled', 'selected', 'hidden', 'indeterminate', 'tabIndex', 'readOnly']);

function setProps(el, props) {
  for (const k in props) {
    const v = props[k];
    if (v == null || v === false) continue;
    if (k === 'class') el.setAttribute('class', v);
    else if (k === 'text') el.textContent = v;
    else if (k === 'style') Object.assign(el.style, v);
    else if (k === 'dataset') Object.assign(el.dataset, v);
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else if (PROPS.has(k)) el[k] = v;
    else el.setAttribute(k, v === true ? '' : String(v));
  }
}

function append(el, kids) {
  for (const c of kids) {
    if (c == null || c === false || c === true) continue;
    if (Array.isArray(c)) append(el, c);
    else if (c instanceof Node) el.appendChild(c);
    else el.appendChild(document.createTextNode(String(c)));
  }
}

/** Create an HTML element. Children may be strings (inserted as text), nodes, arrays or null. */
export function h(tag, props, ...kids) {
  const el = document.createElement(tag);
  if (props) setProps(el, props);
  append(el, kids);
  return el;
}

const SVGNS = 'http://www.w3.org/2000/svg';
export function s(tag, props, ...kids) {
  const el = document.createElementNS(SVGNS, tag);
  if (props) setProps(el, props);
  append(el, kids);
  return el;
}

/** Replace all children. */
export function mount(el, ...kids) {
  el.replaceChildren();
  append(el, kids);
  return el;
}

/**
 * Keyed list reconciliation: reuses DOM nodes by key and only calls build() when an
 * item's signature changed. build(item) returns a fresh node.
 */
export function keyed(container, items, keyOf, sigOf, build) {
  const old = container._keyed || new Map();
  const next = new Map();
  let cursor = container.firstChild;
  for (const item of items) {
    const k = keyOf(item);
    const sig = sigOf(item);
    let node = old.get(k);
    if (!node || node._sig !== sig) {
      const fresh = build(item);
      fresh._sig = sig;
      if (node && node.parentNode === container) {
        if (node === cursor) cursor = fresh;
        node.replaceWith(fresh);
      }
      node = fresh;
    }
    next.set(k, node);
    if (node !== cursor) container.insertBefore(node, cursor);
    else cursor = cursor.nextSibling;
  }
  // Everything from the cursor on is either an unused keyed node or a stray (e.g. an empty-state placeholder).
  while (cursor) { const n = cursor.nextSibling; cursor.remove(); cursor = n; }
  container._keyed = next;
}

// ---- formatting -------------------------------------------------------------

/** Normalises enum strings from the API: "InProgress", "in_progress" → "inprogress". */
export const norm = (v) => (v == null ? '' : String(v).toLowerCase().replace(/[_\-\s]/g, ''));

export const ms = (ts) => (ts ? Date.parse(ts) : NaN);

export function ago(ts, now = Date.now()) {
  const t = typeof ts === 'number' ? ts : ms(ts);
  if (!isFinite(t)) return '—';
  let d = Math.max(0, (now - t) / 1000);
  if (d < 1) return 'now';
  if (d < 60) return `${Math.floor(d)}s`;
  d /= 60; if (d < 60) return `${Math.floor(d)}m`;
  d /= 60; if (d < 48) return `${Math.floor(d)}h`;
  return `${Math.floor(d / 24)}d`;
}

export function duration(msv) {
  if (msv == null || !isFinite(msv)) return '—';
  if (msv < 1000) return `${Math.round(msv)} ms`;
  let sec = Math.round(msv / 1000);
  if (sec < 60) return `${sec}s`;
  const m = Math.floor(sec / 60); sec %= 60;
  if (m < 60) return `${m}m ${sec}s`;
  const hh = Math.floor(m / 60);
  return `${hh}h ${m % 60}m`;
}

const pad = (n, w = 2) => String(n).padStart(w, '0');
export function clock(ts) {
  const d = new Date(typeof ts === 'number' ? ts : ms(ts));
  if (isNaN(d)) return '—';
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}
export function clockMs(ts) {
  const d = new Date(ms(ts));
  if (isNaN(d)) return '—';
  return `${clock(ts)}.${pad(d.getMilliseconds(), 3)}`;
}
export function dateTime(ts) {
  const d = new Date(ms(ts));
  if (isNaN(d)) return '—';
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${clock(ts)}`;
}

export function bytes(n) {
  if (n == null || !isFinite(n)) return '—';
  const u = ['B', 'KiB', 'MiB', 'GiB'];
  let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return `${n.toFixed(i ? 1 : 0)} ${u[i]}`;
}

/** Last path segment of an agent id ("claude:<session>/<agent>") shortened to 8 chars. */
export function shortId(id) {
  if (!id) return '';
  const tail = String(id).split(/[/:]/).pop();
  return tail.slice(0, 8);
}

export const basename = (p) => (p ? String(p).split('/').pop() : '');
export const dirname = (p) => { const i = String(p || '').lastIndexOf('/'); return i > 0 ? p.slice(0, i) : ''; };

export function truncate(t, n) { t = t == null ? '' : String(t); return t.length > n ? t.slice(0, n - 1) + '…' : t; }

export function oneLine(t, n = 200) { return truncate(String(t ?? '').replace(/\s+/g, ' ').trim(), n); }

export function hashStr(str) {
  let x = 2166136261;
  for (let i = 0; i < str.length; i++) { x ^= str.charCodeAt(i); x = Math.imul(x, 16777619); }
  return x >>> 0;
}

export function debounce(fn, wait) {
  let t = 0;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), wait); };
}

/** Element showing a relative time that the global ticker refreshes every second. */
export function agoEl(ts, prefix = '', suffix = ' ago') {
  const el = h('span', { class: 'ago', title: ts ? dateTime(ts) : '' });
  el.dataset.ts = ts || '';
  el.dataset.prefix = prefix;
  el.dataset.suffix = suffix;
  el.textContent = ts ? prefix + ago(ts) + (ago(ts) === 'now' ? '' : suffix) : '—';
  return el;
}

export function tickAgo(root = document) {
  const now = Date.now();
  for (const el of root.querySelectorAll('.ago')) {
    const ts = el.dataset.ts;
    if (!ts) continue;
    const a = ago(ts, now);
    el.textContent = el.dataset.prefix + a + (a === 'now' ? '' : el.dataset.suffix);
  }
}

export function copyText(t) {
  try { navigator.clipboard?.writeText(t); return true; } catch { return false; }
}

export function b64encodeUtf8(str) {
  const bytesArr = new TextEncoder().encode(str);
  let bin = '';
  for (const b of bytesArr) bin += String.fromCharCode(b);
  return btoa(bin);
}
