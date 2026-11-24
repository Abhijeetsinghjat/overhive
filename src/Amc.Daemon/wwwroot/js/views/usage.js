// Usage: token consumption per session (with sub-agents), per agent and per model, usage over time, and the
// estimated API cost at list prices (docs/USAGE.md). Token numbers are what the provider reported on each model
// response (evidence: reported); cost is Overhive's estimate from /api/usage/report (evidence: inferred).
import { h, mount } from '../util.js';
import { api } from '../api.js';
import { renderUsageChart, seriesOrder, fmtMoney } from '../components/usage-chart.js';
import { register, invalidate } from '../store.js';
import { ui, select } from '../ui.js';
import {
  roots, children, agentName, statusDot, evBadge, fmtTok, usageRollup, usageByModelRollup, usageTotal, addUsage, allUsage,
  usageTitle, shortModelName,
} from '../model.js';
import { agentPasses, filterCtx } from '../filters.js';

const COLS = [['input', 'Input'], ['output', 'Output'], ['cacheWrite', 'Cache write'], ['cacheRead', 'Cache read']];
const expanded = new Set();
const RANGES = [['48h', 'Last 48 hours'], ['7d', '7 days'], ['30d', '30 days'], ['90d', '90 days'], ['all', 'All kept']];
const REFRESH_MS = 60_000;
const COST_TIP = 'Estimated API cost: what these tokens would cost at the provider\'s published API list prices '
  + '(Anthropic models built in; add others in config "Pricing"). Not what a subscription plan charges you.';

// Reports from the daemon: `all` (every usage record kept) drives the cost columns, `range` the chart.
const rep = { all: null, range: null, error: null, loadedAt: 0, loading: false };
const prefs = { range: pref('range', '30d'), metric: pref('metric', 'cost'), view: pref('view', 'chart') };
const tz = (() => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch { return ''; } })();

function pref(k, d) { try { return localStorage.getItem('amc.usage.' + k) || d; } catch { return d; } }
function savePref(k, v) { prefs[k] = v; try { localStorage.setItem('amc.usage.' + k, v); } catch { /* private mode */ } }

async function load(force = false) {
  if (rep.loading || (!force && Date.now() - rep.loadedAt < REFRESH_MS)) return;
  rep.loading = true;
  try {
    const [all, range] = await Promise.all([
      api.usageReport({ range: 'all', bucket: 'day', tz }),
      api.usageReport({ range: prefs.range, tz }),
    ]);
    Object.assign(rep, { all, range, error: null, loadedAt: Date.now() });
  } catch (e) {
    rep.error = e.notAvailable ? 'This daemon has no usage report yet (update Overhive).' : e.message;
    rep.loadedAt = Date.now();
  } finally {
    rep.loading = false;
    invalidate('usage-report');
  }
}

/** Estimated cost of an agent and all of its sub-agents (any depth), or null when pricing is off / not loaded. */
function costRollup(a, seen = new Set()) {
  if (!rep.all?.pricing) return null;
  const acc = { cost: 0, unpricedTokens: 0, any: false };
  const walk = (x) => {
    if (!x || seen.has(x.id)) return;
    seen.add(x.id);
    const t = rep.all.agents?.[x.id];
    if (t) { acc.cost += t.cost || 0; acc.unpricedTokens += t.unpricedTokens || 0; acc.any = true; }
    for (const c of children(x)) walk(c);
  };
  walk(a);
  return acc;
}

function costCell(c, cls = 'num') {
  if (!c) return h('td', { class: cls }, '');
  if (!c.any) return h('td', { class: cls + ' muted', title: 'No usage records kept for this agent' }, '—');
  const partial = c.unpricedTokens > 0;
  return h('td', { class: cls, title: partial ? `Plus ${fmtTok(c.unpricedTokens)} tokens from models without a price (not included)` : COST_TIP },
    fmtMoney(c.cost, rep.all.currency), partial ? h('span', { class: 'muted' }, '+') : null);
}

export function initUsage() {
  const root = document.getElementById('view-usage');
  const summary = h('div', { class: 'usage-summary' });
  const over = h('div', { class: 'usage-over' });
  const table = h('div', { class: 'usage-wrap' });
  const models = h('div', { class: 'usage-models' });
  const note = h('span', { class: 'muted small' }, 'Tokens per session, agent and model, usage over time and the estimated API cost.');
  mount(root,
    h('div', { class: 'view-toolbar' }, h('span', { class: 'view-title' }, 'Usage'),
      evBadge('reported', 'Token counts reported by the provider on every model response (transcripts)'),
      evBadge('inferred', COST_TIP), note),
    h('div', { class: 'usage-body' }, summary, over, table, models));

  setInterval(() => { if (ui.tab === 'usage' && !document.hidden) load(); }, REFRESH_MS);
  if (typeof ResizeObserver === 'function') {
    let w = 0;
    new ResizeObserver(() => { if (over.clientWidth !== w) { w = over.clientWidth; invalidate('usage-report'); } }).observe(over);
  }

  register({
    name: 'usage-over', deps: ['tab', 'usage-report'],
    render() {
      if (ui.tab !== 'usage') return;
      load();
      renderOver(over);
    },
  });

  register({
    name: 'usage', deps: ['agent', 'tab', 'filters', 'session', 'usage-report'],
    render() {
      if (ui.tab !== 'usage') return;
      const pricing = !!rep.all?.pricing;
      const ctx = filterCtx();
      const sessions = roots().filter((r) => !ctx.agentSet && !ctx.q || agentPasses(r, ctx) || children(r).some((c) => agentPasses(c, ctx)))
        .map((r) => ({ r, u: usageRollup(r) }))
        .sort((x, y) => usageTotal(y.u) - usageTotal(x.u));

      const total = allUsage();
      // Same scope as the token tiles beside it (allUsage: every agent), not the filtered table.
      const allCost = pricing ? roots().reduce((acc, r) => { const c = costRollup(r); acc.cost += c.cost; acc.unpriced += c.unpricedTokens; return acc; }, { cost: 0, unpriced: 0 }) : null;
      mount(summary,
        pricing ? stat('Est. API cost', fmtMoney(allCost.cost, rep.all.currency) + (allCost.unpriced ? '+' : ''),
          COST_TIP + (allCost.unpriced ? `\nPlus ${fmtTok(allCost.unpriced)} tokens from models without a price (not included).` : '')
            + (ttlNote(rep.all.totals) ? '\n' + ttlNote(rep.all.totals) : ''), 'stat-cost') : null,
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
        rows.push(row(r, u, 0, max, kids.length ? (open ? '▾' : '▸') : '', () => { open ? expanded.delete(r.id) : expanded.add(r.id); invalidate('agent'); }, null, pricing && costRollup(r)));
        if (open) {
          rows.push(row(r, { ...r.usage }, 1, max, '', null, 'session itself', pricing && ownCost(r)));
          for (const k of kids.map((c) => ({ c, u: usageRollup(c) })).sort((x, y) => usageTotal(y.u) - usageTotal(x.u)))
            rows.push(row(k.c, k.u, 1, max, '', null, null, pricing && costRollup(k.c)));
        }
      }
      mount(table, sessions.length
        ? h('table', { class: 'ptable usage-table' },
          h('thead', null, h('tr', null, h('th', null, 'Session / agent'), h('th', null, 'Model'),
            pricing ? h('th', { class: 'num', title: COST_TIP }, 'Est. cost') : null, ...COLS.map(([, l]) => h('th', { class: 'num' }, l)),
            h('th', { class: 'num' }, 'Total'),
            h('th', { class: 'num' }, 'Responses'), h('th', null, 'Share'))),
          h('tbody', null, rows))
        : h('div', { class: 'empty' }, h('div', { class: 'empty-title' }, 'No usage yet'), 'Token usage appears as agents produce model responses.'));

      const byModel = new Map();
      for (const { r } of sessions) for (const [m, mu] of usageByModelRollup(r)) addUsage(byModel.get(m) || byModel.set(m, zero()).get(m), mu);
      mount(models, byModel.size
        ? [h('div', { class: 'section-title' }, 'By model'),
          h('table', { class: 'ptable usage-table' },
            h('thead', null, h('tr', null, h('th', null, 'Model'), ...COLS.map(([, l]) => h('th', { class: 'num' }, l)), h('th', { class: 'num' }, 'Total'),
              pricing ? h('th', { class: 'num', title: 'Estimated API cost of this model\'s responses kept in the history (all sessions)' }, 'Est. cost (all kept)') : null,
              h('th', { class: 'num' }, 'Responses'))),
            h('tbody', null, [...byModel].sort((a, b) => usageTotal(b[1]) - usageTotal(a[1])).map(([m, mu]) =>
              h('tr', null, h('td', null, h('span', { class: 'gc-model', title: m }, shortModelName(m))),
                ...COLS.map(([k]) => h('td', { class: 'num' }, fmtTok(mu[k]))), h('td', { class: 'num' }, h('b', null, fmtTok(usageTotal(mu)))),
                pricing ? modelCostCell(m) : null,
                h('td', { class: 'num' }, mu.responses.toLocaleString())))))]
        : null);
    },
  });

}

const zero = () => ({ input: 0, output: 0, cacheWrite: 0, cacheRead: 0, responses: 0 });

function stat(label, value, title, cls = '') {
  return h('div', { class: ('stat ' + cls).trim(), title: title || '' }, h('div', { class: 'stat-v' }, value), h('div', { class: 'stat-l' }, label));
}

/** The session's own cost, without sub-agents (the "session itself" row). */
function ownCost(a) {
  const t = rep.all?.agents?.[a.id];
  return { cost: t?.cost || 0, unpricedTokens: t?.unpricedTokens || 0, any: !!t };
}

function modelCostCell(model) {
  const m = rep.all?.models?.find((x) => x.model === model);
  if (!m) return h('td', { class: 'num muted' }, '—');
  if (!m.priced) return h('td', { class: 'num muted', title: 'No price for this model: add it in config "Pricing" (docs/USAGE.md). Missing, not zero.' }, 'not priced');
  return h('td', { class: 'num', title: COST_TIP }, fmtMoney(m.totals.cost, rep.all.currency));
}

function renderOver(el) {
  const r = rep.range;
  const pricing = !!r?.pricing;
  const seg = (key, options, current, onPick) => h('div', { class: 'u-seg', role: 'group' },
    options.map(([v, label]) => h('button', { type: 'button', class: 'u-seg-b' + (v === current ? ' on' : ''), 'aria-pressed': v === current ? 'true' : 'false',
      onclick: () => onPick(v) }, label)));
  const chart = h('div', { class: 'uc' });
  const head = h('div', { class: 'usage-over-head' },
    h('div', { class: 'section-title' }, 'Over time'),
    seg('range', RANGES, prefs.range, (v) => { savePref('range', v); rep.range = null; load(true); invalidate('usage-report'); }),
    pricing ? seg('metric', [['cost', 'Est. cost'], ['tokens', 'Tokens']], prefs.metric, (v) => { savePref('metric', v); invalidate('usage-report'); }) : null,
    seg('view', [['chart', 'Chart'], ['table', 'Table']], prefs.view, (v) => { savePref('view', v); invalidate('usage-report'); }),
    r ? h('span', { class: 'muted small usage-over-sum', title: pricing ? ttlNote(r.totals) : '' }, summaryLine(r, pricing),
      pricing && ttlNote(r.totals) ? ' · lower bound ⓘ' : '') : null);
  mount(el, head, chart);
  if (rep.error && !r) { mount(chart, h('div', { class: 'empty uc-empty' }, rep.error)); return; }
  if (!r) { mount(chart, h('div', { class: 'empty uc-empty' }, 'Loading…')); return; }
  renderUsageChart(chart, { report: r, order: seriesOrder(rep.all?.models), metric: prefs.metric, view: prefs.view });
}

function summaryLine(r, pricing) {
  const t = r.totals;
  const parts = [`${fmtTok(t.total)} tokens`, `${t.responses.toLocaleString()} responses`];
  if (pricing) parts.unshift(fmtMoney(t.cost, r.currency) + ' est.' + (t.unpricedTokens ? ` (+${fmtTok(t.unpricedTokens)} unpriced tokens)` : ''));
  return parts.join(' · ') + ` · per ${r.bucket}, ${r.timeZone}` + (pricing ? ` · prices as of ${r.pricesAsOf}` : '');
}

/** Usage recorded before cache lifetimes were kept is priced at the 5-minute write rate: say so, the cost is a floor. */
function ttlNote(t) {
  return t?.cacheWriteUnknownTtl > 0
    ? `${fmtTok(t.cacheWriteUnknownTtl)} cache-write tokens have no recorded cache lifetime (older usage) and are priced at the 5-minute rate; `
      + 'if they were 1-hour writes the real figure is higher.'
    : '';
}

function row(a, u, depth, max, caret = '', onCaret = null, note = null, cost = null) {
  const models = Object.keys(a.usageByModel || {});
  const total = usageTotal(u);
  const caretEl = h('span', { class: 'caret' + (caret ? '' : ' empty') }, caret || '');
  if (onCaret) caretEl.addEventListener('click', (e) => { e.stopPropagation(); onCaret(); });
  const tr = h('tr', { class: depth ? 'usage-sub' : 'usage-session', title: usageTitle(u, agentName(a)) },
    h('td', null, h('span', { class: 'usage-name', style: { paddingLeft: depth * 18 + 'px' } }, caretEl, statusDot(a), ' ',
      note ? h('span', { class: 'muted' }, note) : agentName(a))),
    h('td', null, models.map((m) => h('span', { class: 'gc-model', title: m }, shortModelName(m)))),
    cost ? costCell(cost) : null,
    ...COLS.map(([k]) => h('td', { class: 'num' }, fmtTok(u[k]))),
    h('td', { class: 'num' }, h('b', null, fmtTok(total))),
    h('td', { class: 'num' }, (u.responses || 0).toLocaleString()),
    h('td', null, h('span', { class: 'bar usage-bar' }, h('span', { class: 'bar-fill', style: { width: (100 * total / max) + '%' } }))));
  tr.addEventListener('click', () => select('agent', a.id));
  return tr;
}
