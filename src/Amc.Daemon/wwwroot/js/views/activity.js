// Activity: live structured event stream (newest first), virtualized.
import { h, mount, clock, oneLine, norm, shortId } from '../util.js';
import { store, register, get } from '../store.js';
import { ui, select, openDrawer } from '../ui.js';
import { describe, agentColor, agentLabel, evBadge } from '../model.js';
import { eventPasses, filterCtx } from '../filters.js';
import { VList } from '../components/vlist.js';

const ROW = 26;

export function eventRow(e, { showDate = false } = {}) {
  const dsc = describe(e);
  const a = e.agentId ? get('agent', e.agentId) : null;
  const who = e.agentId
    ? h('span', { class: 'ev-agent', title: e.agentId },
      h('span', { class: 'agent-swatch', style: { background: agentColor(e.agentId) } }),
      a ? oneLine(agentLabel(e.agentId), 26) : shortId(e.agentId))
    : h('span', { class: 'ev-agent muted' }, e.source || 'system');
  const detail = e.filePath && !dsc.text.includes(e.filePath) ? e.filePath : dsc.detail;
  const el = h('div', { class: 'erow' + (dsc.bad ? ' bad' : '') + (e.source === 'simulator' ? ' sim' : ''), title: `${e.type} · seq ${e.seq} · ${e.source}` },
    h('span', { class: 'ev-time mono' }, showDate ? new Date(e.ts).toLocaleDateString() + ' ' + clock(e.ts) : clock(e.ts)),
    who,
    h('span', { class: 'ev-icon ' + dsc.iconCls, 'aria-hidden': 'true' }, dsc.icon),
    h('span', { class: 'ev-text' }, dsc.text),
    detail ? h('span', { class: 'ev-detail mono' }, oneLine(detail, 160)) : null,
    h('span', { class: 'ev-meta' }, evBadge(e.evidence), e.source && e.source !== 'hook' ? h('span', { class: 'src src-' + norm(e.source) }, e.source) : null));
  el.addEventListener('click', (ev) => {
    if (ev.target.closest('.ev-agent') && e.agentId) { select('agent', e.agentId); return; }
    openDrawer('output', { event: e });
  });
  return el;
}

export function initActivity() {
  const root = document.getElementById('view-activity');
  const info = h('span', { class: 'muted small' });
  const newPill = h('button', { class: 'new-pill', hidden: true });
  const scroller = h('div', { class: 'elist' });
  const pauseT = h('label', { class: 'toggle', title: 'Freeze the list (new events are counted, not shown)' }, h('input', { type: 'checkbox' }), h('span', null, 'Pause'));
  let paused = false;
  pauseT.firstChild.addEventListener('change', (e) => { paused = e.target.checked; if (!paused) render(); });
  mount(root,
    h('div', { class: 'view-toolbar' }, h('span', { class: 'view-title' }, 'Activity'), info, h('span', { class: 'spacer' }), pauseT),
    h('div', { class: 'elist-wrap' }, newPill, scroller));

  const vl = new VList(scroller, { rowHeight: ROW, keyOf: (e) => e.seq, sigOf: () => store.stateLoadedAt, render: (e) => eventRow(e) });
  let items = [];
  let pendingNew = 0;

  newPill.addEventListener('click', () => { scroller.scrollTop = 0; pendingNew = 0; newPill.hidden = true; render(); });
  scroller.addEventListener('scroll', () => { if (scroller.scrollTop < ROW && pendingNew) { pendingNew = 0; newPill.hidden = true; } }, { passive: true });

  function render() {
    const ctx = filterCtx();
    const next = [];
    for (let i = store.events.length - 1; i >= 0; i--) {
      const e = store.events[i];
      if (eventPasses(e, ctx)) next.push(e);
    }
    // Keep the viewport steady when the user scrolled down and new events arrived on top.
    if (items.length && next.length > items.length && scroller.scrollTop > ROW) {
      const topSeq = items[0]?.seq;
      const added = next.findIndex((e) => e.seq === topSeq);
      if (added > 0) {
        scroller.scrollTop += added * ROW;
        pendingNew += added;
        newPill.hidden = false;
        newPill.textContent = `↑ ${pendingNew} new event${pendingNew === 1 ? '' : 's'}`;
      }
    }
    items = next;
    vl.setItems(items);
    info.textContent = `${items.length.toLocaleString()} of ${store.events.length.toLocaleString()} events in memory` +
      (store.eventsTrimmed ? ' · older events in Timeline' : '');
    if (!items.length) {
      scroller.dataset.empty = store.events.length ? 'No events match the current filters.' :
        'No events yet. Events stream in live from hooks, transcripts, git and process collectors. Install hooks with `agentctl hooks install`.';
    } else delete scroller.dataset.empty;
  }

  register({
    name: 'activity', deps: ['events', 'filters', 'tab', 'agent'],
    render(d) {
      if (ui.tab !== 'activity') return;
      if (paused && !d.has('filters') && !d.has('tab')) {
        info.textContent = `Paused · ${store.events.length.toLocaleString()} events in memory`;
        return;
      }
      if (d.has('agent') && !d.has('events') && !d.has('filters') && !d.has('tab')) return;
      render();
    },
  });
}
