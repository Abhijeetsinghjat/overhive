// Header (connection, counts, conflicts, health, session picker, search), filter bar and banners.
import { h, mount, norm, ago, oneLine, shortId } from '../util.js';
import { store, register, all, get, invalidate } from '../store.js';
import { ui, setFilter, clearFilters, activeFilterCount, setTab, select, setFollow, setTheme, TABS } from '../ui.js';
import { newSessionDialog } from './newsession.js';
import { allUsage, usageTotal, usageTitle, fmtTok, counts, agentName, roots, EVENT_CATEGORIES, statusDot, worktreeLabel, repoName, conflictHeadline, conflictKindLabel } from '../model.js';
import { RANGES } from '../filters.js';
import { reconnectNow } from '../sync.js';
import { showHelp } from './help.js';
import { guardBadge } from './guard.js';
import { attentionBadge } from './attention.js';

const $ = (id) => document.getElementById(id);

export function initHeader() {
  const header = $('header');
  const conn = h('button', { class: 'conn', title: 'WebSocket connection' });
  conn.addEventListener('click', () => { if (store.conn.state !== 'live') reconnectNow(); });
  const countsEl = h('div', { class: 'counts' });
  const conflictBtn = h('button', { class: 'conflict-badge', hidden: true });
  conflictBtn.addEventListener('click', () => {
    const c = all('conflict')[0];
    if (c) select('conflict', c.key);
  });
  const sessionSel = h('select', { class: 'select', title: 'Session shown in the graph', 'aria-label': 'Session' });
  sessionSel.addEventListener('change', () => { ui.session = sessionSel.value; invalidate('session'); });
  const follow = h('label', { class: 'toggle', title: 'Follow the most recently active agent (selects it automatically)' },
    h('input', { type: 'checkbox', checked: ui.follow }), h('span', { class: 'lbl' }, 'Follow active'));
  follow.firstChild.addEventListener('change', (e) => setFollow(e.target.checked));

  const search = h('input', { class: 'input search', type: 'search', placeholder: 'Search agents, files, events…  /', 'aria-label': 'Search', spellcheck: 'false' });
  const results = h('div', { class: 'search-results', hidden: true });
  let sel = 0;
  const runSearch = () => {
    setFilter('q', search.value);
    renderResults(results, search.value, sel);
  };
  search.addEventListener('input', () => { sel = 0; runSearch(); });
  search.addEventListener('focus', () => renderResults(results, search.value, sel));
  search.addEventListener('blur', () => setTimeout(() => { results.hidden = true; }, 150));
  search.addEventListener('keydown', (e) => {
    const items = results.querySelectorAll('.sr-item');
    if (e.key === 'ArrowDown') { sel = Math.min(items.length - 1, sel + 1); renderResults(results, search.value, sel); e.preventDefault(); }
    else if (e.key === 'ArrowUp') { sel = Math.max(0, sel - 1); renderResults(results, search.value, sel); e.preventDefault(); }
    else if (e.key === 'Enter') { items[sel]?.dispatchEvent(new MouseEvent('mousedown')); search.blur(); }
    else if (e.key === 'Escape') { search.value = ''; setFilter('q', ''); results.hidden = true; search.blur(); }
  });
  const healthDot = h('button', { class: 'health-dot', title: 'System health' });
  healthDot.addEventListener('click', () => setTab('health'));
  const newSession = h('button', { class: 'btn btn-primary new-session-btn', title: 'Start a new AI agent session (any configured tool) in a terminal Overhive owns (n)' }, '+ New session');
  newSession.addEventListener('click', newSessionDialog);
  // Labelled with the theme it switches TO, so it reads as an action.
  const theme = h('button', { class: 'theme-btn', title: 'Switch between dark and light theme' });
  const labelTheme = () => { theme.textContent = ui.theme === 'dark' ? '☀ Light' : '☾ Dark'; };
  theme.addEventListener('click', () => { setTheme(ui.theme === 'dark' ? 'light' : 'dark'); labelTheme(); });
  labelTheme();
  register({ name: 'theme-btn', deps: ['theme'], render: labelTheme });
  const help = h('button', { class: 'icon-btn', title: 'Keyboard shortcuts (?)' }, '?');
  help.addEventListener('click', showHelp);

  mount(header,
    h('div', { class: 'brand' }, h('img', { class: 'logo-img', src: '/logo.svg', alt: '' }), h('span', { class: 'brand-name' }, 'Overhive'),
      h('span', { class: 'beta-tag', title: 'Overhive is in beta: tested on Linux; please report problems on GitHub' }, 'Beta')),
    conn, attentionBadge(), countsEl, conflictBtn, guardBadge(),
    h('div', { class: 'spacer' }),
    sessionSel, follow,
    h('div', { class: 'search-wrap' }, search, results),
    newSession, healthDot, theme, help);

  register({
    name: 'header', deps: ['conn', 'agent', 'conflict', 'health', 'follow', 'tick'],
    render() {
      // Connection
      const c = store.conn;
      const label = { live: 'Live', connecting: 'Connecting…', syncing: 'Syncing…', reconnecting: 'Reconnecting', offline: 'Offline', polling: 'Polling (no live stream)' }[c.state] || c.state;
      conn.className = 'conn conn-' + c.state;
      const retry = c.state === 'reconnecting' && c.nextRetryAt > Date.now() ? ` in ${Math.ceil((c.nextRetryAt - Date.now()) / 1000)}s` : '';
      mount(conn, h('span', { class: 'conn-dot' }), label + retry);
      conn.title = `WebSocket: ${label}${c.detail ? ' — ' + c.detail : ''}${c.lastMessageAt ? `\nLast message ${ago(c.lastMessageAt)} ago` : ''}\nseq ${store.seq}${c.state !== 'live' ? '\nClick to reconnect now' : ''}`;

      // Counts
      const k = counts();
      const chip = (group, glyph, cls, n, label) => {
        const b = h('button', { class: `count ${cls}${ui.filters.status === group ? ' on' : ''}${n ? '' : ' zero'}`, title: `${n} ${label} — click to filter` },
          h('span', { class: 'glyph' }, glyph), h('b', null, n), h('span', { class: 'lbl' }, ' ' + label));
        b.addEventListener('click', () => setFilter('status', ui.filters.status === group ? '' : group));
        return b;
      };
      mount(countsEl,
        chip('active', '●', 'st-running', k.active, 'active'),
        chip('waiting', '◷', 'st-waiting', k.waiting, 'waiting'),
        chip('blocked', '!', 'st-blocked', k.blocked, 'blocked'),
        chip('failed', '✕', 'st-failed', k.failed, 'failed'),
        chip('disconnected', '⌁', 'st-disconnected', k.disconnected, 'disconnected'),
        chip('completed', '✓', 'st-completed', k.completed, 'done'),
        (() => {
          const u = allUsage();
          const b = h('button', { class: 'count tok-count', title: usageTitle(u, 'All agents') + '\nClick for the Usage tab' },
            h('span', { class: 'glyph' }, '◈'), h('b', null, fmtTok(usageTotal(u))), h('span', { class: 'lbl' }, ' tokens'));
          b.addEventListener('click', () => setTab('usage'));
          return b;
        })());

      // Conflicts
      const nConf = store.e.conflict.size;
      conflictBtn.hidden = !nConf;
      mount(conflictBtn, h('span', { class: 'glyph' }, '⚠'), `${nConf} conflict${nConf === 1 ? '' : 's'}`);

      // Health
      let hs = store.healthError ? 'failed' : norm(store.health?.overall) || 'starting';
      if (hs === 'ok' && store.conn.state !== 'live') hs = 'degraded';
      const stalled = store.health && store.healthAt && Date.now() - store.healthAt > 20000;
      healthDot.className = `health-dot hd-${stalled ? 'degraded' : hs}`;
      healthDot.title = store.healthError ? `Health check failed: ${store.healthError}` : `System health: ${hs}${stalled ? ' (stale)' : ''} — click for details`;
      mount(healthDot, h('span', { class: 'hd-dot' }), 'Health');

      // Session picker
      const sessions = roots();
      const sig = sessions.map((a) => a.id + agentName(a)).join('|') + ui.session;
      if (sessionSel._sig !== sig) {
        sessionSel._sig = sig;
        mount(sessionSel, h('option', { value: '' }, `All sessions (${sessions.length})`),
          sessions.map((a) => h('option', { value: a.id, selected: ui.session === a.id }, oneLine(agentName(a), 48) + ' · ' + shortId(a.sessionId || a.id))));
        sessionSel.value = ui.session;
      }
      follow.firstChild.checked = ui.follow;
    },
  });
}

function renderResults(box, q, sel) {
  q = q.trim().toLowerCase();
  if (!q) { box.hidden = true; return; }
  const out = [];
  for (const a of store.e.agent.values()) {
    if ([a.id, a.title, a.description, a.agentType].filter(Boolean).join(' ').toLowerCase().includes(q)) out.push(['agent', a.id, agentName(a), a.agentType || norm(a.kind)]);
  }
  for (const f of store.e.file.values()) if (f.path.toLowerCase().includes(q)) out.push(['file', f.key, f.path, worktreeLabel(get('worktree', f.worktreeId))]);
  for (const w of store.e.worktree.values()) if ((w.root + ' ' + (w.branch || '')).toLowerCase().includes(q)) out.push(['worktree', w.id, worktreeLabel(w), w.root]);
  for (const r of store.e.repository.values()) if (r.name.toLowerCase().includes(q)) out.push(['repository', r.id, r.name, r.commonDir]);
  const shown = out.slice(0, 12);
  mount(box,
    shown.length ? null : h('div', { class: 'sr-empty' }, 'No matching agents, files or worktrees. Event lists are filtered by this text.'),
    shown.map(([kind, id, label, sub], i) => {
      const el = h('div', { class: 'sr-item' + (i === sel ? ' sel' : '') },
        h('span', { class: 'sr-kind' }, kind), kind === 'agent' ? statusDot(get('agent', id)) : null,
        h('span', { class: 'sr-label' }, label), h('span', { class: 'sr-sub' }, sub || ''));
      el.addEventListener('mousedown', (e) => { e.preventDefault(); select(kind, id); box.hidden = true; });
      return el;
    }),
    out.length > shown.length ? h('div', { class: 'sr-empty' }, `${out.length - shown.length} more…`) : null);
  box.hidden = false;
}

// ---- filter bar -----------------------------------------------------------------

export function initFilterBar() {
  const bar = $('filterbar');
  const mk = (name, label) => {
    const el = h('select', { class: 'select', 'aria-label': label, title: label });
    el.addEventListener('change', () => setFilter(name, el.value));
    return el;
  };
  const repo = mk('repo', 'Repository');
  const agent = mk('agent', 'Agent (includes sub-agents)');
  const wt = mk('worktree', 'Worktree');
  const branch = mk('branch', 'Branch');
  const status = mk('status', 'Agent status');
  const type = mk('type', 'Event type');
  const range = mk('range', 'Time range');
  const clear = h('button', { class: 'btn btn-ghost' }, 'Clear filters');
  clear.addEventListener('click', clearFilters);
  const tabs = $('tabs');

  mount(bar, h('span', { class: 'fb-label' }, 'Filter'), repo, agent, wt, branch, status, type, range, clear);

  const opts = (el, list, value) => {
    const sig = JSON.stringify(list) + value;
    if (el._sig === sig) return;
    el._sig = sig;
    mount(el, list.map(([v, l]) => h('option', { value: v }, l)));
    el.value = value;
    el.classList.toggle('on', !!value);
  };

  register({
    name: 'filterbar', deps: ['filters', 'agent', 'repository', 'worktree'],
    render() {
      const f = ui.filters;
      opts(repo, [['', 'All repositories'], ...all('repository').map((r) => [r.id, r.name])], f.repo);
      opts(agent, [['', 'All agents'], ...flatAgents().map(([a, d]) => [a.id, '  '.repeat(d) + oneLine(agentName(a), 40)])], f.agent);
      opts(wt, [['', 'All worktrees'], ...all('worktree').map((w) => [w.id, `${repoName(w.repositoryId)} · ${worktreeLabel(w)}`])], f.worktree);
      const branches = [...new Set(all('worktree').map((w) => w.branch).filter(Boolean))].sort();
      opts(branch, [['', 'All branches'], ...branches.map((b) => [b, b])], f.branch);
      opts(status, [['', 'Any status'], ['active', '● Active'], ['waiting', '◷ Waiting / paused'], ['blocked', '! Blocked'], ['failed', '✕ Failed'], ['disconnected', '⌁ Disconnected'], ['completed', '✓ Completed'], ['unknown', '? Unknown']], f.status);
      opts(type, EVENT_CATEGORIES, f.type);
      opts(range, RANGES, f.range);
      const n = activeFilterCount();
      clear.disabled = !n && !f.q;
      clear.textContent = n ? `Clear filters (${n})` : 'Clear filters';
    },
  });

  // Tabs
  register({
    name: 'tabs', deps: ['tab'],
    render() {
      mount(tabs, TABS.map(([t, label], i) => {
        const b = h('button', { class: 'tab' + (ui.tab === t ? ' on' : ''), role: 'tab', 'aria-selected': ui.tab === t ? 'true' : 'false', title: `${label} (${i + 1})` },
          h('kbd', null, String(i + 1)), label);
        b.addEventListener('click', () => setTab(t));
        return b;
      }));
      for (const v of document.querySelectorAll('.view')) v.classList.toggle('on', v.dataset.tab === ui.tab);
    },
  });
}

function flatAgents() {
  const out = [];
  const walk = (a, d, seen) => {
    if (seen.has(a.id)) return; seen.add(a.id);
    out.push([a, d]);
    for (const c of (a.childIds || []).map((id) => get('agent', id)).filter(Boolean)) walk(c, d + 1, seen);
  };
  const seen = new Set();
  for (const r of roots()) walk(r, 0, seen);
  return out;
}

// ---- banners ------------------------------------------------------------------------

export function initBanners() {
  const sim = $('sim-banner');
  const conf = $('conflict-banner');
  let expanded = false;
  register({
    name: 'banners', deps: ['simulated', 'conflict', 'agent'],
    render() {
      sim.hidden = !store.simulated;
      if (store.simulated) {
        mount(sim, h('b', null, 'SIMULATED DATA'),
          ' — events in view come from the simulator source (Source = "simulator"), not from a real agent. Do not act on them.');
      }
      const list = all('conflict').sort((a, b) => Date.parse(b.detectedAt) - Date.parse(a.detectedAt));
      conf.hidden = !list.length;
      if (!list.length) return;
      const row = (c) => {
        const agents = (c.agentIds || []).map((id) => {
          const a = get('agent', id);
          const link = h('button', { class: 'link' }, a ? agentName(a) : shortId(id));
          link.addEventListener('click', () => select('agent', id));
          return link;
        });
        const path = c.path ? h('button', { class: 'link mono' }, c.path) : null;
        path?.addEventListener('click', () => {
          const key = c.worktreeId ? c.worktreeId + '|' + c.path : (c.worktreeIds?.[0] ? c.worktreeIds[0] + '|' + c.path : null);
          if (key && get('file', key)) select('file', key); else select('conflict', c.key);
        });
        const wts = (c.worktreeIds || []).map((id) => {
          const w = get('worktree', id);
          const l = h('button', { class: 'link' }, w ? worktreeLabel(w) : id);
          l.addEventListener('click', () => select('worktree', id));
          return l;
        });
        const details = h('button', { class: 'btn btn-sm' }, 'Details');
        details.addEventListener('click', () => select('conflict', c.key));
        return h('div', { class: 'cb-row' },
          h('span', { class: 'cb-kind' }, conflictKindLabel(c.kind)),
          h('b', null, conflictHeadline(c)), path ? [' · ', path] : null,
          wts.length ? [' · in ', ...join(wts)] : null,
          agents.length ? [' · ', ...join(agents)] : null,
          h('span', { class: 'spacer' }), details);
      };
      const more = list.length > 1 ? h('button', { class: 'btn btn-sm btn-ghost' }, expanded ? 'Show less' : `+${list.length - 1} more`) : null;
      more?.addEventListener('click', () => { expanded = !expanded; invalidate('conflict'); });
      mount(conf, h('span', { class: 'cb-icon', 'aria-hidden': 'true' }, '⚠'),
        h('div', { class: 'cb-list' }, (expanded ? list : list.slice(0, 1)).map(row)), more);
    },
  });
}

function join(nodes) {
  const out = [];
  nodes.forEach((n, i) => { if (i) out.push(', '); out.push(n); });
  return out;
}
