// 3D Hive capability + preference. Imports nothing, so ui.js can pick the default tab before any view
// loads; three.js itself is only fetched when the Hive tab is opened (views/hive.js).

let gl = null;

/** WebGL 2 (what the vendored three.js needs). Probed once with a throwaway context. */
export function webglAvailable() {
  if (gl != null) return gl;
  try {
    const c = document.createElement('canvas');
    const ctx = window.WebGL2RenderingContext ? c.getContext('webgl2') : null;
    gl = !!ctx;
    ctx?.getExtension('WEBGL_lose_context')?.loseContext(); // give the context back right away
  } catch { gl = false; }
  return gl;
}

/** Mark WebGL unusable for this page (context creation failed or was lost for good). */
export function markWebglBroken() { gl = false; }

/** The user's "3D: on/off" choice; on unless turned off. */
export function hivePref() {
  try { const v = localStorage.getItem('amc.hive3d'); return v == null ? true : JSON.parse(v) !== false; } catch { return true; }
}
export function saveHivePref(on) { try { localStorage.setItem('amc.hive3d', JSON.stringify(!!on)); } catch { /* private mode */ } }

export const hiveEnabled = () => hivePref() && webglAvailable();

const motionQuery = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
export const reducedMotion = () => !!motionQuery?.matches;
export function onReducedMotionChange(fn) { motionQuery?.addEventListener?.('change', fn); }
