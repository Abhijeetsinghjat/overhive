// Processes: live process overlay (WS "live" messages, falling back to polling /api/processes).
import { h, mount, keyed, bytes, duration, oneLine, ago, shortId } from '../util.js';
import { store, register, invalidate, get } from '../store.js';
import { ui, select } from '../ui.js';
import { api } from '../api.js';
import { agentColor, agentLabel, statusDot } from '../model.js';
import { filterCtx } from '../filters.js';

const STATE = { R: 'running', S: 'sleeping', D: 'disk wait', Z: 'zombie', T: 'stopped', t: 'traced', I: 'idle', X: 'dead' };
const COLS = [
  ['pid', 'PID', 'num'], ['ppid', 'PPID', 'num'], ['agent', 'Agent', ''], ['command', 'Command', ''], ['cwd', 'CWD', ''],
  ['cpuPercent', 'CPU %', 'num'], ['rssBytes', 'RSS', 'num'], ['startedAt', 'Runtime', 'num'], ['state', 'State', ''], ['tty', 'TTY', ''],
];

export function initProcesses() {
  const root = document.getElementById('view-processes');
  const info = h('span', { class: 'muted small' });
  const onlyAgents = h('input', { type: 'checkbox', checked: true });
  const tree = h('input', { type: 'checkbox', checked: true });
  onlyAgents.addEventListener('change', () => invalidate('live'));
  tree.addEventListener('change', () => invalidate('live'));
  const thead = h('thead');
  const tbody = h('tbody');
  const table = h('table', { class: 'ptable' }, thead, tbody);
  const body = h('div', { class: 'ptable-wrap' }, table);
  mount(root,
    h('div', { class: 'view-toolbar' }, h('span', { class: 'view-title' }, 'Processes'), info, h('span', { class: 'spacer' }),
      h('label', { class: 'toggle', title: 'Only processes linked to an agent (or their descendants)' }, onlyAgents, h('span', null, 'Agent processes only')),
      h('label', { class: 'toggle', title: 'Indent children under their parent process' }, tree, h('span', null, 'Tree'))),
    body);

  let sort = { key: 'cpuPercent', dir: -1 };
  let lastPoll = 0;

  const poll = async () => {
    if (ui.tab !== 'processes') return;
    if (store.live.source === 'ws' && Date.now() - store.live.at < 5000) return;
    if (Date.now() - lastPoll < 2500) return;
    lastPoll = Date.now();
    try {
      const list = await api.processes();
      store.live.processes = Array.isArray(list) ? list : [];
      store.live.at = Date.now();
      store.live.source = 'poll';
      store.live.error = null;
    } catch (e) {
      store.live.error = e;
    }
    invalidate('live');
  };
  setInterval(poll, 3000);

  register({
    name: 'processes', deps: ['live', 'tab', 'agent', 'filters', 'tick'],
    render(d) {
      if (ui.tab !== 'processes') return;
      if (d.has('tab')) poll();
      if (d.has('tick') && !d.has('live') && !d.has('tab')) { info.textContent = infoText(); return; }

      mount(thead, h('tr', null, COLS.map(([key, label, cls]) => {
        const th = h('th', { class: cls + (sort.key === key ? ' sorted' : ''), title: 'Sort by ' + label }, label, sort.key === key ? (sort.dir > 0 ? ' ▲' : ' ▼') : '');
        th.addEventListener('click', () => { sort = { key, dir: sort.key === key ? -sort.dir : (cls === 'num' ? -1 : 1) }; invalidate('live'); });
        return th;
      })));

      const err = store.live.error;
      const list = store.live.processes;
      info.textContent = infoText();
      if (err && !list) {
        tbody._keyed = null;
        mount(tbody, h('tr', null, h('td', { colspan: COLS.length, class: 'empty' },
          err.notAvailable ? 'The /api/processes endpoint is not available in this daemon build yet (process collector not merged).' : `Could not load processes: ${err.message}`)));
        return;
      }
      if (!list) { tbody._keyed = null; mount(tbody, h('tr', null, h('td', { colspan: COLS.length, class: 'empty' }, 'Loading…'))); return; }

      // Agent link: direct, or inherited from an ancestor process.
      const byPid = new Map(list.map((p) => [p.pid, p]));
      const agentOf = (p) => { let cur = p; const seen = new Set(); while (cur && !seen.has(cur.pid)) { if (cur.agentId) return cur.agentId; seen.add(cur.pid); cur = byPid.get(cur.ppid); } return null; };
      const ctx = filterCtx();
      let rows = list.map((p) => ({ p, agent: agentOf(p) }));
      if (onlyAgents.checked) rows = rows.filter((r) => r.agent);
      if (ctx.agentSet) rows = rows.filter((r) => ctx.agentSet.has(r.agent));
      if (ctx.q) rows = rows.filter((r) => `${r.p.pid} ${r.p.command} ${r.p.cwd}`.toLowerCase().includes(ctx.q));

      const val = (r, k) => k === 'agent' ? agentLabel(r.agent) : k === 'startedAt' ? -Date.parse(r.p.startedAt) : r.p[k];
      const cmp = (a, b) => { const x = val(a, sort.key), y = val(b, sort.key); return (x > y ? 1 : x < y ? -1 : 0) * sort.dir || a.p.pid - b.p.pid; };
      let ordered;
      if (tree.checked) {
        const pids = new Set(rows.map((r) => r.p.pid));
        const kids = new Map();
        for (const r of rows) { const k = pids.has(r.p.ppid) ? r.p.ppid : 0; if (!kids.has(k)) kids.set(k, []); kids.get(k).push(r); }
        ordered = [];
        const walk = (pid, depth) => { for (const r of (kids.get(pid) || []).sort(cmp)) { r.depth = depth; ordered.push(r); if (depth < 30) walk(r.p.pid, depth + 1); } };
        walk(0, 0);
      } else ordered = rows.sort(cmp).map((r) => ({ ...r, depth: 0 }));

      if (!ordered.length) {
        tbody._keyed = null;
        mount(tbody, h('tr', null, h('td', { colspan: COLS.length, class: 'empty' },
          list.length ? 'No processes match (try unticking "Agent processes only").'
            : 'No live processes reported. The process collector scans /proc every 2 s for agent processes and their children; check the Health tab if agents are running.')));
        return;
      }
      keyed(tbody, ordered, (r) => r.p.pid + ':' + r.p.procStart,
        (r) => [r.p.cpuPercent, r.p.rssBytes, r.p.state, r.depth, r.agent, Math.floor((Date.now() - Date.parse(r.p.startedAt)) / 1000)].join('|'),
        (r) => procRow(r));
    },
  });
}

function infoText() {
  const l = store.live;
  if (!l.at) return '';
  return `${(l.processes || []).length} processes · updated ${ago(l.at)} ago via ${l.source === 'ws' ? 'live stream' : 'polling'}`;
}

function procRow({ p, agent, depth }) {
  const a = agent ? get('agent', agent) : null;
  const st = String(p.state || '?');
  const tr = h('tr', { class: agent ? 'clickable' : '' },
    h('td', { class: 'num mono' }, p.pid),
    h('td', { class: 'num mono muted' }, p.ppid),
    h('td', null, agent ? h('span', { class: 'ev-agent', title: agent }, h('span', { class: 'agent-swatch', style: { background: agentColor(agent) } }), a ? statusDot(a) : null, oneLine(agentLabel(agent), 28) || shortId(agent)) : h('span', { class: 'muted' }, '—')),
    h('td', { class: 'mono cmd', title: p.command }, h('span', { style: { paddingLeft: depth * 14 + 'px' } }, depth ? '└ ' : '', oneLine(p.command, 200))),
    h('td', { class: 'mono muted cwd', title: p.cwd || '' }, '\u200e' + (p.cwd || '')),
    h('td', { class: 'num mono' + (p.cpuPercent > 50 ? ' hot' : '') }, (p.cpuPercent ?? 0).toFixed(1)),
    h('td', { class: 'num mono' }, bytes(p.rssBytes)),
    h('td', { class: 'num mono' }, duration(Date.now() - Date.parse(p.startedAt))),
    h('td', { class: 'pstate ps-' + st, title: STATE[st] || st }, st, ' ', h('span', { class: 'muted' }, STATE[st] || '')),
    h('td', { class: 'mono muted' }, p.tty || ''));
  if (agent) tr.addEventListener('click', () => select('agent', agent));
  if (p.envSummary?.length) tr.title = 'Environment (allow-listed names): ' + p.envSummary.join(', ');
  return tr;
}
