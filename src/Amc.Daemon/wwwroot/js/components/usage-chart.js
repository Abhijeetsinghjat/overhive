// Usage over time: stacked bars per hour or day, one segment per model (top five by all-time cost, the rest "Other").
// Hand-written SVG, no chart library. Colors are CSS tokens (--u-s1..5, validated for both themes; see app.css).
// Every value is also reachable without hover: the Table toggle renders the same buckets as a table.
import { h, s, mount } from '../util.js';
import { fmtTok, shortModelName } from '../model.js';

export const SLOTS = 5;
export const OTHER = 'Other';
const HEIGHT = 220, PAD = { top: 10, right: 8, bottom: 26, left: 58 }, GAP = 2, RADIUS = 4;

/** Stable model → slot assignment: rank by all-time cost (then tokens), so a range change never repaints a model. */
export function seriesOrder(models) {
  const ranked = [...(models || [])].filter((m) => m.model)
    .sort((a, b) => (b.totals.cost || 0) - (a.totals.cost || 0) || tokTotal(b.totals) - tokTotal(a.totals));
  const map = new Map();
  ranked.slice(0, SLOTS).forEach((m, i) => map.set(m.model, i + 1));
  return map;
}

export const tokTotal = (t) => (t?.input || 0) + (t?.output || 0) + (t?.cacheWrite || 0) + (t?.cacheRead || 0);

export function fmtMoney(v, currency = 'USD') {
  if (v == null) return '—';
  if (v > 0 && v < 0.01) return '<' + fmtMoney(0.01, currency);
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency, maximumFractionDigits: v >= 1000 ? 0 : 2, minimumFractionDigits: v >= 1000 ? 0 : 2 }).format(v);
  } catch {
    return v.toFixed(2) + ' ' + currency;
  }
}

/**
 * @param {HTMLElement} root container (its width drives the chart)
 * @param {{report, order: Map<string,number>, metric: 'cost'|'tokens', view: 'chart'|'table'}} o
 */
export function renderUsageChart(root, o) {
  const { report, order, view } = o;
  const metric = report?.pricing ? o.metric : 'tokens';
  const buckets = report?.buckets || [];
  const val = (t) => (metric === 'cost' ? (t?.cost || 0) : tokTotal(t));
  const fmt = (v) => (metric === 'cost' ? fmtMoney(v, report.currency) : fmtTok(v));
  const series = seriesList(buckets, order);
  const rows = buckets.map((b) => {
    const seg = new Map();
    for (const [m, t] of Object.entries(b.byModel || {})) {
      const key = order.has(m) ? m : OTHER;
      seg.set(key, (seg.get(key) || 0) + val(t));
    }
    return { start: b.start, total: val(b.totals), seg, totals: b.totals };
  });

  if (!rows.some((r) => r.total > 0)) {
    mount(root, h('div', { class: 'empty uc-empty' }, h('div', { class: 'empty-title' }, 'No usage in this range'), 'Bars appear as agents produce model responses.'));
    return;
  }
  const legend = h('div', { class: 'uc-legend', role: 'list', 'aria-label': 'Models' },
    series.map((m) => h('span', { class: 'uc-key', role: 'listitem', title: m === OTHER ? 'All other models' : m },
      h('span', { class: 'uc-swatch', style: { background: color(m, order) } }), m === OTHER ? OTHER : shortModelName(m))));

  if (view === 'table') {
    mount(root, legend, table(rows, series, report, fmt, metric));
    return;
  }

  const width = Math.max(320, Math.floor(root.clientWidth || 640));
  const plotW = width - PAD.left - PAD.right, plotH = HEIGHT - PAD.top - PAD.bottom;
  const max = niceMax(Math.max(...rows.map((r) => r.total)));
  const step = plotW / rows.length;
  const barW = Math.max(1, Math.min(28, step * 0.72));
  const y = (v) => PAD.top + plotH - (v / max) * plotH;

  const grid = [];
  for (let i = 0; i <= 4; i++) {
    const v = (max / 4) * i, yy = Math.round(y(v)) + 0.5;
    grid.push(s('line', { class: 'uc-grid', x1: PAD.left, x2: width - PAD.right, y1: yy, y2: yy }));
    grid.push(s('text', { class: 'uc-axis', x: PAD.left - 6, y: yy + 3.5, 'text-anchor': 'end' }, fmt(v)));
  }

  const tip = h('div', { class: 'uc-tip', hidden: true, role: 'status' });
  const marks = [], hits = [], labels = [];
  const every = Math.max(1, Math.ceil(rows.length / Math.max(2, Math.floor(plotW / 64))));
  rows.forEach((r, i) => {
    const x = PAD.left + i * step + (step - barW) / 2;
    let base = PAD.top + plotH;
    const parts = series.filter((m) => r.seg.get(m) > 0);
    parts.forEach((m, k) => {
      const hgt = (r.seg.get(m) / max) * plotH;
      const top = k === parts.length - 1;
      const drawH = Math.max(0, hgt - (k > 0 ? GAP : 0)); // the gap sits below every segment but the first
      if (drawH >= 0.5) marks.push(top ? roundTop(x, base - hgt, barW, drawH, m, order) : s('rect', { x, y: base - hgt, width: barW, height: drawH, fill: color(m, order) }));
      base -= hgt;
    });
    const label = bucketLabel(r.start, report.bucket, report.timeZone);
    const hit = s('rect', {
      class: 'uc-hit', x: PAD.left + i * step, y: PAD.top, width: step, height: plotH, tabindex: 0,
      'aria-label': `${label.long}: ${fmt(r.total)}`,
    });
    const show = () => { showTip(tip, r, series, order, fmt, label.long, metric, PAD.left + i * step, step, width); hit.classList.add('on'); };
    const hide = () => { tip.hidden = true; hit.classList.remove('on'); };
    hit.addEventListener('pointerenter', show);
    hit.addEventListener('pointerleave', hide);
    hit.addEventListener('focus', show);
    hit.addEventListener('blur', hide);
    hits.push(hit);
    if (i % every === 0) labels.push(s('text', { class: 'uc-axis', x: PAD.left + i * step + step / 2, y: HEIGHT - 8, 'text-anchor': 'middle' }, label.short));
  });

  const svg = s('svg', { class: 'uc-svg', width, height: HEIGHT, viewBox: `0 0 ${width} ${HEIGHT}`, role: 'img',
    'aria-label': `${metric === 'cost' ? 'Estimated API cost' : 'Tokens'} per ${report.bucket}, stacked by model` },
    grid, s('line', { class: 'uc-base', x1: PAD.left, x2: width - PAD.right, y1: PAD.top + plotH + 0.5, y2: PAD.top + plotH + 0.5 }),
    marks, labels, hits);
  mount(root, legend, h('div', { class: 'uc-plot' }, svg, tip));
}

function seriesList(buckets, order) {
  const seen = new Set();
  for (const b of buckets) for (const m of Object.keys(b.byModel || {})) seen.add(order.has(m) ? m : OTHER);
  return [...seen].sort((a, b) => (order.get(a) || 99) - (order.get(b) || 99));
}

const color = (m, order) => (order.has(m) ? `var(--u-s${order.get(m)})` : 'var(--u-other)');

/** A bar's top segment: 4px rounded top corners, square at the bottom (anchored to the stack below). */
function roundTop(x, y, w, hgt, m, order) {
  const r = Math.min(RADIUS, w / 2, hgt);
  const d = `M${x},${y + hgt}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + hgt}Z`;
  return s('path', { d, fill: color(m, order) });
}

function niceMax(v) {
  if (!(v > 0)) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  for (const f of [1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]) if (f * p >= v) return f * p;
  return 10 * p;
}

function bucketLabel(start, bucket, timeZone) {
  const d = new Date(start);
  const opt = (o) => { try { return new Intl.DateTimeFormat(undefined, { timeZone, ...o }).format(d); } catch { return new Intl.DateTimeFormat(undefined, o).format(d); } };
  if (bucket === 'hour') {
    const hm = opt({ hour: '2-digit', minute: '2-digit' });
    const day = opt({ month: 'short', day: 'numeric' });
    const isMidnight = opt({ hour: '2-digit', hourCycle: 'h23' }) === '00';
    return { short: isMidnight ? day : hm, long: `${opt({ weekday: 'short', month: 'short', day: 'numeric' })}, ${hm}` };
  }
  return { short: opt({ month: 'short', day: 'numeric' }), long: opt({ weekday: 'short', year: 'numeric', month: 'short', day: 'numeric' }) };
}

/** Inside the plot, beside the hovered column (right of it, or left when it would overflow), never over the controls. */
function showTip(tip, r, series, order, fmt, title, metric, x0, step, width) {
  const lines = series.filter((m) => r.seg.get(m) > 0).reverse().map((m) =>
    h('div', { class: 'uc-tip-row' }, h('span', { class: 'uc-tip-key', style: { background: color(m, order) } }),
      h('b', null, fmt(r.seg.get(m))), ' ', h('span', { class: 'muted' }, m === OTHER ? OTHER : shortModelName(m))));
  const unpriced = metric === 'cost' && r.totals?.unpricedTokens > 0
    ? h('div', { class: 'uc-tip-note' }, `+ ${fmtTok(r.totals.unpricedTokens)} tokens from models without a price (not in the total)`) : null;
  mount(tip, h('div', { class: 'uc-tip-title' }, title), h('div', { class: 'uc-tip-total' }, h('b', null, fmt(r.total)), ' total'), lines, unpriced);
  tip.hidden = false;
  const w = tip.offsetWidth || 200;
  const right = x0 + step + 8;
  tip.style.left = (right + w <= width ? right : Math.max(0, x0 - 8 - w)) + 'px';
  tip.style.top = PAD.top + 'px';
}

function table(rows, series, report, fmt, metric) {
  return h('div', { class: 'uc-table-wrap' }, h('table', { class: 'ptable usage-table uc-table' },
    h('thead', null, h('tr', null, h('th', null, report.bucket === 'hour' ? 'Hour' : 'Day'), h('th', { class: 'num' }, 'Total'),
      series.map((m) => h('th', { class: 'num', title: m }, m === OTHER ? OTHER : shortModelName(m))),
      metric === 'cost' ? h('th', { class: 'num', title: 'Tokens from models without a price, not included in the cost' }, 'Unpriced tokens') : null)),
    h('tbody', null, rows.filter((r) => r.total > 0 || r.totals?.unpricedTokens > 0).reverse().map((r) =>
      h('tr', null, h('td', null, bucketLabel(r.start, report.bucket, report.timeZone).long), h('td', { class: 'num' }, h('b', null, fmt(r.total))),
        series.map((m) => h('td', { class: 'num' }, r.seg.get(m) ? fmt(r.seg.get(m)) : '')),
        metric === 'cost' ? h('td', { class: 'num' }, r.totals?.unpricedTokens ? fmtTok(r.totals.unpricedTokens) : '') : null)))));
}
