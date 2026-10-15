// Normalized client-side store + per-frame render scheduler.
//
// Entities are keyed by (kind, id) exactly like the daemon's patches. Events are kept in a
// bounded, seq-ordered buffer; older events are paged from /api/events on demand.

export const KINDS = ['agent', 'repository', 'worktree', 'file', 'message', 'commit', 'test', 'terminal', 'conflict', 'guard'];

const COLLECTIONS = {
  agents: 'agent', repositories: 'repository', worktrees: 'worktree', files: 'file', messages: 'message',
  commits: 'commit', testRuns: 'test', terminals: 'terminal', conflicts: 'conflict', guardAlerts: 'guard',
};

export const MAX_EVENTS = 5000;

export const store = {
  seq: 0,
  clock: null,
  e: Object.fromEntries(KINDS.map((k) => [k, new Map()])),
  events: [],            // ascending by seq, bounded
  eventSeqs: new Set(),
  eventsTrimmed: false,  // true once older events were evicted (timeline pages from the server)
  lastEventAt: 0,        // wall clock of the last event received over the socket
  lastTool: new Map(),   // agentId → latest tool./command. event
  reads: new Map(),      // fileKey → Map(agentId → ts) from file.read events
  simulated: false,
  live: { processes: null, at: 0, error: null, source: null },
  health: null,
  healthError: null,
  healthAt: 0,
  conn: { state: 'connecting', detail: '', attempts: 0, nextRetryAt: 0, lastMessageAt: 0, connectedAt: 0 },
  stateLoadedAt: 0,
};

// ---- scheduler -----------------------------------------------------------------

const views = [];
const dirty = new Set();
let scheduled = false;

/** Register a view: deps is a list of topics; render(dirtySet) runs at most once per frame. */
export function register(view) { views.push(view); }

export function invalidate(...topics) {
  for (const t of topics) dirty.add(t);
  if (!scheduled) {
    scheduled = true;
    requestAnimationFrame(flush);
  }
}

function flush() {
  scheduled = false;
  const d = new Set(dirty);
  dirty.clear();
  for (const v of views) {
    if (d.has('*') || v.deps.includes('*') || v.deps.some((x) => d.has(x))) {
      try { v.render(d); } catch (err) { console.error(`render ${v.name} failed`, err); }
    }
  }
}

// ---- entities ------------------------------------------------------------------

export const get = (kind, id) => store.e[kind]?.get(id);
export const all = (kind) => [...store.e[kind].values()];

export function idOf(kind, v) {
  if (kind === 'file' || kind === 'conflict' || kind === 'guard') return v.key;
  if (kind === 'commit') return v.sha;
  return v.id;
}

export function loadState(state) {
  for (const [coll, kind] of Object.entries(COLLECTIONS)) {
    const m = store.e[kind];
    m.clear();
    for (const [id, v] of Object.entries(state[coll] || {})) m.set(id, v);
  }
  store.seq = state.seq || 0;
  store.clock = state.clock;
  store.stateLoadedAt = Date.now();
  invalidate(...KINDS);
}

export function applyPatch(items, seq) {
  const kinds = new Set();
  for (const it of items || []) {
    const m = store.e[it.kind];
    if (!m) continue;
    if (it.value == null) m.delete(it.id);
    else m.set(it.id, it.value);
    kinds.add(it.kind);
  }
  if (seq > store.seq) store.seq = seq;
  invalidate(...kinds);
}

// ---- events --------------------------------------------------------------------

const eventListeners = [];
export function onEvent(fn) { eventListeners.push(fn); }

function index(e) {
  const t = e.type || '';
  if (e.agentId && (t.startsWith('tool.') || t.startsWith('command.'))) {
    const prev = store.lastTool.get(e.agentId);
    if (!prev || prev.seq < e.seq) store.lastTool.set(e.agentId, e);
  }
  if (t === 'file.read' && e.worktreeId && e.filePath) {
    const key = e.worktreeId + '|' + e.filePath;
    let m = store.reads.get(key);
    if (!m) store.reads.set(key, (m = new Map()));
    if (e.agentId) m.set(e.agentId, e.ts);
  }
  if (e.source === 'simulator' && !store.simulated) {
    store.simulated = true;
    invalidate('simulated');
  }
}

/** Adds events (any order); duplicates by seq are ignored. Returns the number added. */
export function addEvents(list, { live = false } = {}) {
  let added = 0;
  const fresh = [];
  for (const e of list) {
    if (!e || store.eventSeqs.has(e.seq)) continue;
    store.eventSeqs.add(e.seq);
    const arr = store.events;
    if (!arr.length || arr[arr.length - 1].seq < e.seq) arr.push(e);
    else {
      let lo = 0, hi = arr.length;
      while (lo < hi) { const mid = (lo + hi) >> 1; if (arr[mid].seq < e.seq) lo = mid + 1; else hi = mid; }
      arr.splice(lo, 0, e);
    }
    index(e);
    fresh.push(e);
    added++;
  }
  if (store.events.length > MAX_EVENTS) {
    const drop = store.events.splice(0, store.events.length - MAX_EVENTS + 250);
    for (const e of drop) store.eventSeqs.delete(e.seq);
    store.eventsTrimmed = true;
  }
  if (added) {
    if (live) store.lastEventAt = Date.now();
    for (const fn of eventListeners) fn(fresh, live);
    invalidate('events');
  }
  return added;
}

export function clearEvents() {
  store.events = [];
  store.eventSeqs.clear();
  store.lastTool.clear();
  store.reads.clear();
  store.eventsTrimmed = false;
  invalidate('events');
}

export function lastEventSeq() {
  return store.events.length ? store.events[store.events.length - 1].seq : 0;
}
