// Entry point: wires views, keyboard shortcuts, the 1 s ticker and "follow active".
import { tickAgo, ms } from './util.js';
import { store, register, invalidate } from './store.js';
import { ui, setTab, TABS, readHash, toggleDrawer, closeDrawer, openDrawer, select, setFollow, setTheme, setFilter } from './ui.js';
import { api } from './api.js';
import { startSync } from './sync.js';
import { isActive } from './model.js';
import { modalOpen } from './components/overlay.js';
import { initHeader, initFilterBar, initBanners } from './views/header.js';
import { initTree, treeKey } from './views/tree.js';
import { initGraph } from './views/graph.js';
import { initHive } from './views/hive.js';
import { initWorktrees } from './views/worktrees.js';
import { initActivity } from './views/activity.js';
import { initTimeline } from './views/timeline.js';
import { initProcesses } from './views/processes.js';
import { initHealth } from './views/health.js';
import { initUsage } from './views/usage.js';
import { initCommits } from './views/commits.js';
import { initRisks } from './views/risks.js';
import { initDetail } from './views/detail.js';
import { initGuard } from './views/guard.js';
import { initDrawer } from './views/drawer.js';
import { showHelp } from './views/help.js';
import { initLayout } from './layout.js';
import { newSessionDialog } from './views/newsession.js';
import { initAttention, toggleAttentionPanel } from './views/attention.js';

setTheme(ui.theme);
readHash();
window.addEventListener('hashchange', readHash);

initAttention();
initHeader();
initFilterBar();
initBanners();
initGuard();
initTree();
initLayout();
initHive();
initGraph();
initWorktrees();
initActivity();
initTimeline();
initProcesses();
initCommits();
initRisks();
initUsage();
initHealth();
initDetail();
initDrawer();

// Follow active: keep the most recently active, non-terminal agent selected.
register({
  name: 'follow', deps: ['agent', 'follow'],
  render() {
    if (!ui.follow) return;
    let best = null;
    for (const a of store.e.agent.values()) if (isActive(a) && (!best || ms(a.lastActivityAt) > ms(best.lastActivityAt))) best = a;
    if (best && !(ui.selection?.kind === 'agent' && ui.selection.id === best.id)) select('agent', best.id, { fromFollow: true });
  },
});

setInterval(() => { tickAgo(); invalidate('tick'); }, 1000);

// Deep link: #…&ev=<seq> opens that event in the Tool output drawer.
const evSeq = Number(new URLSearchParams(location.hash.slice(1)).get('ev'));
if (evSeq > 0) {
  api.events({ after: evSeq - 1, limit: 1 }).then(([e]) => { if (e && e.seq === evSeq) openDrawer('output', { event: e }); }).catch(() => {});
}

document.addEventListener('keydown', (e) => {
  if (modalOpen() || e.ctrlKey || e.metaKey || e.altKey) return;
  const t = e.target;
  const typing = t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable || t.closest?.('.xterm'));
  if (typing) {
    if (e.key === 'Escape' && t.blur) t.blur();
    return;
  }
  if (e.key === '/') { e.preventDefault(); document.querySelector('.search')?.focus(); return; }
  if (e.key >= '1' && e.key <= '9' && +e.key <= TABS.length) { setTab(TABS[+e.key - 1][0]); return; }
  if (e.key === '?') { showHelp(); return; }
  if (e.key === 'd') { toggleDrawer(); return; }
  if (e.key === 'n') { newSessionDialog(); return; }
  if (e.key === 'a') { toggleAttentionPanel(); return; }
  if (e.key === 'f') { setFollow(!ui.follow); return; }
  if (e.key === 'Escape') {
    if (ui.filters.q) { setFilter('q', ''); const s = document.querySelector('.search'); if (s) s.value = ''; }
    else if (ui.drawer.open) closeDrawer();
    else if (ui.selection) select(null);
    return;
  }
  if (treeKey(e.key)) e.preventDefault();
});

invalidate('*', 'tab', 'selection', 'drawer', 'filters', 'conn', 'health');
startSync();
