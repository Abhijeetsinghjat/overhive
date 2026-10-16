// UI state (tab, selection, filters, drawer). Tab + selection are mirrored to the URL hash
// so views can be deep-linked (and screenshotted).
import { invalidate, get } from './store.js';

export const TABS = [
  ['graph', 'Graph'],
  ['worktrees', 'Worktrees & Files'],
  ['activity', 'Activity'],
  ['timeline', 'Timeline'],
  ['commits', 'Commits'],
  ['risks', 'Risks'],
  ['processes', 'Processes'],
  ['usage', 'Usage'],
  ['health', 'Health'],
];

function load(key, dflt) {
  try { const v = localStorage.getItem('amc.' + key); return v == null ? dflt : JSON.parse(v); } catch { return dflt; }
}
function save(key, v) { try { localStorage.setItem('amc.' + key, JSON.stringify(v)); } catch { /* private mode */ } }

export const ui = {
  tab: 'graph',
  selection: null,          // { kind, id }
  filters: { repo: '', agent: '', worktree: '', branch: '', status: '', type: '', range: '', q: '' },
  session: '',              // '' = all sessions (graph)
  follow: load('follow', false),
  collapsed: new Set(load('collapsed', [])),
  theme: load('theme', 'dark'),
  drawer: { open: load('drawerOpen', false), tab: 'output', height: load('drawerHeight', 300), payload: {} },
  wt: { allWorktrees: load('wtAll', false), showReads: load('wtReads', false) },
  treeCursor: null,
};

export function setTab(tab) {
  if (!TABS.some(([t]) => t === tab) || ui.tab === tab) return;
  ui.tab = tab;
  writeHash();
  invalidate('tab');
}

export function select(kind, id, { fromFollow = false } = {}) {
  if (!kind || !id) { ui.selection = null; }
  else ui.selection = { kind, id };
  if (!fromFollow && ui.follow && kind !== 'agent') { /* following continues for agents only */ }
  if (kind === 'agent') ui.treeCursor = id;
  writeHash();
  invalidate('selection');
}

export function isSelected(kind, id) {
  return ui.selection && ui.selection.kind === kind && ui.selection.id === id;
}

export function setFilter(name, value) {
  if (ui.filters[name] === value) return;
  ui.filters[name] = value;
  invalidate('filters');
}

export function clearFilters() {
  for (const k of Object.keys(ui.filters)) ui.filters[k] = '';
  invalidate('filters');
}

export function activeFilterCount() {
  return Object.entries(ui.filters).filter(([k, v]) => v && k !== 'q').length;
}

export function toggleCollapsed(id) {
  if (ui.collapsed.has(id)) ui.collapsed.delete(id); else ui.collapsed.add(id);
  save('collapsed', [...ui.collapsed]);
  invalidate('tree');
}

export function setCollapsed(ids) {
  ui.collapsed = new Set(ids);
  save('collapsed', ids);
  invalidate('tree');
}

export function setFollow(v) { ui.follow = v; save('follow', v); invalidate('follow'); }

export function setTheme(t) {
  ui.theme = t;
  save('theme', t);
  document.documentElement.dataset.theme = t;
  invalidate('theme');
}

export function setWt(name, v) { ui.wt[name] = v; save(name === 'allWorktrees' ? 'wtAll' : 'wtReads', v); invalidate('wtopts'); }

/** Open the bottom drawer on a tab with a payload ({ kind:'diff', ... }). */
export function openDrawer(tab, payload) {
  ui.drawer.open = true;
  ui.drawer.tab = tab;
  if (payload) ui.drawer.payload[tab] = payload;
  save('drawerOpen', true);
  writeHash();
  invalidate('drawer');
}

export function closeDrawer() { ui.drawer.open = false; save('drawerOpen', false); writeHash(); invalidate('drawer'); }
export function toggleDrawer() { if (ui.drawer.open) closeDrawer(); else openDrawer(ui.drawer.tab); }
export function setDrawerHeight(px) { ui.drawer.height = px; save('drawerHeight', px); invalidate('drawerSize'); }

// ---- hash routing -----------------------------------------------------------------

let writing = false;
function writeHash() {
  const p = new URLSearchParams();
  p.set('tab', ui.tab);
  if (ui.selection) p.set('sel', `${ui.selection.kind}:${ui.selection.id}`);
  if (ui.drawer.open) p.set('drawer', ui.drawer.tab);
  const h = '#' + p.toString();
  if (location.hash !== h) { writing = true; history.replaceState(null, '', h); writing = false; }
}

export function readHash() {
  if (writing) return;
  const p = new URLSearchParams(location.hash.slice(1));
  const tab = p.get('tab');
  if (tab && TABS.some(([t]) => t === tab)) ui.tab = tab;
  const sel = p.get('sel');
  if (sel) {
    const i = sel.indexOf(':');
    ui.selection = { kind: sel.slice(0, i), id: sel.slice(i + 1) };
  }
  const theme = p.get('theme');
  if (theme === 'light' || theme === 'dark') setTheme(theme);
  const drawer = p.get('drawer');
  if (drawer) {
    ui.drawer.open = true; ui.drawer.tab = drawer;
    const target = p.get('agent') || (ui.selection?.kind === 'agent' ? ui.selection.id : null);
    if (target && (drawer === 'terminal' || drawer === 'messages') && !ui.drawer.payload[drawer]) ui.drawer.payload[drawer] = { agentId: target };
  }
  else if (p.has('tab')) ui.drawer.open = false;
  invalidate('tab', 'selection', 'drawer');
}

export function selectionEntity() {
  return ui.selection ? get(ui.selection.kind, ui.selection.id) : null;
}
