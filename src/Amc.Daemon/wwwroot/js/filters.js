// Global filter predicates shared by tree, activity, timeline, graph and worktree views.
import { ui } from './ui.js';
import { get } from './store.js';
import { statusMeta, descendants, describe, typeMatches } from './model.js';

export const RANGES = [
  ['', 'All time'], ['5m', 'Last 5 min'], ['15m', 'Last 15 min'], ['1h', 'Last hour'], ['6h', 'Last 6 hours'], ['24h', 'Last 24 hours'],
];
const RANGE_MS = { '5m': 300e3, '15m': 900e3, '1h': 3600e3, '6h': 6 * 3600e3, '24h': 24 * 3600e3 };

export function fromMs() {
  const r = RANGE_MS[ui.filters.range];
  return r ? Date.now() - r : null;
}

/** Snapshot of derived filter data for one render pass. */
export function filterCtx() {
  const f = ui.filters;
  let agentSet = null;
  if (f.agent) {
    const a = get('agent', f.agent);
    agentSet = new Set([f.agent, ...(a ? descendants(a).map((x) => x.id) : [])]);
  }
  return { f, agentSet, from: fromMs(), q: f.q.trim().toLowerCase() };
}

function worktreeMatches(wtId, f) {
  if (!f.worktree && !f.branch && !f.repo) return true;
  const wt = wtId ? get('worktree', wtId) : null;
  if (f.worktree && wtId !== f.worktree) return false;
  if (f.branch && (!wt || wt.branch !== f.branch)) return false;
  if (f.repo && (!wt || wt.repositoryId !== f.repo)) return false;
  return true;
}

export function agentPasses(a, ctx = filterCtx()) {
  const { f, agentSet, q } = ctx;
  if (agentSet && !agentSet.has(a.id)) return false;
  if (f.status && statusMeta(a).group !== f.status) return false;
  if (f.repo && !(a.repositories && a.repositories[f.repo]) && a.currentRepositoryId !== f.repo) return false;
  if (f.worktree && !(a.worktrees && a.worktrees[f.worktree])) return false;
  if (f.branch) {
    const wts = Object.keys(a.worktrees || {}).map((id) => get('worktree', id)).filter(Boolean);
    if (a.gitBranch !== f.branch && !wts.some((w) => w.branch === f.branch)) return false;
  }
  if (q) {
    const hay = [a.id, a.title, a.description, a.agentType, a.lastPrompt, a.currentFile, a.currentAction, a.cwd, a.gitBranch]
      .filter(Boolean).join(' ').toLowerCase();
    if (!hay.includes(q)) return false;
  }
  return true;
}

export function eventPasses(e, ctx = filterCtx()) {
  const { f, agentSet, from, q } = ctx;
  if (agentSet && !agentSet.has(e.agentId)) return false;
  if (f.type && !typeMatches(e.type, f.type)) return false;
  if (from && Date.parse(e.ts) < from) return false;
  if (f.repo && e.repositoryId !== f.repo) {
    const wt = e.worktreeId && get('worktree', e.worktreeId);
    if (!wt || wt.repositoryId !== f.repo) return false;
  }
  if ((f.worktree || f.branch) && !worktreeMatches(e.worktreeId, { worktree: f.worktree, branch: f.branch })) return false;
  if (f.status) {
    const a = e.agentId && get('agent', e.agentId);
    if (!a || statusMeta(a).group !== f.status) return false;
  }
  if (q) {
    const d = e.data || {};
    const hay = [e.type, e.agentId, e.filePath, d.command, d.summary, d.text, d.tool, describe(e).text].filter(Boolean).join(' ').toLowerCase();
    if (!hay.includes(q)) return false;
  }
  return true;
}

export function filePasses(fc, ctx = filterCtx()) {
  const { f, agentSet, q } = ctx;
  if (!worktreeMatches(fc.worktreeId, f)) return false;
  if (agentSet && !Object.keys(fc.agents || {}).some((id) => agentSet.has(id))) return false;
  if (q && !fc.path.toLowerCase().includes(q) && !Object.keys(fc.agents || {}).some((id) => id.toLowerCase().includes(q))) return false;
  return true;
}

export function worktreePasses(wt, ctx = filterCtx()) {
  const { f } = ctx;
  if (f.repo && wt.repositoryId !== f.repo) return false;
  if (f.worktree && wt.id !== f.worktree) return false;
  if (f.branch && wt.branch !== f.branch) return false;
  return true;
}

/** Server-side query parameters equivalent to the current filters (used by timeline paging). */
export function serverParams() {
  const f = ui.filters;
  const from = fromMs();
  return {
    agent: f.agent || undefined,
    descendants: f.agent ? 'true' : undefined,
    repo: f.repo || undefined,
    worktree: f.worktree || undefined,
    type: f.type || undefined,
    from: from ? new Date(from).toISOString() : undefined,
    q: f.q.trim() || undefined,
  };
}
