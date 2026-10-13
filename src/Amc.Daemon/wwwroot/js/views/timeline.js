// Timeline: global, filterable, virtualized, grouped by minute; pages older events from /api/events.
import { h, s, mount, clock } from '../util.js';
import { store, register, onEvent } from '../store.js';
import { ui } from '../ui.js';
import { api } from '../api.js';
import { describe } from '../model.js';
import { eventPasses, filterCtx, serverParams } from '../filters.js';
import { VList } from '../components/vlist.js';
import { eventRow } from './activity.js';

const ROW = 26, PAGE = 500;

export function initTimeline() {
  const root = document.getElementById('view-timeline');
  const info = h('span', { class: 'muted small' });
  const hist = h('div', { class: 'histo' });
  const scroller = h('div', { class: 'elist' });
  const status = h('div', { class: 'tl-status small muted' });
  const loadBtn = h('button', { class: 'btn btn-sm' }, 'Load older');
  mount(root,
    h('div', { class: 'view-toolbar' }, h('span', { class: 'view-title' }, 'Timeline'), info, h('span', { class: 'spacer' }), loadBtn),
    hist,
    h('div', { class: 'elist-wrap' }, scroller),
    status);

  const fetched = new Map();   // seq → event fetched from the server for the current filters
  let exhausted = false, loading = false, gen = 0, dirty = true, error = null;

  const vl = new VList(scroller, {
    rowHeight: ROW,
    keyOf: (r) => r.hdr ? r.key : r.seq,
    sigOf: (r) => r.hdr ? String(r.count) : String(store.stateLoadedAt),
    render: (r) => r.hdr
      ? h('div', { class: 'tl-hdr' }, h('span', { class: 'mono' }, r.label), h('span', { class: 'muted' }, `${r.count} event${r.count === 1 ? '' : 's'}`), r.bad ? h('span', { class: 'bad' }, `${r.bad} problem${r.bad === 1 ? '' : 's'}`) : null)
      : eventRow(r, { showDate: false }),
    onRange: (first, last, n) => { if (ui.tab === 'timeline' && n && last >= n - 30) loadOlder(); },
  });

  loadBtn.addEventListener('click', () => loadOlder(true));

  async function loadOlder(force = false) {
    if (loading || (exhausted && !force)) return;
    loading = true;
    const myGen = gen;
    const oldest = rows.length ? rows[rows.length - 1].seq : undefined;
    status.textContent = 'Loading older events…';
    try {
      const page = await api.events({ ...serverParams(), order: 'desc', limit: PAGE, before: oldest });
      if (myGen !== gen) return;
      for (const e of page) fetched.set(e.seq, e);
      exhausted = page.length < PAGE;
      error = null;
    } catch (e) {
      error = e.message;
    } finally {
      loading = false;
      if (myGen === gen) { dirty = true; render(); }
    }
  }

  let rows = [];
  function render() {
    const ctx = filterCtx();
    const bySeq = new Map();
    for (const e of store.events) if (eventPasses(e, ctx)) bySeq.set(e.seq, e);
    for (const [seq, e] of fetched) if (!bySeq.has(seq) && eventPasses(e, ctx)) bySeq.set(seq, e);
    const evs = [...bySeq.values()].sort((a, b) => b.seq - a.seq);
    rows = [];
    let curMin = null, hdr = null;
    const buckets = new Map();
    for (const e of evs) {
      const t = Date.parse(e.ts);
      const m = Math.floor(t / 60000);
      if (m !== curMin) {
        curMin = m;
        const d = new Date(m * 60000);
        hdr = { hdr: true, key: 'm' + m + ':' + rows.length, label: `${d.toLocaleDateString()} ${clock(d.getTime()).slice(0, 5)}`, count: 0, bad: 0 };
        rows.push(hdr);
      }
      hdr.count++;
      const bad = describe(e).bad;
      if (bad) hdr.bad++;
      rows.push(e);
      const b = buckets.get(m) || { n: 0, bad: 0 };
      b.n++; if (bad) b.bad++;
      buckets.set(m, b);
    }
    vl.setItems(rows);
    info.textContent = `${evs.length.toLocaleString()} events loaded${exhausted ? ' (complete)' : ''}`;
    status.textContent = error ? `Could not load older events: ${error}` : exhausted ? 'Beginning of the log for these filters.' : loading ? 'Loading…' : 'Scroll down to load older events.';
    loadBtn.disabled = exhausted || loading;
    renderHisto(hist, buckets);
    if (!evs.length) scroller.dataset.empty = loading ? 'Loading…' : 'No events for these filters.'; else delete scroller.dataset.empty;
    dirty = false;
  }

  onEvent(() => { dirty = true; });

  register({
    name: 'timeline', deps: ['events', 'filters', 'tab'],
    render(d) {
      if (d.has('filters')) { gen++; fetched.clear(); exhausted = false; dirty = true; }
      if (ui.tab !== 'timeline') return;
      if (dirty || d.has('tab')) render();
      if (d.has('filters') || (d.has('tab') && !fetched.size && !exhausted)) loadOlder();
    },
  });
}

function renderHisto(el, buckets) {
  if (!buckets.size) { el.replaceChildren(); return; }
  const keys = [...buckets.keys()];
  const max = Math.max(...keys), min = Math.max(Math.min(...keys), max - 119);
  const n = max - min + 1;
  const peak = Math.max(1, ...[...buckets.values()].map((b) => b.n));
  const W = 1000, Hh = 36, bw = W / n;
  const bars = [];
  for (let m = min; m <= max; m++) {
    const b = buckets.get(m);
    if (!b) continue;
    const hgt = Math.max(1.5, (b.n / peak) * (Hh - 4));
    const x = (m - min) * bw;
    const r = s('rect', { x: x + 0.5, y: Hh - hgt, width: Math.max(1, bw - 1), height: hgt, class: 'hb' });
    r.appendChild(s('title', null, `${clock(m * 60000).slice(0, 5)} — ${b.n} events${b.bad ? `, ${b.bad} problems` : ''}`));
    bars.push(r);
    if (b.bad) bars.push(s('rect', { x: x + 0.5, y: Hh - Math.max(1.5, (b.bad / peak) * (Hh - 4)), width: Math.max(1, bw - 1), height: Math.max(1.5, (b.bad / peak) * (Hh - 4)), class: 'hb-bad' }));
  }
  mount(el, s('svg', { viewBox: `0 0 ${W} ${Hh}`, preserveAspectRatio: 'none', class: 'histo-svg' }, ...bars),
    h('div', { class: 'histo-axis small muted' }, h('span', null, clock(min * 60000).slice(0, 5)), h('span', null, 'events per minute (loaded range, max 2 h)'), h('span', null, clock(max * 60000).slice(0, 5))));
}
