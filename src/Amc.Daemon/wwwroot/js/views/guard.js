// Runaway guard: alert banner, header badge, agent-panel section and graph counters.
//
// Alerts are state entities (kind "guard", keyed root|rule) patched live like conflicts. Their
// value is the one at the crossing; live values per session come from GET /api/guard, polled
// only while an alert exists.
import { h, mount, oneLine, ms, agoEl, norm } from '../util.js';
import { store, register, get, all, invalidate } from '../store.js';
import { select } from '../ui.js';
import { api } from '../api.js';
import { agentName, sessionRoot, children, evBadge, fmtTok } from '../model.js';
import { toast } from '../components/overlay.js';

export const RULE_LABELS = {
  'active-sub-agents': 'Sub-agents at once',
  'spawn-rate': 'Spawn rate',
  depth: 'Tree depth',
  'token-rate': 'Token burn rate',
  'tokens-hour': 'Tokens in the last hour',
  'token-budget': 'Session token budget',
};

const live = { data: null, at: 0, loading: false, error: null };
const POLL_MS = 10000;

const sevRank = (a) => (norm(a.severity) === 'critical' ? 2 : 1);
const bySeverity = (x, y) => sevRank(y) - sevRank(x) || ms(x.raisedAt) - ms(y.raisedAt);

/** Every alert of the tree whose root is rootId, most severe first. */
export function guardAlertsFor(rootId) {
  return all('guard').filter((a) => a.rootAgentId === rootId).sort(bySeverity);
}

/** Alerts not dismissed by the user. */
export function openGuardAlerts() {
  return all('guard').filter((a) => !a.dismissed).sort(bySeverity);
}

/** Live metrics of one root from the last /api/guard poll (null when not loaded). */
export function guardMetrics(rootId) {
  return live.data?.roots?.find((r) => r.rootAgentId === rootId) || null;
}

/** Signature of the guard state of the tree an agent belongs to. */
export function guardSigForAgent(a) {
  const r = sessionRoot(a);
  return r ? guardSig(r.id) : '';
}

/** Signature for views that render guard state (detail/graph). */
export function guardSig(rootId) {
  return guardAlertsFor(rootId).map((a) => a.key + a.severity + a.dismissed + a.value).join(',') + '|' + JSON.stringify(guardMetrics(rootId));
}

async function refresh() {
  if (live.loading) return;
  live.loading = true;
  try {
    live.data = await api.get('/api/guard');
    live.error = null;
  } catch (e) {
    live.error = e;
  } finally {
    live.loading = false;
    live.at = Date.now();
    invalidate('guard-live');
  }
}

export async function pauseTree(rootId) {
  try {
    const r = await api.post('/api/guard/pause', { rootAgentId: rootId });
    const extra = (r.results || []).length > 1 ? ` (+${r.results.length - 1} sub-agent process${r.results.length === 2 ? '' : 'es'})` : '';
    toast('Pause tree: done', 'ok', (r.detail || '') + extra);
  } catch (e) {
    toast('Pause tree failed', 'err', e.body?.detail || e.message);
  }
}

export async function dismissGuard(body) {
  try {
    await api.post('/api/guard/dismiss', body);
  } catch (e) {
    toast('Dismiss failed', 'err', e.message);
  }
}

function actionBtn(label, title, fn, cls = '') {
  const b = h('button', { class: 'btn btn-sm ' + cls, title }, label);
  b.addEventListener('click', (e) => { e.stopPropagation(); fn(); });
  return b;
}

const sevTag = (a) => h('span', { class: `guard-sev gs-${norm(a.severity)}`, title: `Severity: ${norm(a.severity)}` }, norm(a.severity) === 'critical' ? 'critical' : 'warn');

/** Header indicator, placed by header.js. Hidden while there is no open alert. */
export function guardBadge() {
  const b = h('button', { class: 'guard-badge', hidden: true });
  b.addEventListener('click', () => { const a = openGuardAlerts()[0]; if (a) select('agent', a.rootAgentId); });
  register({
    name: 'guard-badge', deps: ['guard'],
    render() {
      const open = openGuardAlerts();
      const roots = new Set(open.map((a) => a.rootAgentId));
      b.hidden = !open.length;
      b.classList.toggle('critical', open.some((a) => norm(a.severity) === 'critical'));
      b.title = open.length ? `Runaway guard: ${open.length} alert(s) in ${roots.size} session(s)\n` + open.slice(0, 6).map((a) => '• ' + a.summary).join('\n') + '\nClick to open the session' : '';
      mount(b, h('span', { class: 'glyph' }, '⚠'), roots.size > 1 ? `runaway ×${roots.size}` : 'runaway');
    },
  });
  return b;
}

export function initGuard() {
  const el = document.getElementById('guard-banner');
  let expanded = false;
  let pollTimer = 0;
  register({
    name: 'guard-banner', deps: ['guard', 'agent'],
    render(d) {
      // Poll live values only while alerts exist; refresh right away when alerts change.
      const any = store.e.guard.size > 0;
      if (any && !pollTimer) pollTimer = setInterval(refresh, POLL_MS);
      if (!any && pollTimer) { clearInterval(pollTimer); pollTimer = 0; live.data = null; }
      if (any && d.has('guard')) refresh();

      if (!el) return;
      const list = openGuardAlerts();
      el.hidden = !list.length;
      if (!list.length) return;
      const row = (a) => {
        const root = get('agent', a.rootAgentId);
        const link = h('button', { class: 'link' }, root ? oneLine(agentName(root), 48) : a.rootAgentId);
        link.addEventListener('click', () => select('agent', a.rootAgentId));
        return h('div', { class: 'cb-row' },
          sevTag(a), h('span', { class: 'cb-kind' }, RULE_LABELS[a.rule] || a.rule),
          h('b', null, a.summary), ' · ', link, ' ', evBadge(a.evidence, 'Evidence behind the measured value'),
          h('span', { class: 'muted small' }, ' since ', agoEl(a.raisedAt)),
          h('span', { class: 'spacer' }),
          actionBtn('Pause tree', 'Pause this session and any sub-agent with its own process (SIGSTOP; resume any time)', () => pauseTree(a.rootAgentId)),
          actionBtn('Dismiss', 'Hide this alert (it shows again if it becomes critical)', () => dismissGuard({ key: a.key }), 'btn-ghost'));
      };
      const more = list.length > 1 ? h('button', { class: 'btn btn-sm btn-ghost' }, expanded ? 'Show less' : `+${list.length - 1} more`) : null;
      more?.addEventListener('click', () => { expanded = !expanded; invalidate('guard'); });
      mount(el, h('span', { class: 'cb-icon', 'aria-hidden': 'true' }, '⚠'),
        h('div', { class: 'cb-list' }, h('div', { class: 'guard-title' }, 'Runaway guard'), (expanded ? list : list.slice(0, 1)).map(row)), more);
    },
  });
}

/** Counters of a tree computed from the client store (depth, active, spawned, total). */
export function treeCounters(root) {
  let depth = 0, active = 0, total = 0;
  const seen = new Set([root.id]);
  const walk = (a, d) => {
    for (const c of children(a)) {
      if (seen.has(c.id)) continue;
      seen.add(c.id);
      total++;
      const st = norm(c.status);
      if (['starting', 'running', 'waiting', 'blocked'].includes(st)) { active++; depth = Math.max(depth, d); }
      walk(c, d + 1);
    }
  };
  walk(root, 1);
  return { depth, active, total };
}

/** Compact counters row for a root's graph card; null unless the tree has an alert. */
export function guardCardRow(a) {
  if (a.parentId && get('agent', a.parentId)) return null;
  const alerts = guardAlertsFor(a.id);
  if (!alerts.length) return null;
  const m = guardMetrics(a.id);
  const c = treeCounters(a);
  const critical = alerts.some((x) => norm(x.severity) === 'critical');
  return h('div', { class: `gc-guard${critical ? ' critical' : ''}`, title: alerts.map((x) => '• ' + x.summary).join('\n') + '\nAgent counts from the reported sub-agent tree; tokens reported by the tool' },
    h('b', null, '⚠ runaway'),
    ` · depth ${m ? m.maxDepth : c.depth} · ${m ? m.activeSubAgents : c.active} active`,
    m ? ` · ${m.spawnedInWindow} new` : ` · ${c.total} spawned`,
    m && m.tokensPerMinute ? ` · ${fmtTok(m.tokensPerMinute)}/min` : null);
}

/** "Runaway guard" section of the agent panel (any agent of a tree with alerts). section() comes from detail.js. */
export function guardSection(a, section) {
  const root = sessionRoot(a);
  if (!root) return null;
  const alerts = guardAlertsFor(root.id);
  if (!alerts.length) return null;
  const m = guardMetrics(root.id);
  const rules = live.data?.rules || [];
  const ruleOf = (id) => rules.find((r) => r.rule === id);
  const now = m ? h('div', { class: 'small guard-live' },
    h('b', null, 'Now: '),
    `${m.activeSubAgents} active · ${m.spawnedInWindow} spawned recently · depth ${m.maxDepth} · `,
    `${fmtTok(m.tokensPerMinute)} tokens/min · ${fmtTok(m.tokensLastHour)} in the last hour · ${fmtTok(m.sessionTokens)} total `,
    evBadge('reported', 'Sub-agent tree and token counts are reported by the tool; cache reads are not counted')) : null;
  return section('a-guard', 'Runaway guard', alerts.length,
    root.id !== a.id ? h('div', { class: 'small muted' }, 'Alerts of this session tree: ', (() => {
      const l = h('button', { class: 'link' }, oneLine(agentName(root), 48));
      l.addEventListener('click', () => select('agent', root.id));
      return l;
    })()) : null,
    alerts.map((x) => {
      const r = ruleOf(x.rule);
      return h('div', { class: 'guard-item' + (x.dismissed ? ' dismissed' : '') },
        h('div', { class: 'guard-item-head' }, sevTag(x), h('b', null, RULE_LABELS[x.rule] || x.rule), evBadge(x.evidence),
          x.dismissed ? h('span', { class: 'tag', title: 'Dismissed: hidden from the banner until it becomes critical' }, 'dismissed') : null,
          h('span', { class: 'spacer' }), h('span', { class: 'muted small' }, agoEl(x.raisedAt))),
        h('div', { class: 'small' }, x.summary),
        r ? h('div', { class: 'small muted' }, r.description, ` · warn ${r.warn > 0 ? r.warn.toLocaleString() : 'off'}, critical ${r.critical > 0 ? r.critical.toLocaleString() : 'off'}`) : null);
    }),
    now,
    live.data?.autoPause ? h('div', { class: 'small muted' }, 'Auto-pause is on: a new critical alert pauses this session automatically (audited).') : null,
    h('div', { class: 'controls' },
      actionBtn('Pause tree', 'Pause the session and any sub-agent with its own process (SIGSTOP; resume any time)', () => pauseTree(root.id)),
      alerts.some((x) => !x.dismissed) ? actionBtn('Dismiss', 'Hide these alerts from the banner (a critical escalation shows them again)', () => dismissGuard({ rootAgentId: root.id }), 'btn-ghost') : null));
}
