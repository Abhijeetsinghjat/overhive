// 3D effects: the user setting (localStorage amc.effects3d = on | off, mirrored as <html data-effects="off">),
// pointer tilt for cards and a helper to replay entry animations. Tilt only runs for a mouse or pen, never
// under prefers-reduced-motion, and the transform is removed as soon as the pointer leaves, so idle text
// is never rendered through a transform.
const KEY = 'amc.effects3d';
const reduced = window.matchMedia('(prefers-reduced-motion: reduce)');
const TILT = '.gcard, .wt-card, .stat, .comp';
const MAX_DEG = 4.5;

export function effectsSetting() {
  try { return localStorage.getItem(KEY) === 'off' ? 'off' : 'on'; } catch { return 'on'; }
}

function apply(v) {
  if (v === 'off') document.documentElement.dataset.effects = 'off';
  else delete document.documentElement.dataset.effects;
}

export function setEffects(v) {
  const val = v === 'off' ? 'off' : 'on';
  try { localStorage.setItem(KEY, val); } catch { /* private mode */ }
  apply(val);
  if (val === 'off') release();
}

/** True when 3D motion should run (setting on and no reduced-motion preference). */
export function effectsActive() {
  return document.documentElement.dataset.effects !== 'off' && !reduced.matches;
}

/** Restarts a CSS entry animation by toggling a class (e.g. the details panel on a new selection). */
export function replay(el, cls = 'fx-enter') {
  if (!el || !effectsActive()) return;
  el.classList.remove(cls);
  void el.offsetWidth; // reflow so the animation starts again
  el.classList.add(cls);
  clearTimeout(el._fxT);
  // Drop the class once played, so later re-renders of the content do not animate again.
  el._fxT = setTimeout(() => el.classList.remove(cls), 600);
}

let cur = null, rect = null, px = 0, py = 0, raf = 0;

function release() {
  if (raf) { cancelAnimationFrame(raf); raf = 0; }
  if (!cur) return;
  cur.classList.remove('fx-tilting');
  for (const p of ['--fx-rx', '--fx-ry', '--fx-mx', '--fx-my']) cur.style.removeProperty(p);
  cur = null; rect = null;
}

function frame() {
  raf = 0;
  if (!cur || !rect || !cur.isConnected) { release(); return; }
  const x = Math.min(1, Math.max(0, (px - rect.left) / rect.width));
  const y = Math.min(1, Math.max(0, (py - rect.top) / rect.height));
  // Scale the angle down for big cards so their far edge does not swing too far.
  const k = Math.min(1, 320 / Math.max(rect.width, 1));
  cur.style.setProperty('--fx-ry', ((x - 0.5) * 2 * MAX_DEG * k).toFixed(2) + 'deg');
  cur.style.setProperty('--fx-rx', ((0.5 - y) * 2 * MAX_DEG * k).toFixed(2) + 'deg');
  cur.style.setProperty('--fx-mx', (x * 100).toFixed(1) + '%');
  cur.style.setProperty('--fx-my', (y * 100).toFixed(1) + '%');
  cur.classList.add('fx-tilting');
}

function onMove(e) {
  if (e.pointerType !== 'mouse' && e.pointerType !== 'pen') return;
  if (e.buttons || !effectsActive()) { release(); return; }
  const el = e.target instanceof Element ? e.target.closest(TILT) : null;
  if (el !== cur) {
    release();
    if (!el) return;
    cur = el;
    rect = el.getBoundingClientRect(); // measured once, before it tilts (a tilted rect would feed back)
  }
  px = e.clientX; py = e.clientY;
  if (!raf) raf = requestAnimationFrame(frame);
}

let started = false;
/** Applies the stored setting and starts the tilt helper (idempotent). */
export function initEffects() {
  apply(effectsSetting());
  if (started) return;
  started = true;
  document.addEventListener('pointermove', onMove, { passive: true });
  document.addEventListener('pointerdown', release, { passive: true });
  document.documentElement.addEventListener('pointerleave', release);
  document.addEventListener('scroll', release, { passive: true, capture: true });
  window.addEventListener('blur', release);
  reduced.addEventListener?.('change', release);
  // Another tab changed the setting.
  window.addEventListener('storage', (e) => { if (e.key === KEY) apply(effectsSetting()); });
}
