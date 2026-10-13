// Usage: token consumption per session (with sub-agents), per agent and per model.
// Numbers are what the provider reported on each model response (evidence: reported).
import { h, mount } from '../util.js';
import { register, invalidate } from '../store.js';
import { ui, select } from '../ui.js';
import {
  roots, children, agentName, statusDot, evBadge, fmtTok, usageRollup, usageByModelRollup, usageTotal, addUsage, allUsage,
  usageTitle, shortModelName,
} from '../model.js';
import { agentPasses, filterCtx } from '../filters.js';

const COLS = [['input', 'Input'], ['output', 'Output'], ['cacheWrite', 'Cache write'], ['cacheRead', 'Cache read']];
const expanded = new Set();

export function initUsage() {
  const root = document.getElementById('view-usage');
  const summary = h('div', { class: 'usage-summary' });
  const table = h('div', { class: 'usage-wrap' });
  const models = h('div', { class: 'usage-models' });
  mount(root,
    h('div', { class: 'view-toolbar' }, h('span', { class: 'view-title' }, 'Usage'),
      evBadge('reported', 'Token counts reported by the provider on every model response (transcripts)'),
      h('span', { class: 'muted small' }, 'Tokens per session, agent and model. Cost is not estimated: prices depend on your model and plan.')),
    h('div', { class: 'usage-body' }, summary, table, models));

  register({
    name: 'usage', deps: ['agent', 'tab', 'filters', 'session'],
    render() {
      if (ui.tab !== 'usage') return;
      const ctx = filterCtx();
      const sessions = roots().filter((r) => !ctx.agentSet && !ctx.q || agentPasses(r, ctx) || children(r).some((c) => agentPasses(c, ctx)))
        .map((r) => ({ r, u: usageRollup(r) }))
        .sort((x, y) => usageTotal(y.u) - usageTotal(x.u));

      const total = allUsage();
      mount(summary,
        stat('Total tokens', fmtTok(usageTotal(total)), usageTitle(total, 'All agents')),
        stat('Input', fmtTok(total.input)), stat('Output', fmtTok(total.output)),
        stat('Cache write', fmtTok(total.cacheWrite)), stat('Cache read', fmtTok(total.cacheRead)),
        stat('Model responses', total.responses.toLocaleString()),
        stat('Sessions', String(sessions.length)));

      const max = Math.max(1, ...sessions.map((x) => usageTotal(x.u)));
      const rows = [];
      for (const { r, u } of sessions) {
        const kids = children(r);
        const open = expanded.has(r.id);
        rows.push(row(r, u, 0, max, kids.length ? (open ? '▾' : '▸') : '', () => { open ? expanded.delete(r.id) : expanded.add(r.id); invalidate('agent'); }));
        if (open) {
          rows.push(row(r, { ...r.usage }, 1, max, '', null, 'session itself'));
          for (const k of kids.map((c) => ({ c, u: usageRollup(c) })).sort((x, y) => usageTotal(y.u) - usageTotal(x.u)))
            rows.push(row(k.c, k.u, 1, max));
        }
      }
      mount(table, sessions.length
        ? h('table', { class: 'ptable usage-table' },
          h('thead', null, h('tr', null, h('th', null, 'Session / agent'), h('th', null, 'Model'), ...COLS.map(([, l]) => h('th', { class: 'num' }, l)),
            h('th', { class: 'num' }, 'Total'), h('th', { class: 'num' }, 'Responses'), h('th', null, 'Share'))),
          h('tbody', null, rows))
        : h('div', { class: 'empty' }, h('div', { class: 'empty-title' }, 'No usage yet'), 'Token usage appears as agents produce model responses.'));

      const byModel = new Map();
      for (const { r } of sessions) for (const [m, mu] of usageByModelRollup(r)) addUsage(byModel.get(m) || byModel.set(m, zero()).get(m), mu);
      mount(models, byModel.size
        ? [h('div', { class: 'section-title' }, 'By model'),
          h('table', { class: 'ptable usage-table' },
            h('thead', null, h('tr', null, h('th', null, 'Model'), ...COLS.map(([, l]) => h('th', { class: 'num' }, l)), h('th', { class: 'num' }, 'Total'), h('th', { class: 'num' }, 'Responses'))),
            h('tbody', null, [...byModel].sort((a, b) => usageTotal(b[1]) - usageTotal(a[1])).map(([m, mu]) =>
              h('tr', null, h('td', null, h('span', { class: 'gc-model', title: m }, shortModelName(m))),
                ...COLS.map(([k]) => h('td', { class: 'num' }, fmtTok(mu[k]))), h('td', { class: 'num' }, h('b', null, fmtTok(usageTotal(mu)))),
                h('td', { class: 'num' }, mu.responses.toLocaleString())))))]
        : null);
    },
  });

}

const zero = () => ({ input: 0, output: 0, cacheWrite: 0, cacheRead: 0, responses: 0 });

function stat(label, value, title) {
  return h('div', { class: 'stat', title: title || '' }, h('div', { class: 'stat-v' }, value), h('div', { class: 'stat-l' }, label));
}

function row(a, u, depth, max, caret = '', onCaret = null, note = null) {
  const models = Object.keys(a.usageByModel || {});
  const total = usageTotal(u);
  const caretEl = h('span', { class: 'caret' + (caret ? '' : ' empty') }, caret || '');
  if (onCaret) caretEl.addEventListener('click', (e) => { e.stopPropagation(); onCaret(); });
  const tr = h('tr', { class: depth ? 'usage-sub' : 'usage-session', title: usageTitle(u, agentName(a)) },
    h('td', null, h('span', { class: 'usage-name', style: { paddingLeft: depth * 18 + 'px' } }, caretEl, statusDot(a), ' ',
      note ? h('span', { class: 'muted' }, note) : agentName(a))),
    h('td', null, models.map((m) => h('span', { class: 'gc-model', title: m }, shortModelName(m)))),
    ...COLS.map(([k]) => h('td', { class: 'num' }, fmtTok(u[k]))),
    h('td', { class: 'num' }, h('b', null, fmtTok(total))),
    h('td', { class: 'num' }, (u.responses || 0).toLocaleString()),
    h('td', null, h('span', { class: 'bar usage-bar' }, h('span', { class: 'bar-fill', style: { width: (100 * total / max) + '%' } }))));
  tr.addEventListener('click', () => select('agent', a.id));
  return tr;
}
