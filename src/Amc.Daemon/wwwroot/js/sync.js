// State sync: /api/state + WebSocket (/ws?since=<seq>) with exponential-backoff reconnect.
//
// Ordering guarantees:
//  * The socket connects first; on "hello" we (re)fetch /api/state while buffering patches,
//    then apply only buffered patches newer than the snapshot. Entities are therefore never
//    older than what the server has broadcast to us.
//  * Reconnects resume from the last event seq we saw, so the server replays every event we
//    missed. "resync" (gap too large / client too slow) starts over from a fresh snapshot.

import { api, wsUrl, TOKEN_MISSING } from './api.js';
import { store, invalidate, loadState, applyPatch, addEvents, clearEvents, lastEventSeq } from './store.js';
import { setAttention } from './attention-store.js';

const SEED_EVENTS = 2000;
let ws = null;
let cursor = 0;
let needSeed = true;
let patchBuffer = null;
let retryTimer = 0;
let generation = 0;
let gotHello = false;
let wsFailures = 0;
let pollTimer = 0;
let polling = false;
let pollStateAt = 0;

function setConn(state, detail = '') {
  store.conn.state = state;
  store.conn.detail = detail;
  invalidate('conn');
}

export function startSync() {
  if (TOKEN_MISSING) {
    setConn('offline', 'No API token embedded in the page — open the UI through the daemon (http://127.0.0.1:47821/)');
    return;
  }
  // First paint from REST right away; the socket's hello re-syncs (with patch buffering) afterwards.
  loadSnapshot().catch((e) => setConn('reconnecting', 'state fetch failed: ' + e.message));
  connect();
  pollHealth();
  setInterval(pollHealth, 5000);
}

export function reconnectNow() {
  clearTimeout(retryTimer);
  if (ws) { try { ws.close(); } catch { /* ignore */ } }
  else connect();
}

function connect() {
  clearTimeout(retryTimer);
  const gen = ++generation;
  if (!polling) setConn(store.conn.attempts ? 'reconnecting' : 'connecting', store.conn.attempts ? `attempt ${store.conn.attempts + 1}` : '');
  let sock;
  try {
    sock = new WebSocket(wsUrl('/ws', { since: cursor || undefined }));
  } catch (e) {
    scheduleReconnect(e.message);
    return;
  }
  ws = sock;
  gotHello = false;
  sock.onmessage = (m) => {
    if (gen !== generation) return;
    store.conn.lastMessageAt = Date.now();
    let msg;
    try { msg = JSON.parse(m.data); } catch { return; }
    handle(msg, gen);
  };
  sock.onclose = (ev) => {
    if (gen !== generation) return;
    ws = null;
    patchBuffer = null;
    if (!gotHello) { wsFailures++; startPolling(); }
    scheduleReconnect(ev.code === 1008 || ev.code === 4401 ? 'token rejected' : ev.reason || `socket closed (${ev.code})`);
  };
  sock.onerror = () => { /* onclose follows */ };
}

function scheduleReconnect(reason) {
  store.conn.attempts++;
  const base = Math.min(15000, 500 * 2 ** Math.min(store.conn.attempts - 1, 6));
  const delay = Math.round(base * (0.8 + Math.random() * 0.4));
  store.conn.nextRetryAt = Date.now() + delay;
  if (polling) setConn('polling', `WebSocket unavailable (${reason}); polling REST every 2 s`);
  else setConn('reconnecting', reason);
  retryTimer = setTimeout(connect, delay);
}

function handle(msg, gen) {
  switch (msg.t) {
    case 'hello':
      gotHello = true;
      wsFailures = 0;
      stopPolling();
      store.conn.serverSeq = msg.seq;
      store.conn.skewMs = msg.serverTime ? Date.parse(msg.serverTime) - Date.now() : 0;
      syncState(gen);
      break;
    case 'event':
      if (msg.e) {
        addEvents([msg.e], { live: !msg.replay });
        if (msg.e.seq > cursor) cursor = msg.e.seq;
      }
      break;
    case 'patch':
      if (patchBuffer) patchBuffer.push(msg);
      else applyPatch(msg.items, msg.seq);
      break;
    case 'live':
      if (Array.isArray(msg.processes)) {
        store.live.processes = msg.processes;
        store.live.at = Date.now();
        store.live.source = 'ws';
        store.live.error = null;
        invalidate('live');
      }
      break;
    case 'attention':
      setAttention(msg);
      break;
    case 'resync':
      cursor = 0;
      needSeed = true;
      clearEvents();
      setConn('reconnecting', 'server requested resync: ' + (msg.reason || ''));
      store.conn.attempts = 0;
      if (ws) ws.close();
      break;
    default:
      break;
  }
}

async function syncState(gen) {
  setConn('syncing', 'loading state');
  patchBuffer = [];
  try {
    const st = await api.state();
    if (gen !== generation) return;
    loadState(st);
    if (needSeed) {
      const seed = await api.events({ order: 'desc', limit: SEED_EVENTS, before: st.seq + 1 });
      if (gen !== generation) return;
      addEvents(seed);
      store.eventsTrimmed = seed.length >= SEED_EVENTS;
      needSeed = false;
      if (st.seq > cursor) cursor = st.seq;
    }
    const buffered = patchBuffer || [];
    patchBuffer = null;
    for (const p of buffered) if ((p.seq || 0) > st.seq) applyPatch(p.items, p.seq);
    if (lastEventSeq() > cursor) cursor = lastEventSeq();
    store.conn.attempts = 0;
    store.conn.connectedAt = Date.now();
    setConn('live');
  } catch (e) {
    patchBuffer = null;
    if (gen !== generation) return;
    setConn('reconnecting', 'state fetch failed: ' + e.message);
    if (ws) ws.close();
  }
}

// ---- REST polling fallback (used while the WebSocket cannot be established) -------------

async function loadSnapshot() {
  const st = await api.state();
  if (store.stateLoadedAt && !needSeed && store.seq >= st.seq) return; // the socket already got here first
  loadState(st);
  pollStateAt = Date.now();
  if (needSeed) {
    const seed = await api.events({ order: 'desc', limit: SEED_EVENTS, before: st.seq + 1 });
    addEvents(seed);
    store.eventsTrimmed = seed.length >= SEED_EVENTS;
    needSeed = false;
    if (st.seq > cursor) cursor = st.seq;
  }
}

function startPolling() {
  if (polling) return;
  polling = true;
  pollOnce();
}

function stopPolling() {
  polling = false;
  clearTimeout(pollTimer);
}

async function pollOnce() {
  if (!polling) return;
  try {
    if (needSeed || !store.stateLoadedAt) {
      await loadSnapshot();
    } else {
      const page = await api.events({ after: cursor, limit: 1000 });
      if (page.length) {
        addEvents(page, { live: true });
        cursor = Math.max(cursor, page[page.length - 1].seq);
      }
      // No patches without the socket: refresh entities when something happened (throttled).
      if ((page.length || Date.now() - pollStateAt > 15000) && Date.now() - pollStateAt > 1500) {
        loadState(await api.state());
        pollStateAt = Date.now();
      }
    }
    if (polling && store.conn.state !== 'polling') setConn('polling', 'WebSocket unavailable; polling REST every 2 s');
    store.conn.lastMessageAt = Date.now();
  } catch (e) {
    if (polling) setConn('reconnecting', 'polling failed: ' + e.message);
  }
  if (polling) pollTimer = setTimeout(pollOnce, 2000);
}

async function pollHealth() {
  try {
    store.health = await api.health();
    store.healthError = null;
  } catch (e) {
    store.healthError = e.message;
  }
  store.healthAt = Date.now();
  invalidate('health');
}

export { pollHealth };
