// Responsive pane layout: collapsible tree/details on wide screens, slide-over panes on narrow ones.
import { register } from './store.js';
import { ui } from './ui.js';

const NARROW_RIGHT = window.matchMedia('(max-width: 1250px)');   // details become a slide-over
const NARROW_LEFT = window.matchMedia('(max-width: 900px)');     // agent tree becomes a slide-over
const body = document.body;

function load(key) { try { return localStorage.getItem('amc.' + key) === '1'; } catch { return false; } }
function save(key, on) { try { localStorage.setItem('amc.' + key, on ? '1' : '0'); } catch { /* private mode */ } }

function syncScrim() {
  const open = body.classList.contains('show-left') || body.classList.contains('show-right');
  document.getElementById('scrim').hidden = !open;
}
function closeOverlays() { body.classList.remove('show-left', 'show-right'); syncScrim(); }

function measureTop() {
  const main = document.getElementById('main');
  document.documentElement.style.setProperty('--main-top', Math.max(0, main.getBoundingClientRect().top) + 'px');
}

export function initLayout() {
  body.classList.toggle('hide-left', load('hideLeft'));
  body.classList.toggle('hide-right', load('hideRight'));

  document.getElementById('toggle-left').addEventListener('click', () => {
    if (NARROW_LEFT.matches) { body.classList.toggle('show-left'); body.classList.remove('show-right'); syncScrim(); return; }
    const on = !body.classList.contains('hide-left');
    body.classList.toggle('hide-left', on); save('hideLeft', on);
  });
  document.getElementById('toggle-right').addEventListener('click', () => {
    if (NARROW_RIGHT.matches) { body.classList.toggle('show-right'); body.classList.remove('show-left'); syncScrim(); return; }
    const on = !body.classList.contains('hide-right');
    body.classList.toggle('hide-right', on); save('hideRight', on);
  });
  document.getElementById('scrim').addEventListener('click', closeOverlays);
  // Escape closes an open slide-over and nothing else (main.js would otherwise also close the drawer / selection).
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape' || !(body.classList.contains('show-left') || body.classList.contains('show-right'))) return;
    e.stopImmediatePropagation();
    closeOverlays();
  });
  for (const mq of [NARROW_LEFT, NARROW_RIGHT]) mq.addEventListener('change', () => { closeOverlays(); measureTop(); });
  // The slide-overs start below the header, filter bar and any visible banner; banners come and go
  // (conflicts, runaway guard, simulated data) and change #main's height, so watch them all.
  const ro = new ResizeObserver(measureTop);
  for (const id of ['header', 'filterbar', 'sim-banner', 'conflict-banner', 'guard-banner', 'main']) {
    const el = document.getElementById(id);
    if (el) ro.observe(el);
  }
  window.addEventListener('resize', measureTop);
  measureTop();

  // Selecting something on a narrow screen opens the details slide-over (and closes the tree slide-over).
  let lastSel = null;
  register({
    name: 'layout', deps: ['selection'],
    render() {
      const key = ui.selection ? ui.selection.kind + ':' + ui.selection.id : null;
      if (key === lastSel) return;
      lastSel = key;
      if (!key) return;
      if (NARROW_LEFT.matches) body.classList.remove('show-left');
      if (NARROW_RIGHT.matches) body.classList.add('show-right');
      syncScrim();
    },
  });
}
