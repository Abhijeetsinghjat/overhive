// Right detail panel: agent / worktree / repository / file / conflict.
import { h, mount, agoEl, oneLine, norm, shortId, dateTime, clock, duration, ms, basename, copyText } from '../util.js';
import { store, register, get, all, invalidate } from '../store.js';
import { ui, select, openDrawer, setFilter, setTab } from '../ui.js';
import { api } from '../api.js';
import {
  agentName, statusMeta, statusOf, statusPill, statusDot, evBadge, agentColor, children, isTerminal, isActive, worktreeLabel, repoName,
  fileStatusLetter, filesOfAgent, conflictHeadline, conflictKindLabel, describe, STATUS_DESCRIPTIONS, agentLabel,
  fmtTok, usageRollup, usageByModelRollup, usageTotal, shortModelName,
  shaLink,
  commitsForAgent,
} from '../model.js';
import { renderDiff } from '../components/diff.js';
import { toast, confirmModal } from '../components/overlay.js';
import { guardSection, guardSigForAgent } from './guard.js';

const caps = new Map();       // agentId → { data, error, loading, sig }
const history = new Map();    // fileKey → { events, error, loading, at }
const composers = new Map();  // agentId → composer element (kept so typing survives re-renders)
const closed = new Set(JSON.parse(safeGet('amc.closedSections') || '[]'));

function safeGet(k) { try { return localStorage.getItem(k); } catch { return null; } }

export function initDetail() {
  const right = document.getElementById('right');
  let lastSig = null;
  register({
    name: 'detail', deps: ['selection', 'agent', 'worktree', 'file', 'message', 'test', 'commit', 'conflict', 'repository', 'terminal', 'detail', 'follow', 'events', 'guard', 'guard-live'],
    render(d) {
      const sel = ui.selection;
      const ent = sel ? get(sel.kind, sel.id) : null;
      const sig = sel ? sigFor(sel, ent) : overviewSig();
      if (sig === lastSig && !d.has('selection') && !d.has('detail')) return;
      const scroll = right.scrollTop;
      const sameSel = right._sel === (sel ? sel.kind + sel.id : '');
      lastSig = sig;
      right._sel = sel ? sel.kind + sel.id : '';
      let content;
      if (!sel) content = overview();
      else if (!ent) content = h('div', { class: 'detail' }, h('div', { class: 'empty' }, store.stateLoadedAt ? `This ${sel.kind} is not in the current state (it may have been removed or not discovered yet).` : 'Loading…'), backBtn());
      else if (sel.kind === 'agent') content = agentDetail(ent);
      else if (sel.kind === 'worktree') content = worktreeDetail(ent);
      else if (sel.kind === 'repository') content = repoDetail(ent);
      else if (sel.kind === 'file') content = fileDetail(ent);
      else if (sel.kind === 'conflict') content = conflictDetail(ent);
      else content = h('div', { class: 'detail' }, h('pre', { class: 'pre' }, JSON.stringify(ent, null, 2)));
      mount(right, content);
      if (sameSel) right.scrollTop = scroll;
    },
  });
}

function overviewSig() {
  const parts = ['none', store.e.repository.size, store.e.worktree.size, [...store.e.conflict.keys()].join(',')];
  for (const a of store.e.agent.values()) if (['blocked', 'failed', 'disconnected'].includes(statusOf(a))) parts.push(a.id, a.status, a.statusReason);
  return parts.join('§');
}

function sigFor(sel, ent) {
  if (!ent) return sel.kind + sel.id + ':gone';
  const parts = [sel.kind, sel.id, JSON.stringify(ent)];
  if (sel.kind === 'agent') {
    const c = caps.get(sel.id);
    parts.push(c?.loading, c?.error?.message, JSON.stringify(c?.data));
    for (const kind of ['message', 'test', 'commit']) for (const v of store.e[kind].values()) if (v.agentId === sel.id) parts.push(JSON.stringify(v));
    for (const f of store.e.file.values()) if (f.agents && f.agents[sel.id]) parts.push(f.key, f.gitStatus, f.added, f.removed, JSON.stringify(f.agents[sel.id]));
    for (const id of ent.childIds || []) { const c2 = get('agent', id); parts.push(c2?.status, c2?.title); }
    const lt = store.lastTool.get(sel.id); parts.push(lt?.seq);
    parts.push(guardSigForAgent(ent));
  }
  if (sel.kind === 'file') { const hst = history.get(sel.id); parts.push(hst?.at, hst?.loading); }
  if (sel.kind === 'worktree' || sel.kind === 'repository') parts.push(store.e.file.size, store.e.commit.size, store.e.agent.size);
  parts.push(store.e.conflict.size);
  return parts.join('§');
}

// ---- shared bits ----------------------------------------------------------------

function backBtn() {
  const b = h('button', { class: 'btn btn-sm btn-ghost' }, '← Overview');
  b.addEventListener('click', () => select(null));
  return b;
}

function section(key, title, count, ...content) {
  const isClosed = closed.has(key);
  const head = h('button', { class: 'sec-head', 'aria-expanded': String(!isClosed) },
    h('span', { class: 'sec-caret' }, isClosed ? '▸' : '▾'), title, count != null ? h('span', { class: 'sec-count' }, String(count)) : null);
  const body = h('div', { class: 'sec-body', hidden: isClosed }, ...content);
  head.addEventListener('click', () => {
    if (closed.has(key)) closed.delete(key); else closed.add(key);
    try { localStorage.setItem('amc.closedSections', JSON.stringify([...closed])); } catch { /* ignore */ }
    body.hidden = closed.has(key);
    head.firstChild.textContent = closed.has(key) ? '▸' : '▾';
  });
  return h('section', { class: 'sec' }, head, body);
}

function kv(rows) {
  return h('table', { class: 'kv' }, rows.filter((r) => r && r[1] != null && r[1] !== '' && r[1] !== false).map(([k, v, ev]) =>
    h('tr', null, h('td', null, k), h('td', null, v, ev ? [' ', evBadge(ev)] : null))));
}

function link(label, fn, cls = '') {
  const b = h('button', { class: 'link ' + cls }, label);
  b.addEventListener('click', fn);
  return b;
}

const agentLink = (id) => {
  const a = get('agent', id);
  if (!a) return h('span', { class: 'mono muted' }, shortId(id));
  return h('span', { class: 'alink' }, statusDot(a), h('span', { class: 'agent-swatch', style: { background: agentColor(id) } }), link(oneLine(agentName(a), 50), () => select('agent', id)));
};
const wtLink = (id) => { const w = get('worktree', id); return w ? link('⑂ ' + worktreeLabel(w), () => select('worktree', id)) : h('span', { class: 'mono muted' }, id); };
const repoLink = (id) => { const r = get('repository', id); return r ? link(r.name, () => select('repository', id)) : h('span', { class: 'mono muted' }, id || '—'); };

function copyable(text, display = text) {
  const el = h('span', { class: 'mono copy', title: 'Click to copy\n' + text }, display);
  el.addEventListener('click', () => { copyText(text); toast('Copied', 'ok', text, 1500); });
  return el;
}

function btn(label, title, fn, cls = '') {
  const b = h('button', { class: 'btn btn-sm ' + cls, title }, label);
  b.addEventListener('click', fn);
  return b;
}

function conflictsFor(pred) {
  return all('conflict').filter(pred).map((c) => h('div', { class: 'conf-item' },
    h('span', { class: 'cb-kind' }, conflictKindLabel(c.kind)), ' ', link(conflictHeadline(c), () => select('conflict', c.key)),
    c.path ? h('div', { class: 'mono small muted' }, c.path) : null));
}

// ---- overview (nothing selected) ---------------------------------------------------

function overview() {
  const conflicts = all('conflict');
  const blocked = all('agent').filter((a) => statusOf(a) === 'blocked');
  const failed = all('agent').filter((a) => statusOf(a) === 'failed' || statusOf(a) === 'disconnected');
  return h('div', { class: 'detail' },
    h('div', { class: 'd-head' }, h('div', { class: 'd-title' }, 'Overview'), h('div', { class: 'muted small' }, 'Select an agent, worktree, file or conflict to see details.')),
    conflicts.length ? section('ov-conf', 'Conflicts', conflicts.length, conflictsFor(() => true)) : null,
    section('ov-blocked', 'Needs your input', blocked.length, blocked.length ? blocked.map((a) => h('div', { class: 'ov-item' }, agentLink(a.id), h('div', { class: 'small muted' }, a.statusReason || ''))) : h('div', { class: 'muted small' }, 'No agent is waiting for you.')),
    failed.length ? section('ov-failed', 'Failed / disconnected', failed.length, failed.map((a) => h('div', { class: 'ov-item' }, agentLink(a.id), h('div', { class: 'small muted' }, a.statusReason || '')))) : null,
    section('ov-repos', 'Repositories', store.e.repository.size, all('repository').map((r) => h('div', { class: 'ov-item' }, repoLink(r.id), h('span', { class: 'muted small' }, ` · ${all('worktree').filter((w) => w.repositoryId === r.id).length} worktrees`)))),
    section('ov-help', 'How data appears', null, h('ul', { class: 'small muted plain' },
      h('li', null, 'Any AI coding tool is detected from its running process: folder, worktree, files and commits (observed).'),
      h('li', null, 'Tools with hooks connected (Claude Code, Codex, Gemini CLI, Grok, Cursor, Copilot) or reporting through the open protocol also send tool calls, prompts, files and usage (reported).'),
      h('li', null, 'Git state is observed from repositories under the workspace roots.'),
      h('li', null, 'Sessions started with ', h('code', null, '+ New session'), ' get a live terminal you can type into.'))));
}

// ---- agent ----------------------------------------------------------------------------

function ensureCaps(a) {
  const sig = a.status + '|' + a.pid + '|' + a.terminalId + '|' + (a.transports || []).join(',');
  const c = caps.get(a.id);
  if (c && (c.sig === sig || c.loading)) return c;
  const entry = { loading: true, sig, data: c?.data, error: null };
  caps.set(a.id, entry);
  api.agent(a.id).then((r) => { entry.data = r?.capabilities ?? null; entry.loaded = true; })
    .catch((e) => { entry.error = e; })
    .finally(() => { entry.loading = false; invalidate('detail'); });
  return entry;
}

const CONTROLS = [
  ['pause', 'Pause', 'SIGSTOP the agent process group'],
  ['resume', 'Resume', 'SIGCONT the agent process group'],
  ['interrupt', 'Interrupt', 'Send ESC into the agent terminal'],
  ['terminate', 'Terminate', 'SIGTERM — asks the agent process to exit'],
  ['kill', 'Kill', 'SIGKILL — immediately kills the process'],
];

function controlsRow(a, c) {
  const controls = {};
  for (const [k, v] of Object.entries(c?.data?.controls || {})) controls[norm(k)] = v;
  const noCaps = c?.error ? `Capabilities unavailable: ${c.error.message}` : c?.loading && !c?.data ? 'Loading capabilities…' : !c?.data ? 'No provider is loaded for this agent, so it cannot be controlled.' : null;
  return h('div', { class: 'controls' }, CONTROLS.map(([action, label, tip]) => {
    const reason = noCaps || (action in controls ? controls[action] : `${label} is not supported for this agent`);
    const supported = !reason;
    const danger = action === 'terminate' || action === 'kill';
    const b = h('button', { class: `btn btn-sm${danger ? ' btn-danger-o' : ''}`, disabled: !supported, title: supported ? tip : `${label} unavailable: ${reason}` }, label);
    b.addEventListener('click', () => doControl(a, action, label, danger));
    return h('span', { class: 'ctl-wrap', title: supported ? tip : `${label} unavailable: ${reason}` }, b);
  }));
}

async function doControl(a, action, label, danger) {
  let confirm = false;
  if (danger) {
    const sid = shortId(a.id);
    const ok = await confirmModal({
      title: `${label} ${agentName(a)}?`,
      body: h('div', null,
        h('p', null, action === 'kill' ? 'SIGKILL ends the process immediately. Unsaved work and running tool calls are lost.' : 'SIGTERM asks the agent process to exit. Running tool calls may be cut short.'),
        h('p', { class: 'muted small' }, 'Agent ', h('code', null, a.id), a.pid ? [' · pid ', h('code', null, String(a.pid))] : null),
        norm(a.kind) === 'sub' ? h('p', { class: 'warn small' }, 'Note: sub-agents usually run inside their session\'s process; stopping acts on that process if allowed.') : null),
      confirmLabel: label, danger: true, typeToConfirm: sid,
    });
    if (!ok) return;
    confirm = true;
  }
  try {
    const r = await api.control(a.id, action, confirm);
    toast(`${label}: ${r.ok ? 'done' : 'failed'}`, r.ok ? 'ok' : 'err', r.detail || '');
  } catch (e) {
    toast(`${label} failed`, 'err', e.status === 428 ? 'Confirmation required (428).' : e.message);
  }
}

function composer(a, c) {
  let el = composers.get(a.id);
  if (!el) {
    const ta = h('textarea', { class: 'input composer-text', rows: 3, placeholder: 'Message this agent…  (Ctrl+Enter to send)', maxlength: 8000 });
    const send = h('button', { class: 'btn btn-primary btn-sm' }, 'Send');
    const note = h('div', { class: 'small muted composer-note' });
    const doSend = async () => {
      const text = ta.value.trim();
      if (!text || send.disabled) return;
      send.disabled = true;
      try {
        const r = await api.sendMessage(a.id, text);
        ta.value = '';
        const how = r.delivered ? `delivered via ${r.transport}` : `queued${r.transport ? ' on ' + r.transport : ''} (not yet delivered)`;
        toast(r.ok ? `Message ${how}` : 'Message not accepted', r.ok ? 'ok' : 'err', r.detail || '');
      } catch (e) {
        toast('Message failed', 'err', e.message);
      } finally { send.disabled = false; invalidate('detail'); }
    };
    send.addEventListener('click', doSend);
    ta.addEventListener('keydown', (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); doSend(); } });
    el = h('div', { class: 'composer' }, ta, h('div', { class: 'composer-row' }, note, h('span', { class: 'spacer' }), send));
    el._ta = ta; el._send = send; el._note = note;
    composers.set(a.id, el);
  }
  const transports = c?.data?.messageTransports || [];
  let reason = null;
  if (c?.error) reason = `Capabilities unavailable: ${c.error.message}`;
  else if (!c?.data) reason = c?.loading ? 'Loading capabilities…' : 'No provider is loaded for this agent, so no message transport is available.';
  else if (!transports.length) reason = 'This agent cannot receive messages: no supported transport (it is not a PTY session and has no hook mailbox).';
  el._ta.disabled = !!reason;
  el._send.disabled = !!reason;
  el._send.title = reason || `Send via ${transports[0]}`;
  el._note.replaceChildren(reason ? h('span', { class: 'warn' }, reason)
    : h('span', null, 'via ', h('b', null, transports[0]), transports[0] === 'hook-mailbox' ? ' — a busy agent gets it at its next tool call; an idle agent is woken immediately.' : transports[0] === 'pty' ? ' — typed into the terminal Overhive owns.' : ''));
  return el;
}

// Only the latest messages are rendered; earlier ones load on demand (per agent, for this page view).
const MSG_INITIAL = 3, MSG_STEP = 10;
const msgShown = new Map();

function messageThread(agentId) {
  const msgs = all('message').filter((m) => m.agentId === agentId).sort((x, y) => ms(x.sentAt) - ms(y.sentAt));
  if (!msgs.length) return h('div', { class: 'muted small' }, 'No messages sent to this agent yet.');
  const shown = Math.min(msgs.length, msgShown.get(agentId) ?? MSG_INITIAL);
  const hidden = msgs.length - shown;
  let more = null;
  if (hidden > 0) {
    more = h('button', { class: 'btn btn-sm btn-ghost more-msgs', title: 'Load earlier messages' },
      `Show ${Math.min(hidden, MSG_STEP)} earlier message${Math.min(hidden, MSG_STEP) === 1 ? '' : 's'} (${hidden} hidden)`);
    more.addEventListener('click', () => { msgShown.set(agentId, shown + MSG_STEP); invalidate('message'); });
  }
  return h('div', { class: 'thread' }, more, msgs.slice(hidden).map(messageItem));
}

export function messageItem(m) {
  const st = norm(m.status);
  const step = (label, ts, on, cls = '') => h('span', { class: `mstep ${on ? 'on' : ''} ${cls}` }, label, ts ? [' ', h('span', { class: 'muted', title: dateTime(ts) }, clock(ts))] : null);
  return h('div', { class: 'msg' },
    h('div', { class: 'msg-out' },
      h('div', { class: 'msg-meta small' }, h('b', null, 'You'), ' → ', agentLabel(m.agentId), ' · ', m.transport, h('span', { class: 'spacer' }), h('span', { class: `mstatus ms-${st}` }, st)),
      h('div', { class: 'msg-text' }, m.text),
      h('div', { class: 'msg-steps small' },
        step('queued', m.sentAt, true), ' → ',
        st === 'failed' ? step('failed', null, true, 'bad') : step('delivered', m.deliveredAt, !!m.deliveredAt), ' → ',
        step('answered', m.repliedAt, st === 'answered')),
      m.detail ? h('div', { class: 'small muted' }, m.detail) : null),
    m.reply ? h('div', { class: 'msg-in' },
      h('div', { class: 'msg-meta small' }, h('b', null, agentLabel(m.agentId)), ' replied ', evBadge('inferred', 'Reply linkage is inferred: the first assistant text after delivery.'), h('span', { class: 'spacer' }), m.repliedAt ? clock(m.repliedAt) : ''),
      h('div', { class: 'msg-text' }, m.reply)) : null);
}

function taskList(a) {
  const tasks = a.tasks || [];
  const done = tasks.filter((t) => norm(t.state) === 'completed').length;
  const icon = { completed: ['✓', 'tk-done'], inprogress: ['●', 'tk-cur'], pending: ['○', 'tk-pend'], blocked: ['!', 'tk-block'], cancelled: ['⊘', 'tk-pend'] };
  return [
    h('div', { class: 'tasks-progress', title: 'completed / total, from the task list the agent reported' },
      h('span', { class: 'bar' }, h('span', { class: 'bar-fill', style: { width: (100 * done / tasks.length) + '%' } })),
      h('span', null, `${done}/${tasks.length} completed`), evBadge('reported', 'From the agent\'s TodoWrite / task tool calls')),
    h('ul', { class: 'tasks' }, tasks.map((t) => {
      const [g, cls] = icon[norm(t.state)] || ['○', 'tk-pend'];
      return h('li', { class: cls, title: `${t.state}${t.updatedAt ? ' · updated ' + dateTime(t.updatedAt) : ''}` },
        h('span', { class: 'tk-icon' }, g), h('span', null, norm(t.state) === 'inprogress' && t.activeForm ? t.activeForm : t.title));
    })),
  ];
}

/** Files changed by any descendant sub-agent (any depth), grouped per sub-agent. Null when there are none. */
function subFilesSection(a) {
  const groups = [];
  const seen = new Set([a.id]);
  const walk = (x) => {
    for (const c of children(x)) {
      if (seen.has(c.id)) continue;
      seen.add(c.id);
      const fs = filesOfAgent(c.id);
      if (fs.length) groups.push([c, fs]);
      walk(c);
    }
  };
  walk(a);
  if (!groups.length) return null;
  const total = new Set(groups.flatMap(([, fs]) => fs.map((f) => f.key))).size;
  return section('a-subfiles', 'Files changed by sub-agents', total,
    groups.map(([c, fs]) => h('div', { class: 'fgroup' },
      h('div', { class: 'fgroup-head' }, agentLink(c.id), h('span', { class: 'muted small' }, ` ${fs.length} file${fs.length === 1 ? '' : 's'}`)),
      filesByWorktree(fs, c.id))));
}

function filesByWorktree(files, agentId) {
  const groups = new Map();
  for (const f of files) { if (!groups.has(f.worktreeId)) groups.set(f.worktreeId, []); groups.get(f.worktreeId).push(f); }
  return [...groups].map(([wid, list]) => h('div', { class: 'fgroup' },
    h('div', { class: 'fgroup-head' }, wtLink(wid), h('span', { class: 'muted small' }, ` ${list.length} file${list.length === 1 ? '' : 's'}`)),
    list.sort((x, y) => x.path.localeCompare(y.path)).map((f) => fileLine(f, agentId))));
}

function fileLine(f, agentId) {
  const st = fileStatusLetter(f);
  const att = agentId ? f.agents?.[agentId] : null;
  const el = h('div', { class: 'fline', title: f.path },
    h('span', { class: 'fs ' + st.cls, title: st.tip }, st.letter),
    h('span', { class: 'fl-path mono' }, '\u200e' + f.path),
    h('span', { class: 'spacer' }),
    f.added != null || f.removed != null ? h('span', { class: 'small nowrap' }, h('span', { class: 'add' }, `+${f.added ?? 0}`), ' ', h('span', { class: 'del' }, `−${f.removed ?? 0}`)) : null,
    att ? evBadge(att.evidence, `Attribution of this change to the agent (${att.edits} edit${att.edits === 1 ? '' : 's'})`) : null);
  el.addEventListener('click', () => select('file', f.key));
  return el;
}

function testItem(t) {
  const st = norm(t.state);
  return h('div', { class: 'test-item ts-' + st },
    h('div', { class: 'ti-row' },
      h('span', { class: 'ti-state' }, st === 'passed' ? '✓ passed' : st === 'failed' ? '✕ failed' : '◌ running'),
      t.passed != null ? h('span', { class: 'add' }, `${t.passed} passed`) : null,
      t.failed ? h('span', { class: 'del' }, `${t.failed} failed`) : null,
      t.skipped ? h('span', { class: 'muted' }, `${t.skipped} skipped`) : null,
      evBadge('inferred', 'Counts are parsed from the test runner output'),
      h('span', { class: 'spacer' }), agoEl(t.endedAt || t.startedAt)),
    h('div', { class: 'mono small muted' }, oneLine(t.command, 120)),
    (t.failedTests || []).length ? h('ul', { class: 'failed-tests mono small' }, t.failedTests.slice(0, 12).map((n) => h('li', null, '✕ ', n))) : null);
}

function commitItem(c, kind = null, viewerId = null) {
  return h('div', { class: 'commit' },
    shaLink(c.worktreeId, c.sha), ' ', h('span', null, oneLine(c.subject, 90)),
    h('div', { class: 'small muted' }, c.branch ? '⑂ ' + c.branch + ' · ' : '', c.filesChanged ? `${c.filesChanged} files · ` : '', agoEl(c.at),
      c.agentId && c.agentId !== viewerId ? [' · by ', agentLink(c.agentId), ' ', evBadge('inferred', 'Commit author agent is inferred')] : null,
      c.agentId && c.agentId === viewerId ? [' ', evBadge('inferred', 'Attributed to this agent by inference')] : null,
      kind === 'unattributed' ? [' · ', h('span', { class: 'tag', title: 'Several agents (or none) were active in this worktree when the commit appeared, so it is not attributed to anyone' }, 'unattributed — in a worktree this agent used')] : null));
}

function agentDetail(a) {
  const c = ensureCaps(a);
  const m = statusMeta(a);
  const st = statusOf(a);
  const isMain = norm(a.kind) === 'main';
  const wt = a.currentWorktreeId ? get('worktree', a.currentWorktreeId) : null;
  const parent = a.parentId ? get('agent', a.parentId) : null;
  const kids = children(a);
  const files = filesOfAgent(a.id);
  const tests = all('test').filter((t) => t.agentId === a.id).sort((x, y) => ms(y.startedAt) - ms(x.startedAt));
  const commits = commitsForAgent(a);
  const nMsgs = all('message').filter((x) => x.agentId === a.id).length;
  const terminal = a.terminalId ? get('terminal', a.terminalId) : null;
  const lt = store.lastTool.get(a.id);
  const recent = [];
  for (let i = store.events.length - 1; i >= 0 && recent.length < 15; i--) if (store.events[i].agentId === a.id) recent.push(store.events[i]);

  const now = a.currentTool
    ? kv([
      ['Tool', h('b', null, a.currentTool), 'reported'],
      ['Action', a.currentAction && a.currentAction !== a.currentTool ? a.currentAction : null],
      ['Command', a.currentCommand ? h('code', { class: 'wrap' }, a.currentCommand) : null],
      ['File', a.currentFile ? h('span', { class: 'mono' }, a.currentFile) : null],
    ])
    : h('div', { class: 'muted small' }, isTerminal(a) ? 'Agent has ended; no current activity.' : 'No tool call in progress is reported. ',
      lt ? ['Last: ', describe(lt).text, ' · ', agoEl(lt.ts)] : null);

  const actions = h('div', { class: 'd-actions' },
    btn('Diff', 'Diff of files attributed to this agent', () => openDrawer('diff', { kind: 'agent', agentId: a.id, title: agentName(a) })),
    btn('Terminal', a.terminalId ? 'Live terminal (PTY)' : 'Command output (no live terminal)', () => openDrawer('terminal', { agentId: a.id })),
    btn('Messages', 'Message thread in the drawer', () => openDrawer('messages', { agentId: a.id })),
    btn('Events', 'Filter the timeline to this agent and its sub-agents', () => { setFilter('agent', a.id); setTab('timeline'); }));

  return h('div', { class: 'detail' },
    h('div', { class: 'd-head', style: { borderLeftColor: agentColor(a.id) } },
      h('div', { class: 'd-kicker' }, isMain ? 'Session (main agent)' : `Sub-agent · ${a.agentType || 'unknown type'}`, h('span', { class: 'spacer' }), backBtn()),
      h('div', { class: 'd-title' }, agentName(a)),
      h('div', { class: 'd-status' }, statusPill(a), evBadge(a.statusEvidence, 'Evidence for the status'),
        h('span', { class: 'muted small' }, 'last evidence ', agoEl(a.lastActivityAt))),
      a.statusReason ? h('div', { class: `d-reason ${m.cls}` }, a.statusReason) : h('div', { class: 'd-reason muted' }, STATUS_DESCRIPTIONS[st] || ''),
      st === 'blocked' ? h('div', { class: 'needs-input' }, '⚠ Needs your input — ', a.terminalId ? 'answer in its terminal (Terminal button) or send a message.' : 'answer in the terminal where this Claude Code session runs.') : null,
      a.description && !isMain ? h('div', { class: 'd-desc' }, a.description) : null),
    controlsRow(a, c),
    guardSection(a, section),
    actions,
    section('a-where', 'Location', null, locationOf(a, wt)),
    section('a-now', 'Now', null, now),
    usageSection(a, kids),
    (a.tasks || []).length ? section('a-tasks', 'Tasks', a.tasks.length, ...taskList(a)) : null,
    section('a-msg', 'Messages', nMsgs, messageThread(a.id), composer(a, c)),
    section('a-files', 'Files changed', files.length, files.length ? filesByWorktree(files, a.id) : h('div', { class: 'muted small' }, 'No file changes attributed to this agent.')),
    subFilesSection(a),
    tests.length ? section('a-tests', 'Tests', tests.length, tests.slice(0, 10).map(testItem)) : null,
    commits.length ? section('a-commits', 'Commits', commits.length,
      commits.slice(0, 10).map(({ c, kind }) => commitItem(c, kind, a.id)),
      h('div', { class: 'd-actions' }, btn(commits.length > 10 ? `Show all ${commits.length} in Commits tab` : 'Open in Commits tab',
        'All commits of this agent and its sub-agents', () => { setFilter('agent', a.id); setTab('commits'); }))) : null,
    conflictsFor((x) => (x.agentIds || []).includes(a.id)).length ? section('a-conf', 'Conflicts', null, conflictsFor((x) => (x.agentIds || []).includes(a.id))) : null,
    section('a-info', 'Details', null, kv([
      ['ID', copyable(a.id)],
      ['Provider', a.provider],
      ['Model', a.model ? h('span', { class: 'mono' }, a.model) : null, 'reported'],
      ['Version', a.version],
      ['Parent', parent ? agentLink(parent.id) : null],
      ['Children', kids.length ? h('div', { class: 'vlist-links' }, kids.map((k) => agentLink(k.id))) : null],
      ['Session', a.sessionId ? copyable(a.sessionId, shortId(a.sessionId) + '…') : null],
      ['PID', a.pid ? h('span', null, link(String(a.pid), () => setTab('processes')), a.procStart ? h('span', { class: 'muted small' }, ` start ${a.procStart}`) : null) : (isMain ? h('span', { class: 'muted' }, 'unknown') : h('span', { class: 'muted' }, 'runs inside the session process')), a.pid ? 'observed' : null],
      ['CWD', a.cwd ? h('span', { class: 'mono' }, a.cwd) : null, 'reported'],
      ['Terminal', a.terminalId ? h('span', null, h('span', { class: 'mono' }, a.terminalId), terminal ? (terminal.alive ? ' · alive' : ` · exited ${terminal.exitCode ?? ''}`) : '') : h('span', { class: 'muted' }, 'none (not started via agentctl run)')],
      ['Transports', (a.transports || []).length ? a.transports.join(', ') : h('span', { class: 'muted' }, 'none reported')],
      ['Transcript', a.transcriptPath ? h('span', { class: 'mono small' }, a.transcriptPath) : null],
      ['Created', dateTime(a.createdAt)],
      ['Started', a.startedAt ? dateTime(a.startedAt) : null],
      ['Completed', a.completedAt ? dateTime(a.completedAt) : null],
      ['Duration', duration((isTerminal(a) ? ms(a.completedAt || a.lastActivityAt) : Date.now()) - ms(a.startedAt || a.createdAt))],
      ['Tool calls', `${a.toolCount || 0} (${a.toolFailures || 0} failed)`, 'reported'],
      ['Last prompt', a.lastPrompt ? h('div', { class: 'quote' }, a.lastPrompt) : null, a.lastPrompt ? 'reported' : null],
      ['Last output', a.lastOutput ? h('div', { class: 'quote' }, a.lastOutput) : null, a.lastOutput ? 'reported' : null],
    ])),
    section('a-recent', 'Recent events', recent.length, recent.length ? h('div', { class: 'mini-events' }, recent.map((e) => miniEvent(e))) : h('div', { class: 'muted small' }, 'No events for this agent in memory.')));
}

/** Token usage: this agent, and (for agents with sub-agents) the whole subtree, plus per-model split. */
function usageSection(a, kids) {
  const own = a.usage || {};
  const all = usageRollup(a);
  if (!usageTotal(all)) return section('a-usage', 'Usage', null, h('div', { class: 'muted small' }, 'No token usage reported yet.'));
  const line = (label, u, strong) => h('tr', null, h('td', null, label),
    ...['input', 'output', 'cacheWrite', 'cacheRead'].map((k) => h('td', { class: 'num' }, fmtTok(u[k] || 0))),
    h('td', { class: 'num' }, strong ? h('b', null, fmtTok(usageTotal(u))) : fmtTok(usageTotal(u))));
  const models = [...usageByModelRollup(a)].sort((x, y) => usageTotal(y[1]) - usageTotal(x[1]));
  return section('a-usage', 'Usage', fmtTok(usageTotal(all)),
    h('table', { class: 'ptable usage-mini' },
      h('thead', null, h('tr', null, h('th', null, ''), h('th', { class: 'num' }, 'In'), h('th', { class: 'num' }, 'Out'),
        h('th', { class: 'num' }, 'Cache w'), h('th', { class: 'num' }, 'Cache r'), h('th', { class: 'num' }, 'Total'))),
      h('tbody', null,
        line('This agent', own, !kids.length),
        kids.length ? line('With sub-agents', all, true) : null,
        models.length > 1 ? models.map(([m, u]) => line(h('span', { class: 'gc-model', title: m }, shortModelName(m)), u, false)) : null)),
    h('div', { class: 'small muted' }, `${(all.responses || 0).toLocaleString()} model responses `, evBadge('reported', 'Reported by the provider on each model response')));
}

/** Repository / worktree / branch / HEAD / working-tree status of where the agent works. */
function locationOf(a, wt) {
  const others = Object.entries(a.worktrees || {}).filter(([id]) => id !== a.currentWorktreeId)
    .sort((x, y) => ms(y[1]) - ms(x[1]));
  if (!wt) {
    return h('div', null,
      h('div', { class: 'muted small' }, 'No Git worktree attributed yet: this agent has not touched a path inside a known repository.',
        a.cwd ? [' Working directory: ', h('span', { class: 'mono' }, a.cwd), ' (reported).'] : null),
      others.length ? kv([['Touched', h('div', { class: 'vlist-links' }, others.map(([id]) => wtLink(id)))]]) : null);
  }
  const repo = get('repository', wt.repositoryId);
  const counts = [['M', wt.modified], ['A', wt.added], ['D', wt.deletedFiles], ['?', wt.untracked], ['U', wt.conflicted]]
    .filter(([, n]) => n > 0).map(([k, n]) => `${n} ${k}`).join(' · ');
  const sync = [wt.ahead ? `↑${wt.ahead}` : null, wt.behind ? `↓${wt.behind}` : null].filter(Boolean).join(' ');
  return kv([
    ['Repository', repoLink(wt.repositoryId)],
    ['Worktree', h('span', null, wtLink(wt.id), ' ', h('span', { class: 'muted small' }, wt.isMain ? 'main worktree' : 'linked worktree'),
      h('div', { class: 'mono small muted' }, wt.root)), 'inferred'],
    ['Branch', wt.detached ? h('span', { class: 'muted' }, 'detached HEAD') : wt.branch, 'observed'],
    ['HEAD', wt.head ? shaLink(wt.id, wt.head, 12) : null, wt.head ? 'observed' : null],
    ['Status', h('span', null, counts || 'clean', sync ? h('span', { class: 'muted' }, '  ' + sync) : null,
      h('span', { class: 'muted small' }, ` · ${wt.lifecycle || ''}`), wt.operation ? h('b', null, ` · ${wt.operation} in progress`) : null), 'observed'],
    ['Main worktree', !wt.isMain && repo?.mainWorktree ? h('span', { class: 'mono small' }, repo.mainWorktree) : null],
    ['Also touched', others.length ? h('div', { class: 'vlist-links' }, others.map(([id, ts]) => h('span', null, wtLink(id), ' ', h('span', { class: 'muted small' }, agoEl(ts))))) : null],
  ]);
}

function miniEvent(e) {
  const d = describe(e);
  const el = h('div', { class: 'mini-ev' + (d.bad ? ' bad' : '') }, h('span', { class: 'mono muted' }, clock(e.ts)), h('span', { class: 'ev-icon ' + d.iconCls }, d.icon), h('span', { class: 'ev-text' }, d.text));
  el.addEventListener('click', () => openDrawer('output', { event: e }));
  return el;
}

// ---- worktree / repository ------------------------------------------------------------

function worktreeDetail(w) {
  const files = all('file').filter((f) => f.worktreeId === w.id).sort((x, y) => x.path.localeCompare(y.path));
  const commits = all('commit').filter((c) => c.worktreeId === w.id).sort((x, y) => ms(y.at) - ms(x.at));
  const agents = Object.entries(w.agents || {}).sort((x, y) => ms(y[1]) - ms(x[1]));
  const dirty = (w.modified || 0) + (w.added || 0) + (w.deletedFiles || 0) + (w.untracked || 0) + (w.conflicted || 0);
  return h('div', { class: 'detail' },
    h('div', { class: 'd-head' },
      h('div', { class: 'd-kicker' }, w.isMain ? 'Main worktree' : 'Linked worktree', h('span', { class: 'spacer' }), backBtn()),
      h('div', { class: 'd-title' }, '⑂ ', worktreeLabel(w)),
      h('div', { class: 'mono small muted' }, w.root),
      h('div', { class: 'd-status' }, dirty ? h('span', { class: 'pill st-waiting' }, `${dirty} uncommitted`) : h('span', { class: 'pill st-completed' }, '✓ clean'), evBadge('observed'),
        w.operation ? h('span', { class: 'pill st-failed' }, w.operation) : null, h('span', { class: 'muted small' }, 'updated ', agoEl(w.updatedAt)))),
    h('div', { class: 'd-actions' },
      btn('Diff', 'git diff of this worktree', () => openDrawer('diff', { kind: 'worktree', worktreeId: w.id, title: worktreeLabel(w) })),
      btn('Log', 'git log', () => openDrawer('diff', { kind: 'log', worktreeId: w.id, title: worktreeLabel(w) })),
      btn('Events', 'Timeline for this worktree', () => { setFilter('worktree', w.id); setTab('timeline'); })),
    conflictsFor((c) => c.worktreeId === w.id || (c.worktreeIds || []).includes(w.id)).length ? section('w-conf', 'Conflicts', null, conflictsFor((c) => c.worktreeId === w.id || (c.worktreeIds || []).includes(w.id))) : null,
    section('w-info', 'Git', null, kv([
      ['Repository', repoLink(w.repositoryId)],
      ['Branch', w.branch || (w.detached ? 'detached HEAD' : '—'), 'observed'],
      ['HEAD', w.head ? shaLink(w.id, w.head, 12) : null],
      ['Upstream', w.upstream ? `${w.upstream} (↑${w.ahead || 0} ↓${w.behind || 0})` : null],
      ['Changes', `${w.modified || 0} modified · ${w.added || 0} added · ${w.deletedFiles || 0} deleted · ${w.untracked || 0} untracked · ${w.conflicted || 0} conflicted`],
      ['Lifecycle', w.lifecycle],
      ['Flags', [w.locked && 'locked', w.prunable && 'prunable', w.removed && 'removed'].filter(Boolean).join(', ') || null],
    ])),
    section('w-agents', 'Agents', agents.length, agents.length ? agents.map(([id, ts]) => h('div', { class: 'ov-item' }, agentLink(id), h('span', { class: 'muted small' }, ' · last touch ', agoEl(ts), ' ', evBadge('inferred', 'Worktree attribution is inferred from tool-call paths')))) : h('div', { class: 'muted small' }, 'No agent activity seen in this worktree.')),
    section('w-files', 'Files', files.length, files.length ? files.map((f) => fileLine(f, null)) : h('div', { class: 'muted small' }, 'No tracked changes.')),
    commits.length ? section('w-commits', 'Commits', commits.length, commits.slice(0, 20).map(commitItem)) : null);
}

function repoDetail(r) {
  const wts = all('worktree').filter((w) => w.repositoryId === r.id);
  const agentIds = new Set(wts.flatMap((w) => Object.keys(w.agents || {})));
  return h('div', { class: 'detail' },
    h('div', { class: 'd-head' }, h('div', { class: 'd-kicker' }, 'Repository', h('span', { class: 'spacer' }), backBtn()), h('div', { class: 'd-title' }, r.name), h('div', { class: 'mono small muted' }, r.commonDir)),
    section('r-info', 'Details', null, kv([['ID', copyable(r.id)], ['Main worktree', r.mainWorktree ? h('span', { class: 'mono' }, r.mainWorktree) : null], ['Discovered', dateTime(r.discoveredAt)]])),
    section('r-wts', 'Worktrees', wts.length, wts.map((w) => h('div', { class: 'ov-item' }, wtLink(w.id), h('span', { class: 'muted small mono' }, ' ' + w.root)))),
    section('r-agents', 'Agents', agentIds.size, [...agentIds].map((id) => h('div', { class: 'ov-item' }, agentLink(id)))),
    conflictsFor((c) => c.repositoryId === r.id).length ? section('r-conf', 'Conflicts', null, conflictsFor((c) => c.repositoryId === r.id)) : null);
}

// ---- file ---------------------------------------------------------------------------------

function ensureHistory(f) {
  const hst = history.get(f.key);
  if (hst && (hst.loading || hst.seq === f.lastEventSeq)) return hst;
  const entry = { loading: true, seq: f.lastEventSeq, events: hst?.events || [], error: null, at: 0 };
  history.set(f.key, entry);
  api.events({ worktree: f.worktreeId, file: f.path, order: 'desc', limit: 300 })
    .then((list) => { entry.events = list; })
    .catch((e) => { entry.error = e; })
    .finally(() => { entry.loading = false; entry.at = Date.now(); invalidate('detail'); });
  return entry;
}

function fileDetail(f) {
  const st = fileStatusLetter(f);
  const hst = ensureHistory(f);
  const w = get('worktree', f.worktreeId);
  const edits = (hst.events || []).filter((e) => e.type.startsWith('file.') && e.type !== 'file.read' || e.type === 'git.file_status');
  const reads = (hst.events || []).filter((e) => e.type === 'file.read').length;
  const atts = Object.entries(f.agents || {}).sort((x, y) => ms(y[1].lastAt) - ms(x[1].lastAt));
  return h('div', { class: 'detail' },
    h('div', { class: 'd-head' },
      h('div', { class: 'd-kicker' }, 'File', h('span', { class: 'spacer' }), backBtn()),
      h('div', { class: 'd-title mono' }, basename(f.path)),
      h('div', { class: 'mono small muted' }, f.path),
      h('div', { class: 'd-status' },
        h('span', { class: 'fs fs-lg ' + st.cls, title: st.tip }, st.letter),
        f.gitStatus ? [h('span', null, 'git: ', h('b', null, f.gitStatus)), evBadge('observed', 'git status --porcelain')] : h('span', { class: 'muted small' }, 'not yet confirmed by git'),
        f.added != null || f.removed != null ? h('span', null, h('span', { class: 'add' }, `+${f.added ?? 0}`), ' ', h('span', { class: 'del' }, `−${f.removed ?? 0}`)) : null,
        f.lastChangeKind ? [h('span', { class: 'muted small' }, 'last ' + f.lastChangeKind), evBadge('reported')] : null)),
    h('div', { class: 'd-actions' },
      btn('Diff', 'git diff for this file', () => openDrawer('diff', { kind: 'file', worktreeId: f.worktreeId, path: f.path, title: f.path })),
      btn('Content', 'Current file content', () => openDrawer('diff', { kind: 'content', worktreeId: f.worktreeId, path: f.path, title: f.path })),
      btn('Log', 'git log for this file', () => openDrawer('diff', { kind: 'log', worktreeId: f.worktreeId, path: f.path, title: f.path })),
      btn('Events', 'Timeline for this worktree', () => { setFilter('worktree', f.worktreeId); setFilter('q', f.path); setTab('timeline'); })),
    conflictsFor((c) => c.path === f.path && (c.worktreeId === f.worktreeId || (c.worktreeIds || []).includes(f.worktreeId))).length
      ? section('f-conf', 'Conflicts', null, conflictsFor((c) => c.path === f.path && (c.worktreeId === f.worktreeId || (c.worktreeIds || []).includes(f.worktreeId)))) : null,
    section('f-agents', 'Agents touching this file', atts.length, atts.length ? atts.map(([id, att]) => h('div', { class: 'ov-item' }, agentLink(id),
      h('div', { class: 'small muted' }, `${att.edits} edit${att.edits === 1 ? '' : 's'} · last `, agoEl(att.lastAt), ' ', evBadge(att.evidence, 'Attribution evidence')))) : h('div', { class: 'muted small' }, 'No agent attributed (change observed by git only — the filesystem has no actor).')),
    section('f-info', 'Details', null, kv([
      ['Worktree', w ? h('span', null, wtLink(w.id), h('div', { class: 'mono small muted' }, w.root)) : f.worktreeId],
      ['Repository', f.repositoryId ? repoLink(f.repositoryId) : null],
      ['Old path', f.oldPath ? h('span', { class: 'mono' }, f.oldPath) : null],
      ['First change', dateTime(f.firstChangedAt)],
      ['Last change', dateTime(f.lastChangedAt)],
      ['Edits', String(f.editCount || 0), 'reported'],
      ['Reads', reads ? String(reads) : null, 'reported'],
    ])),
    section('f-history', 'Edit history', hst.loading && !edits.length ? '…' : edits.length,
      hst.error ? h('div', { class: 'warn small' }, `Could not load history: ${hst.error.message}`) : null,
      !edits.length && !hst.loading ? h('div', { class: 'muted small' }, 'No file events recorded for this path.') : null,
      edits.map(editItem)));
}

function editItem(e) {
  const d = e.data || {};
  const isGit = e.type === 'git.file_status';
  const body = h('div', { class: 'edit-body' });
  const head = h('div', { class: 'edit-head' },
    h('span', { class: 'mono muted' }, clock(e.ts)),
    isGit ? h('span', null, 'git observed ', h('b', null, d.status)) : h('span', null, h('b', null, e.type.slice(5)), d.tool ? ` via ${d.tool}` : ''),
    e.agentId ? agentLink(e.agentId) : isGit && d.attributedAgentId ? [agentLink(d.attributedAgentId), evBadge('inferred')] : null,
    d.added != null || d.removed != null ? h('span', { class: 'small' }, h('span', { class: 'add' }, `+${d.added ?? 0}`), ' ', h('span', { class: 'del' }, `−${d.removed ?? 0}`)) : null,
    h('span', { class: 'spacer' }), evBadge(e.evidence),
    d.patch ? h('span', { class: 'edit-toggle' }, '▾ patch') : null);
  if (d.patch) {
    let shown = false;
    const toggle = () => {
      shown = !shown;
      head.lastChild.textContent = shown ? '▴ patch' : '▾ patch';
      body.replaceChildren(shown ? renderDiff(d.patch, { fallbackName: e.filePath, compact: true }) : '');
    };
    head.classList.add('clickable');
    head.addEventListener('click', (ev) => { if (!ev.target.closest('.alink')) toggle(); });
  }
  return h('div', { class: 'edit-item' + (isGit ? ' git' : '') }, head, body);
}

// ---- conflict ----------------------------------------------------------------------------

function conflictDetail(c) {
  const fileKey = c.path && (c.worktreeId || c.worktreeIds?.[0]) ? (c.worktreeId || c.worktreeIds[0]) + '|' + c.path : null;
  return h('div', { class: 'detail' },
    h('div', { class: 'd-head d-conflict' },
      h('div', { class: 'd-kicker' }, 'Conflict · ', conflictKindLabel(c.kind), h('span', { class: 'spacer' }), backBtn()),
      h('div', { class: 'd-title' }, '⚠ ', conflictHeadline(c)),
      h('div', { class: 'd-desc' }, c.description),
      h('div', { class: 'muted small' }, 'detected ', agoEl(c.detectedAt), ' · derived from observed git state and reported/inferred attributions')),
    section('c-where', 'Where', null, kv([
      ['Path', c.path ? (fileKey && get('file', fileKey) ? link(c.path, () => select('file', fileKey), 'mono') : h('span', { class: 'mono' }, c.path)) : null],
      ['Repository', c.repositoryId ? repoLink(c.repositoryId) : null],
      ['Worktrees', (c.worktreeIds || []).length ? h('div', { class: 'vlist-links' }, c.worktreeIds.map(wtLink)) : c.worktreeId ? wtLink(c.worktreeId) : null],
    ])),
    section('c-agents', 'Agents involved', (c.agentIds || []).length, (c.agentIds || []).map((id) => {
      const a = get('agent', id);
      const f = fileKey ? get('file', fileKey) : null;
      const att = f?.agents?.[id];
      return h('div', { class: 'ov-item' }, agentLink(id),
        h('div', { class: 'small muted' }, a ? statusMeta(a).label : '', att ? [` · ${att.edits} edit(s) · last `, agoEl(att.lastAt), ' ', evBadge(att.evidence)] : null));
    })),
    section('c-what', 'What you can do', null, h('ul', { class: 'small plain' },
      h('li', null, 'Message one of the agents to stop touching the file (Messages section of the agent).'),
      h('li', null, 'Pause an agent while you sort it out (if its controls allow).'),
      h('li', null, 'Open the file diff to see both agents\' changes.'))),
    fileKey ? h('div', { class: 'd-actions' }, btn('Open file', '', () => select('file', fileKey)),
      btn('Diff', '', () => openDrawer('diff', { kind: 'file', worktreeId: c.worktreeId || c.worktreeIds[0], path: c.path, title: c.path }))) : null);
}

