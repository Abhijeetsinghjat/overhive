// Domain presentation: status/evidence vocabulary, agent hierarchy helpers, event summaries.
import { store, get, all } from './store.js';
import { norm, shortId, oneLine, basename, ms, duration, h } from './util.js';

// Distinct colour + glyph + label per status (never colour alone).
export const STATUS = {
  running:      { label: 'Active',       glyph: '●', cls: 'st-running', group: 'active' },
  starting:     { label: 'Starting',     glyph: '◌', cls: 'st-running', group: 'active' },
  created:      { label: 'Created',      glyph: '○', cls: 'st-unknown', group: 'unknown' },
  waiting:      { label: 'Waiting',      glyph: '◷', cls: 'st-waiting', group: 'waiting' },
  blocked:      { label: 'Blocked',      glyph: '!', cls: 'st-blocked', group: 'blocked' },
  paused:       { label: 'Paused',       glyph: '‖', cls: 'st-paused',  group: 'waiting' },
  completed:    { label: 'Completed',    glyph: '✓', cls: 'st-completed', group: 'completed' },
  cancelled:    { label: 'Cancelled',    glyph: '⊘', cls: 'st-completed', group: 'completed' },
  failed:       { label: 'Failed',       glyph: '✕', cls: 'st-failed',  group: 'failed' },
  disconnected: { label: 'Disconnected', glyph: '⌁', cls: 'st-disconnected', group: 'disconnected' },
  unknown:      { label: 'Unknown',      glyph: '?', cls: 'st-unknown', group: 'unknown' },
};

export const STATUS_DESCRIPTIONS = {
  running: 'Live evidence of work: process alive and a recent tool event or busy flag.',
  waiting: 'Idle at its prompt or waiting for sub-agents.',
  blocked: 'Waiting for you: permission prompt or notification.',
  paused: 'Stopped by Overhive (SIGSTOP).',
  completed: 'Ended normally (SessionEnd / SubagentStop).',
  failed: 'Ended with an error.',
  cancelled: 'Stopped by the user via Overhive.',
  disconnected: 'Process vanished without an end event.',
  unknown: 'No live evidence (historical session).',
  created: 'Known, but no activity yet.',
  starting: 'Starting up.',
};

export const statusOf = (a) => norm(a?.status) || 'unknown';
export const statusMeta = (a) => STATUS[statusOf(a)] || STATUS.unknown;
export const isTerminal = (a) => ['completed', 'failed', 'cancelled', 'disconnected'].includes(statusOf(a));
export const isActive = (a) => ['starting', 'running', 'waiting', 'blocked', 'paused'].includes(statusOf(a));

export function statusPill(a, { short = false } = {}) {
  const m = statusMeta(a);
  const st = statusOf(a);
  return h('span', { class: `pill ${m.cls}`, title: `${m.label}: ${STATUS_DESCRIPTIONS[st] || ''}` },
    h('span', { class: 'glyph', 'aria-hidden': 'true' }, m.glyph), short ? null : m.label);
}

export function statusDot(a) {
  const m = statusMeta(a);
  return h('span', { class: `sdot ${m.cls}`, title: m.label, 'aria-label': m.label }, m.glyph);
}

// ---- evidence ------------------------------------------------------------------

export const EVIDENCE = {
  observed: { label: 'observed', cls: 'ev-observed', tip: 'Observed: seen directly by the OS, Git or the filesystem.' },
  reported: { label: 'reported', cls: 'ev-reported', tip: 'Reported: stated by the agent itself (hook payload, transcript, tool input).' },
  inferred: { label: 'inferred', cls: 'ev-inferred', tip: 'Inferred: derived by correlation; may be wrong.' },
};

export function evBadge(ev, extraTip = '') {
  const m = EVIDENCE[norm(ev)] || { label: norm(ev) || '?', cls: 'ev-unknown', tip: 'Unknown evidence level' };
  return h('span', { class: `ev ${m.cls}`, title: m.tip + (extraTip ? '\n' + extraTip : '') }, m.label);
}

// ---- colours -----------------------------------------------------------------

const PALETTE = ['#4ea1ff', '#f2a93b', '#b67dff', '#3ccf91', '#ff6b8b', '#36c5d6', '#e8d44d', '#ff8f52', '#8aa2ff', '#9be15d', '#e07ad8', '#5ad1b4'];
const colorCache = new Map();
let colorOrder = null;

export function agentColor(id) {
  if (!id) return '#7d8590';
  let c = colorCache.get(id);
  if (c) return c;
  // Assign in creation order so siblings get distinct hues.
  if (!colorOrder || !colorOrder.has(id)) {
    colorOrder = new Map(all('agent').sort((a, b) => ms(a.createdAt) - ms(b.createdAt) || (a.id < b.id ? -1 : 1)).map((a, i) => [a.id, i]));
  }
  const i = colorOrder.get(id);
  c = PALETTE[(i ?? [...id].reduce((x, ch) => x + ch.charCodeAt(0), 0)) % PALETTE.length];
  colorCache.set(id, c);
  return c;
}

// ---- agents --------------------------------------------------------------------

export function agentName(a) {
  if (!a) return '';
  if (norm(a.kind) === 'main') return a.title || 'Session ' + shortId(a.sessionId || a.id);
  return a.title || a.agentType || 'sub-agent ' + shortId(a.id);
}

export function agentLabel(id) {
  const a = get('agent', id);
  return a ? agentName(a) : shortId(id);
}

export function roots() {
  return all('agent').filter((a) => !a.parentId || !get('agent', a.parentId))
    .sort((a, b) => ms(b.lastActivityAt) - ms(a.lastActivityAt));
}

export function children(a) {
  return (a.childIds || []).map((id) => get('agent', id)).filter(Boolean)
    .sort((x, y) => ms(x.createdAt) - ms(y.createdAt));
}

export function sessionRoot(a) {
  let cur = a; const seen = new Set();
  while (cur && cur.parentId && !seen.has(cur.id)) { seen.add(cur.id); const p = get('agent', cur.parentId); if (!p) break; cur = p; }
  return cur;
}

export function descendants(a) {
  const out = []; const stack = [...children(a)]; const seen = new Set();
  while (stack.length) { const c = stack.pop(); if (seen.has(c.id)) continue; seen.add(c.id); out.push(c); stack.push(...children(c)); }
  return out;
}

export function agentDuration(a) {
  const start = ms(a.startedAt || a.createdAt);
  const end = isTerminal(a) ? ms(a.completedAt || a.lastActivityAt) : Date.now();
  return duration(end - start);
}

export function worktreeOfAgent(a) {
  const id = a.currentWorktreeId || mostRecentKey(a.worktrees);
  return id ? get('worktree', id) : null;
}

function mostRecentKey(dict) {
  let best = null, t = -Infinity;
  for (const [k, v] of Object.entries(dict || {})) { const x = ms(v); if (x > t) { t = x; best = k; } }
  return best;
}

export function counts() {
  const c = { active: 0, waiting: 0, blocked: 0, failed: 0, disconnected: 0, completed: 0, unknown: 0, total: 0 };
  for (const a of store.e.agent.values()) { c[statusMeta(a).group]++; c.total++; }
  return c;
}

export function repoName(id) { return get('repository', id)?.name || (id ? id.slice(0, 12) : ''); }

/** `<worktree dir>@<branch>`: distinguishes worktrees that share a branch name (e.g. several repos on `main`). */
export function worktreeRef(wt) {
  if (!wt) return '';
  const ref = wt.branch || (wt.head || '').slice(0, 7) || 'detached';
  return `${basename(wt.root)}@${ref}`;
}

export function worktreeLabel(wt) {
  if (!wt) return '';
  return wt.branch || (wt.detached ? 'detached@' + (wt.head || '').slice(0, 7) : basename(wt.root));
}

// ---- events --------------------------------------------------------------------

export const EVENT_CATEGORIES = [
  ['', 'All event types'],
  ['agent.*', 'Agent lifecycle'],
  ['agent.message_*', 'Messages'],
  ['tool.*', 'Tool calls'],
  ['command.*', 'Commands'],
  ['file.*', 'File changes'],
  ['git.*', 'Git'],
  ['test.*', 'Tests'],
  ['task.*', 'Tasks'],
  ['process.*', 'Processes'],
  ['worktree.*', 'Worktrees'],
  ['terminal.*', 'Terminals'],
  ['audit.*', 'Control / audit'],
  ['risk.*', 'Risky actions'],
  ['guard.*', 'Runaway guard'],
];

export function typeMatches(type, filter) {
  if (!filter) return true;
  if (filter.endsWith('*')) return type.startsWith(filter.slice(0, -1));
  return type === filter;
}

const ICON = {
  agent: ['◆', 'ic-agent'], message: ['✉', 'ic-msg'], tool: ['⚙', 'ic-tool'], command: ['$', 'ic-cmd'],
  file: ['✎', 'ic-file'], read: ['◫', 'ic-read'], git: ['⑂', 'ic-git'], test: ['⚑', 'ic-test'],
  task: ['☰', 'ic-task'], process: ['⚙', 'ic-proc'], worktree: ['⑂', 'ic-git'], repository: ['⛁', 'ic-git'],
  terminal: ['▣', 'ic-term'], audit: ['⛨', 'ic-audit'], risk: ['⛔', 'ic-fail'], fail: ['✕', 'ic-fail'], ok: ['✓', 'ic-ok'],
};

/** One-line structured description of an event: { icon, iconCls, text, detail, bad } */
export function describe(e) {
  const d = e.data || {};
  const t = e.type || '';
  const cat = t.split('.')[0];
  let [icon, iconCls] = ICON[cat] || ['•', 'ic-agent'];
  let text = t, detail = '', bad = false;
  switch (t) {
    case 'agent.created': text = `Session started: ${oneLine(d.title || '', 120)}`; detail = d.cwd || ''; break;
    case 'agent.spawned': text = `Spawned ${d.agentType || 'sub-agent'}: ${oneLine(d.title || d.description || '', 120)}`; break;
    case 'agent.started': case 'agent.status_changed': case 'agent.completed': case 'agent.failed': {
      const st = STATUS[norm(d.status)] || STATUS.unknown;
      text = `Status → ${st.label}${d.reason ? ': ' + oneLine(d.reason, 140) : ''}`;
      bad = ['failed', 'blocked', 'disconnected'].includes(norm(d.status));
      icon = st.glyph; iconCls = st.cls; break;
    }
    case 'agent.updated': text = 'Agent updated' + (d.title ? `: ${oneLine(d.title, 80)}` : '') + (d.gitBranch ? ` · branch ${d.gitBranch}` : ''); break;
    case 'agent.prompt': text = `Prompt: ${oneLine(d.text, 200)}`; icon = '›'; break;
    case 'agent.output': text = `Output: ${oneLine(d.text, 200)}`; icon = '‹'; break;
    case 'agent.notification': text = `Notification: ${oneLine(d.text, 200)}`; icon = '!'; iconCls = 'st-blocked'; break;
    case 'agent.message_sent': [icon, iconCls] = ICON.message; text = `Message queued (${d.transport || '?'}): ${oneLine(d.text, 160)}`; break;
    case 'agent.message_delivered': [icon, iconCls] = ICON.message; text = `Message delivered via ${d.transport || '?'}`; detail = d.detail || ''; break;
    case 'agent.message_failed': [icon, iconCls] = ICON.fail; text = `Message failed (${d.transport || '?'})`; detail = d.detail || ''; bad = true; break;
    case 'agent.message_received': [icon, iconCls] = ICON.message; text = `Reply (inferred): ${oneLine(d.text, 160)}`; break;
    case 'process.started': text = `Process ${d.pid} started: ${oneLine(d.command, 120)}`; break;
    case 'process.exited': text = `Process ${d.pid} exited${d.exitCode != null ? ' (' + d.exitCode + ')' : ''}${d.reason ? ': ' + d.reason : ''}`; bad = true; break;
    case 'tool.started': text = `${d.tool || 'Tool'} ${oneLine(d.summary && d.summary.startsWith(d.tool) ? d.summary.slice(d.tool.length) : d.summary || '', 160)}`; detail = d.filePath || ''; break;
    case 'tool.completed': [icon, iconCls] = ICON.ok; text = `${d.tool || 'Tool'} done${d.durationMs != null ? ' · ' + duration(d.durationMs) : ''}`; detail = d.filePath || ''; break;
    case 'tool.failed': [icon, iconCls] = ICON.fail; text = `${d.tool || 'Tool'} failed: ${oneLine(d.error || '', 160)}`; bad = true; break;
    case 'command.started': text = `$ ${oneLine(d.command, 200)}`; detail = d.description || ''; break;
    case 'command.completed': [icon, iconCls] = ICON.ok; text = `$ ${oneLine(d.command, 160)} → exit ${d.exitCode ?? 0}${d.durationMs != null ? ' · ' + duration(d.durationMs) : ''}`; break;
    case 'command.failed': [icon, iconCls] = ICON.fail; text = `$ ${oneLine(d.command, 160)} → exit ${d.exitCode ?? '?'}${d.interrupted ? ' (interrupted)' : ''}`; bad = true; break;
    case 'file.created': text = `Created ${e.filePath}${lines(d)}`; break;
    case 'file.modified': text = `Modified ${e.filePath}${lines(d)}`; break;
    case 'file.deleted': text = `Deleted ${e.filePath}`; break;
    case 'file.renamed': text = `Renamed ${d.oldPath || '?'} → ${e.filePath}`; break;
    case 'file.read': [icon, iconCls] = ICON.read; text = `Read ${e.filePath}`; break;
    case 'git.file_status': text = `git status ${d.status} ${e.filePath}${lines(d)}`; break;
    case 'git.commit_created': text = `Commit ${String(d.sha || '').slice(0, 7)} ${oneLine(d.subject, 140)}`; detail = d.branch || ''; break;
    case 'git.branch_changed': case 'git.checkout': text = `${t === 'git.checkout' ? 'Checkout' : 'Branch'} → ${d.branch || 'detached ' + String(d.head || '').slice(0, 7)}`; break;
    case 'git.merge': text = `Merge ${d.detail || ''}`; break;
    case 'git.rebase': text = `Rebase ${d.detail || ''}`; break;
    case 'git.conflict': text = `Git conflict in ${(d.paths || []).length} file(s)${d.operation ? ' during ' + d.operation : ''}`; bad = true; break;
    case 'repository.discovered': [icon, iconCls] = ICON.repository; text = `Repository discovered: ${d.name}`; detail = d.commonDir || ''; break;
    case 'worktree.created': text = `Worktree ${d.branch || ''} at ${d.root}`; break;
    case 'worktree.changed': text = `Worktree ${d.branch || ''}: ${d.modified || 0}M ${d.added || 0}A ${d.deleted || 0}D ${d.untracked || 0}? ${d.conflicted ? d.conflicted + 'U' : ''}`; detail = d.root || ''; break;
    case 'worktree.deleted': text = `Worktree removed ${d.root || ''}`; break;
    case 'test.started': text = `Tests started: ${oneLine(d.command, 140)}`; break;
    case 'test.passed': [icon, iconCls] = ICON.ok; text = `Tests passed: ${d.passed ?? '?'} passed${d.skipped ? `, ${d.skipped} skipped` : ''}`; detail = d.command || ''; break;
    case 'test.failed': [icon, iconCls] = ICON.fail; text = `Tests failed: ${d.failed ?? '?'} failed, ${d.passed ?? '?'} passed`; detail = (d.failedTests || []).join(', '); bad = true; break;
    case 'task.updated': {
      const ts = d.tasks || [];
      text = `Tasks: ${ts.filter((x) => norm(x.state) === 'completed').length}/${ts.length} completed`;
      const cur = ts.find((x) => norm(x.state) === 'inprogress');
      detail = cur ? cur.activeForm || cur.title : '';
      break;
    }
    case 'terminal.started': text = `Terminal ${d.terminalId} started: ${d.command || ''}`; break;
    case 'terminal.exited': text = `Terminal ${d.terminalId} exited (${d.exitCode ?? '?'})`; break;
    case 'risk.detected': text = `Risk (${d.severity || '?'}): ${oneLine(d.explanation || d.ruleId || '', 160)}`; detail = d.command || d.span || ''; bad = d.severity !== 'info'; break;
    case 'audit.control': text = `Control ${d.action} → ${d.outcome}`; detail = d.detail || ''; bad = d.outcome !== 'ok'; break;
    case 'guard.alert': icon = '⚠'; iconCls = norm(d.severity) === 'critical' ? 'ic-fail' : 'st-blocked'; text = `Runaway guard ${norm(d.severity)}: ${oneLine(d.summary || '', 160)}`; bad = norm(d.severity) === 'critical'; break;
    case 'guard.cleared': [icon, iconCls] = ICON.ok; text = `Runaway guard cleared (${d.rule}): ${oneLine(d.reason || '', 140)}`; break;
    case 'guard.dismissed': icon = '⚠'; text = 'Runaway guard alert dismissed'; detail = d.key || ''; break;
    default: text = t; break;
  }
  return { icon, iconCls, text, detail, bad };
}

function lines(d) {
  if (d.added == null && d.removed == null) return '';
  return ` (+${d.added ?? 0} −${d.removed ?? 0})`;
}

export function fileStatusLetter(f) {
  const g = (f.gitStatus || '').toUpperCase();
  if (g === 'CLEAN') return { letter: 'C', cls: 'fs-c', tip: 'Clean in git (committed or reverted) — observed' };
  if (g === '?') return { letter: 'A', cls: 'fs-a', tip: 'Untracked new file (git: ?) — observed' };
  if (g === 'A') return { letter: 'A', cls: 'fs-a', tip: 'Added (git: A) — observed' };
  if (g === 'M') return { letter: 'M', cls: 'fs-m', tip: 'Modified (git: M) — observed' };
  if (g === 'D') return { letter: 'D', cls: 'fs-d', tip: 'Deleted (git: D) — observed' };
  if (g === 'R') return { letter: 'R', cls: 'fs-m', tip: 'Renamed (git: R) — observed' };
  if (g === 'U') return { letter: 'U', cls: 'fs-u', tip: 'Unmerged / conflicted (git: U) — observed' };
  const k = norm(f.lastChangeKind);
  const map = { created: 'A', modified: 'M', deleted: 'D', renamed: 'R' };
  return { letter: map[k] || '·', cls: 'fs-r', tip: 'Not yet confirmed by git; change reported by the agent' };
}

export function filesOfAgent(id) {
  return all('file').filter((f) => f.agents && f.agents[id]);
}

export function conflictKindLabel(k) {
  switch (norm(k)) {
    case 'samefile': return 'Same file';
    case 'sameworktree': return 'Same worktree';
    case 'crossworktree': return 'Cross-worktree';
    case 'gitunmerged': return 'Git unmerged';
    default: return k;
  }
}

export function conflictHeadline(c) {
  switch (norm(c.kind)) {
    case 'samefile': return 'Two active agents are touching the same file';
    case 'sameworktree': return 'Several active agents are writing in the same worktree';
    case 'crossworktree': return 'The same path is changed in several worktrees — merging will likely conflict';
    case 'gitunmerged': return 'Git reports unmerged files / an in-progress merge or rebase';
    default: return 'Conflict';
  }
}

// ---- token usage ---------------------------------------------------------------------

const ZERO = () => ({ input: 0, output: 0, cacheWrite: 0, cacheRead: 0, responses: 0 });
export const usageTotal = (u) => (u?.input || 0) + (u?.output || 0) + (u?.cacheWrite || 0) + (u?.cacheRead || 0);

export function addUsage(acc, u) {
  if (!u) return acc;
  acc.input += u.input || 0; acc.output += u.output || 0; acc.cacheWrite += u.cacheWrite || 0;
  acc.cacheRead += u.cacheRead || 0; acc.responses += u.responses || 0;
  return acc;
}

/** The agent's own usage plus all of its sub-agents (any depth). */
export function usageRollup(a, seen = new Set()) {
  const acc = ZERO();
  const walk = (x) => {
    if (!x || seen.has(x.id)) return;
    seen.add(x.id);
    addUsage(acc, x.usage);
    for (const id of x.childIds || []) walk(get('agent', id));
  };
  walk(a);
  return acc;
}

/** Usage per model for the agent and its sub-agents. */
export function usageByModelRollup(a) {
  const out = new Map();
  const seen = new Set();
  const walk = (x) => {
    if (!x || seen.has(x.id)) return;
    seen.add(x.id);
    for (const [m, u] of Object.entries(x.usageByModel || {})) addUsage(out.get(m) || out.set(m, ZERO()).get(m), u);
    for (const id of x.childIds || []) walk(get('agent', id));
  };
  walk(a);
  return out;
}

export function allUsage() {
  const acc = ZERO();
  for (const a of all('agent')) addUsage(acc, a.usage);
  return acc;
}

/** 950 → "950", 12_300 → "12.3k", 4_560_000 → "4.56M", 1.2e9 → "1.2B" */
export function fmtTok(n) {
  n = n || 0;
  if (n < 1000) return String(n);
  const [v, u] = n >= 1e9 ? [n / 1e9, 'B'] : n >= 1e6 ? [n / 1e6, 'M'] : [n / 1e3, 'k'];
  const s = v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2);
  return (s.includes('.') ? s.replace(/0+$/, '').replace(/\.$/, '') : s) + u; // trim decimals only: 170M stays 170M
}

export function usageTitle(u, label = 'Tokens') {
  return `${label} (reported by the provider)\n` +
    `input ${(u.input || 0).toLocaleString()} · output ${(u.output || 0).toLocaleString()}\n` +
    `cache write ${(u.cacheWrite || 0).toLocaleString()} · cache read ${(u.cacheRead || 0).toLocaleString()}\n` +
    `${(u.responses || 0).toLocaleString()} model responses`;
}

/** claude-opus-5-5 → opus 5.5; claude-haiku-4-5-20251001 → haiku 4.5. Other vendors' ids (gpt-5-codex,
 *  gemini-2.5-pro) have no such convention, so they are shown as reported, minus a trailing date. */
export function shortModelName(m) {
  const id = String(m).replace(/-\d{8}$/, '').replace(/-\d{4}-\d{2}-\d{2}$/, '');
  if (!/^claude-/.test(id)) return id;
  const parts = id.replace(/^claude-/, '').split('-');
  const name = parts.shift();
  return parts.length && parts.every((x) => /^\d+$/.test(x)) ? `${name} ${parts.join('.')}` : [name, ...parts].join(' ');
}

// ---- commit links ----------------------------------------------------------------------

/** Web page of a commit, from the repository's origin remote (derived by the daemon). Null if unknown. */
export function commitUrl(worktreeId, sha) {
  const wt = worktreeId ? get('worktree', worktreeId) : null;
  const web = wt ? get('repository', wt.repositoryId)?.webUrl : null;
  if (!web || !/^https:\/\//.test(web) || !/^[0-9a-f]{7,64}$/i.test(sha || '')) return null;
  const host = new URL(web).host;
  return host.includes('gitlab') ? `${web}/-/commit/${sha}` : host.includes('bitbucket') ? `${web}/commits/${sha}` : `${web}/commit/${sha}`;
}

/** Short SHA that opens the commit on the git host in a new tab (plain text if the repo has no web remote). */
export function shaLink(worktreeId, sha, len = 7) {
  const text = String(sha || '').slice(0, len);
  const url = commitUrl(worktreeId, sha);
  if (!url) return h('span', { class: 'mono sha', title: 'No web remote (origin) for this repository' }, text);
  const a = h('a', { class: 'mono sha sha-link', href: url, target: '_blank', rel: 'noopener noreferrer', title: `Open commit ${sha} on ${new URL(url).host}` }, text, ' ↗');
  a.addEventListener('click', (e) => e.stopPropagation());
  return a;
}

/**
 * Commits relevant to an agent: those attributed to it or to any of its sub-agents (any depth), plus
 * unattributed commits in worktrees it or its sub-agents touched. Each entry says which applies.
 */
export function commitsForAgent(a) {
  const ids = new Set([a.id, ...descendants(a).map((x) => x.id)]);
  const wts = new Set();
  for (const id of ids) for (const w of Object.keys(get('agent', id)?.worktrees || {})) wts.add(w);
  return all('commit')
    .filter((c) => (c.agentId && ids.has(c.agentId)) || (!c.agentId && wts.has(c.worktreeId)))
    .map((c) => ({ c, kind: !c.agentId ? 'unattributed' : c.agentId === a.id ? 'own' : 'sub' }))
    .sort((x, y) => ms(y.c.at) - ms(x.c.at));
}

const TOOL_NAMES = { claude: 'Claude Code', codex: 'Codex', gemini: 'Gemini CLI', grok: 'Grok', aider: 'Aider', opencode: 'OpenCode', cursor: 'Cursor', copilot: 'Copilot' };
/** Display name of the AI tool an agent belongs to (its provider id). */
export function toolName(providerId) { return TOOL_NAMES[providerId] || providerId || 'agent'; }
