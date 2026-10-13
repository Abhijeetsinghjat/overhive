// Health: the observer observes itself. Collector state, queue depth, drops, db, websocket — stalls must be obvious.
import { h, mount, ago, norm, dateTime, agoEl } from '../util.js';
import { store, register } from '../store.js';
import { ui } from '../ui.js';
import { pollHealth, reconnectNow } from '../sync.js';
import { counts } from '../model.js';

const STATE_CLS = { ok: 'hs-ok', degraded: 'hs-degraded', failed: 'hs-failed', starting: 'hs-starting', disabled: 'hs-disabled' };

export function initHealth() {
  const root = document.getElementById('view-health');
  const body = h('div', { class: 'health' });
  const refresh = h('button', { class: 'btn btn-sm' }, 'Refresh');
  refresh.addEventListener('click', pollHealth);
  mount(root, h('div', { class: 'view-toolbar' }, h('span', { class: 'view-title' }, 'System health'), h('span', { class: 'spacer' }), refresh), body);

  register({
    name: 'health', deps: ['health', 'tab', 'conn', 'tick'],
    render() {
      if (ui.tab !== 'health') return;
      const hl = store.health;
      const now = Date.now();
      const problems = [];
      if (store.healthError) problems.push(['failed', `Daemon health endpoint unreachable: ${store.healthError}`]);
      if (store.conn.state === 'polling') problems.push(['degraded', `Live stream unavailable — ${store.conn.detail}. Updates arrive with up to 2 s delay; live process metrics are not streamed.`]);
      else if (store.conn.state !== 'live') problems.push(['degraded', `Live stream ${store.conn.state}${store.conn.detail ? ': ' + store.conn.detail : ''} — the UI is not receiving updates.`]);
      const c = counts();
      const quiet = store.lastEventAt ? now - store.lastEventAt : (store.conn.connectedAt ? now - store.conn.connectedAt : 0);
      if ((store.conn.state === 'live' || store.conn.state === 'polling') && c.active > 0 && quiet > 120000)
        problems.push(['degraded', `No events received for ${ago(now - quiet)} while ${c.active} agent(s) are marked active — a collector may have stalled.`]);
      if (hl) {
        for (const comp of hl.components || []) {
          const st = norm(comp.state);
          if (st === 'failed' || st === 'degraded') problems.push([st, `${comp.name}: ${comp.detail || st}`]);
        }
        if (hl.queue?.dropped) problems.push(['degraded', `${hl.queue.dropped} event(s) dropped because the ingest queue was full.`]);
        if (hl.database && !hl.database.ok) problems.push(['failed', 'Database ping failed.']);
      }
      const overall = store.healthError ? 'failed' : problems.some((p) => p[0] === 'failed') ? 'failed' : problems.length ? 'degraded' : norm(hl?.overall) || 'starting';

      const stat = (label, value, sub, cls = '') => h('div', { class: 'stat ' + cls }, h('div', { class: 'stat-v' }, value), h('div', { class: 'stat-l' }, label), sub ? h('div', { class: 'stat-s muted' }, sub) : null);
      const reconnect = h('button', { class: 'btn btn-sm' }, 'Reconnect');
      reconnect.addEventListener('click', reconnectNow);

      mount(body,
        h('div', { class: `health-banner ${STATE_CLS[overall] || ''}` },
          h('span', { class: 'hb-state' }, overall === 'ok' ? '✓ Monitoring healthy' : overall === 'failed' ? '✕ Monitoring failing' : overall === 'starting' ? '◌ Starting' : '! Monitoring degraded'),
          h('span', { class: 'muted small' }, store.healthAt ? `checked ${ago(store.healthAt)} ago` : 'not checked yet'),
          problems.length ? h('ul', { class: 'hb-problems' }, problems.map(([st, t]) => h('li', { class: STATE_CLS[st] }, t))) : null),
        h('div', { class: 'stats' },
          stat('Live stream', store.conn.state, store.conn.lastMessageAt ? `last message ${ago(store.conn.lastMessageAt)} ago` : '—', store.conn.state === 'live' ? 'ok' : 'warn'),
          stat('Last event', store.lastEventAt ? ago(store.lastEventAt) + ' ago' : '—', 'received live over WebSocket'),
          stat('Ingest queue', hl ? String(hl.queue?.depth ?? '—') : '—', 'events waiting', hl?.queue?.depth > 1000 ? 'warn' : ''),
          stat('Dropped events', hl ? String(hl.queue?.dropped ?? 0) : '—', 'ingest queue overflow', hl?.queue?.dropped ? 'bad' : ''),
          stat('Database', hl ? (hl.database?.ok ? 'ok' : 'FAILED') : '—', hl ? `${(hl.database?.events ?? 0).toLocaleString()} events stored` : '', hl && !hl.database?.ok ? 'bad' : ''),
          stat('WebSocket clients', hl ? String(hl.websocket?.clients ?? '—') : '—', 'connected browsers / tools'),
          stat('Event seq', String(hl?.seq ?? store.seq), `UI at seq ${store.seq}`),
          stat('UI buffer', store.events.length.toLocaleString(), store.eventsTrimmed ? 'oldest evicted (paged on demand)' : 'events in memory')),
        h('div', { class: 'section-title' }, 'Collectors & components'),
        hl && hl.components?.length ? h('div', { class: 'comp-grid' }, hl.components.map(compCard)) :
          h('div', { class: 'empty' }, store.healthError ? 'Health endpoint unreachable.' : 'No components reported yet.'),
        h('div', { class: 'section-title' }, 'Daemon'),
        h('table', { class: 'kv' },
          h('tr', null, h('td', null, 'Version'), h('td', { class: 'mono' }, hl?.version || '—')),
          h('tr', null, h('td', null, 'Server time'), h('td', { class: 'mono' }, hl?.serverTime ? dateTime(hl.serverTime) : '—',
            store.conn.skewMs && Math.abs(store.conn.skewMs) > 2000 ? h('span', { class: 'bad' }, ` (clock skew ${Math.round(store.conn.skewMs / 1000)}s)`) : null)),
          h('tr', null, h('td', null, 'State loaded'), h('td', null, store.stateLoadedAt ? agoEl(new Date(store.stateLoadedAt).toISOString()) : '—')),
          h('tr', null, h('td', null, 'Connection'), h('td', null, `${store.conn.state}${store.conn.detail ? ' — ' + store.conn.detail : ''} `, reconnect))));
    },
  });
}

function compCard(c) {
  const st = norm(c.state);
  const last = c.lastSuccessAt ? Date.parse(c.lastSuccessAt) : 0;
  return h('div', { class: `comp ${STATE_CLS[st] || ''}` },
    h('div', { class: 'comp-head' }, h('span', { class: 'comp-dot' }), h('b', null, c.name), h('span', { class: 'spacer' }), h('span', { class: 'comp-state' }, st)),
    h('div', { class: 'comp-detail' }, c.detail || '—'),
    h('div', { class: 'small muted' }, 'last success ', last ? agoEl(c.lastSuccessAt) : 'never', ' · updated ', agoEl(c.updatedAt)),
    Object.keys(c.counters || {}).length ? h('div', { class: 'counters' },
      Object.entries(c.counters).map(([k, v]) => h('span', { class: 'counter' }, h('span', { class: 'muted' }, k), ' ', h('b', null, Number(v).toLocaleString())))) : null);
}
