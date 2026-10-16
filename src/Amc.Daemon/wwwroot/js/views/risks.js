// Risks: the risky-action flight recorder. Every finding of the risk classifier (recursive deletes outside the
// worktree, force-pushes, credential paths, pipe-to-shell installs, publishes, database drops …), newest first,
// with severity, agent, the redacted command, the rule's explanation and the evidence behind it.
// Respects the agent / repository / worktree / time / search filters, plus its own severity filter.
import { h, mount, agoEl, oneLine, dateTime } from '../util.js';
import { register, get, all } from '../store.js';
import { ui, select, openDrawer, setFilter, setTab } from '../ui.js';
import { api } from '../api.js';
import { agentName, agentColor, evBadge } from '../model.js';
import { filterCtx } from '../filters.js';

export const SEVERITY = {
  critical: { label: 'Critical', glyph: '⛔', rank: 2 },
  warn: { label: 'Warning', glyph: '⚠', rank: 1 },
  info: { label: 'Info', glyph: 'ℹ', rank: 0 },
};
const rank = (s) => SEVERITY[s]?.rank ?? 0;

export function severityPill(sev) {
  const m = SEVERITY[sev] || SEVERITY.info;
  return h('span', { class: `pill risk-sev risk-${sev in SEVERITY ? sev : 'info'}`, title: m.label }, h('span', { class: 'glyph', 'aria-hidden': 'true' }, m.glyph), m.label);
}

/** Red badge for agents with critical findings (tree rows, graph cards). */
export function riskBadge(a) {
  const n = a?.risks?.critical || 0;
  if (!n) return null;
  const b = h('span', { class: 'risk-badge', title: `${n} critical risky action(s) recorded — open the Risks tab`, 'aria-label': `${n} critical risks` }, `⛔ ${n}`);
  b.addEventListener('click', (e) => { e.stopPropagation(); select('agent', a.id); setFilter('agent', a.id); setTab('risks'); });
  return b;
}

/** Critical findings in the store (tab badge). */
export function criticalCount() {
  let n = 0;
  for (const r of all('risk')) if (r.severity === 'critical') n++;
  return n;
}

const RULES = {
  'recursive-delete': 'Recursive delete',
  'delete-outside-worktree': 'Delete outside worktree',
  'write-outside-worktree': 'Write outside worktree',
  'git-force-push': 'Force push',
  'git-remote-delete': 'Remote branch delete',
  'git-reset-hard': 'git reset --hard',
  'git-clean': 'git clean',
  'git-discard-changes': 'Discard changes',
  'git-branch-force-delete': 'Branch force-delete',
  'chmod-recursive': 'chmod -R',
  'chown-recursive': 'chown -R',
  'pipe-to-shell': 'Pipe to shell',
  'credential-access': 'Credential path',
  'publish-deploy': 'Publish / deploy',
  'database-drop': 'Database drop',
};

export function initRisks() {
  const root = document.getElementById('view-risks');
  const info = h('span', { class: 'muted small' });
  let minSev = load();
  const sevSel = h('select', { class: 'select', title: 'Minimum severity', 'aria-label': 'Minimum severity' },
    h('option', { value: '' }, 'All severities'), h('option', { value: 'warn' }, '⚠ Warnings and critical'), h('option', { value: 'critical' }, '⛔ Critical only'));
  sevSel.value = minSev;
  sevSel.addEventListener('change', () => { minSev = sevSel.value; save(minSev); render(); });
  const body = h('div', { class: 'ptable-wrap' });
  mount(root,
    h('div', { class: 'view-toolbar' }, h('span', { class: 'view-title' }, 'Risks'), info, sevSel, h('span', { class: 'spacer' }),
      h('span', { class: 'muted small risk-note', title: 'Commands are redacted before storage. Credential files are recognised by their path only; Overhive never opens them.' },
        'Commands redacted · credential files matched by path only')),
    body);

  function render() {
    if (ui.tab !== 'risks') return;
    const ctx = filterCtx();
    const f = ui.filters;
    const minRank = minSev ? rank(minSev) : 0;
    const rows = all('risk')
      .filter((r) =>
        rank(r.severity) >= minRank &&
        (!ctx.agentSet || ctx.agentSet.has(r.agentId)) &&
        (!f.worktree || r.worktreeId === f.worktree) &&
        (!f.repo || get('worktree', r.worktreeId)?.repositoryId === f.repo) &&
        (!ctx.from || Date.parse(r.at) >= ctx.from) &&
        (!ctx.q || `${r.ruleId} ${r.explanation} ${r.command || ''} ${r.target || ''}`.toLowerCase().includes(ctx.q)))
      .sort((x, y) => (y.at || '').localeCompare(x.at || '') || (y.seq || 0) - (x.seq || 0));
    const total = all('risk').length;
    const crit = rows.filter((r) => r.severity === 'critical').length;
    info.textContent = `${rows.length} finding${rows.length === 1 ? '' : 's'}${crit ? ` · ${crit} critical` : ''}`;

    if (!rows.length) {
      mount(body, h('div', { class: 'empty' }, h('div', { class: 'empty-title' }, total ? 'No findings match' : 'No risky actions recorded'),
        total ? 'No findings match the current filters.'
          : 'Overhive checks every command and file event of every agent — recursive deletes outside the worktree, force-pushes, credential files, pipe-to-shell installs, publishes, database drops — and lists them here.'));
      return;
    }
    mount(body, h('table', { class: 'ptable risks-table' },
      h('thead', null, h('tr', null, h('th', null, 'When'), h('th', null, 'Severity'), h('th', null, 'Agent'), h('th', null, 'Rule'),
        h('th', null, 'Command'), h('th', null, 'Evidence'))),
      h('tbody', null, rows.slice(0, 500).map((r) => {
        const a = r.agentId ? get('agent', r.agentId) : null;
        const evTip = norm(r.evidence) === 'observed'
          ? 'Observed: Overhive saw this command line in the agent\'s process tree.'
          : 'Reported: the agent\'s tool hook / transcript / protocol said it ran this.';
        const tr = h('tr', { class: `risk-row risk-row-${r.severity}`, title: dateTime(r.at) },
          h('td', { class: 'nowrap muted' }, agoEl(r.at, '', ' ago')),
          h('td', { class: 'nowrap' }, severityPill(r.severity)),
          h('td', { class: 'nowrap' }, a
            ? h('span', { class: 'risk-agent' }, h('span', { class: 'agent-swatch', style: { background: agentColor(a.id) } }), ' ', oneLine(agentName(a), 30))
            : h('span', { class: 'muted' }, r.agentId || '?')),
          h('td', { class: 'risk-rule' }, h('div', { class: 'risk-rule-name' }, RULES[r.ruleId] || r.ruleId), h('div', { class: 'risk-expl' }, r.explanation)),
          h('td', { class: 'cmd mono small', title: r.command || r.span }, oneLine(r.command || r.span, 160)),
          h('td', { class: 'nowrap' }, evBadge(r.evidence, evTip), r.origin ? h('span', { class: 'muted small' }, ' ' + r.origin) : null));
        tr.addEventListener('click', () => {
          if (a) select('agent', a.id);
          if (r.seq) api.events({ after: r.seq - 1, limit: 1 }).then(([e]) => { if (e && e.seq === r.seq) openDrawer('output', { event: e }); }).catch(() => {});
        });
        return tr;
      }))));
  }

  register({ name: 'risks', deps: ['risk', 'agent', 'tab', 'filters'], render });
}

function norm(v) { return String(v ?? '').toLowerCase(); }
function load() { try { return localStorage.getItem('amc.riskSeverity') || ''; } catch { return ''; } }
function save(v) { try { localStorage.setItem('amc.riskSeverity', v); } catch { /* private mode */ } }
