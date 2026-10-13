// Bottom drawer (resizable): Diff / file content / log, Terminal, Tool output, Message thread.
import { h, mount, clock, dateTime, oneLine, b64encodeUtf8, duration } from '../util.js';
import { store, register, get, all, invalidate } from '../store.js';
import { ui, closeDrawer, openDrawer, setDrawerHeight, select } from '../ui.js';
import { api, wsUrl, textOf } from '../api.js';
import { renderDiff, renderContent } from '../components/diff.js';
import { describe, agentLabel, evBadge, agentName , shaLink } from '../model.js';
import { messageItem } from './detail.js';

const TABS = [['diff', 'Diff'], ['terminal', 'Terminal'], ['output', 'Tool output'], ['messages', 'Messages']];
let sideBySide = false;
const diffCache = new Map();   // payload key → { loading, data, error }

export function initDrawer() {
  const el = document.getElementById('drawer');
  const handle = h('div', { class: 'drawer-handle', title: 'Drag to resize' });
  const tabs = h('div', { class: 'drawer-tabs' });
  const body = h('div', { class: 'drawer-body' });
  mount(el, handle, h('div', { class: 'drawer-bar' }, tabs), body);

  handle.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    handle.setPointerCapture(e.pointerId);
    const startY = e.clientY, startH = ui.drawer.height;
    const move = (ev) => { const hgt = Math.max(120, Math.min(window.innerHeight - 200, startH + (startY - ev.clientY))); el.style.height = hgt + 'px'; ui.drawer.height = hgt; };
    const up = () => { handle.removeEventListener('pointermove', move); handle.removeEventListener('pointerup', up); setDrawerHeight(ui.drawer.height); term?.fit(); };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up);
  });

  let lastKey = null;
  register({
    name: 'drawer', deps: ['drawer', 'drawerSize', 'message', 'diffload', 'agent', 'terminal'],
    render(d) {
      el.classList.toggle('open', ui.drawer.open);
      el.style.height = ui.drawer.open ? ui.drawer.height + 'px' : '';
      document.getElementById('app').classList.toggle('drawer-open', ui.drawer.open);
      const tab = ui.drawer.tab;
      mount(tabs, TABS.map(([t, label]) => {
        const b = h('button', { class: 'dtab' + (tab === t ? ' on' : '') }, label);
        b.addEventListener('click', () => openDrawer(t));
        return b;
      }), h('span', { class: 'spacer' }), h('span', { class: 'muted small' }, subtitle()), closeBtn());
      if (!ui.drawer.open) { if (term) term.dispose(); body.replaceChildren(); lastKey = null; return; }
      const p = ui.drawer.payload[tab] || {};
      const key = tab + JSON.stringify(p);
      // Terminal keeps its live DOM; only rebuild when the target changes.
      if (tab === 'terminal') {
        if (key !== lastKey || !body.firstChild || (body._waiting && get('agent', p.agentId))) { lastKey = key; renderTerminal(body, p); }
        else if (d.has('drawerSize')) term?.fit();
        return;
      }
      if (term) term.dispose();
      if (tab === 'messages' && !d.has('message') && key === lastKey && !d.has('drawer')) return;
      if (tab === 'diff' && key === lastKey && !d.has('diffload') && !d.has('drawer')) return;
      if (tab === 'output' && key === lastKey && !d.has('drawer') && !d.has('agent')) return;
      lastKey = key;
      if (tab === 'diff') renderDiffTab(body, p);
      else if (tab === 'output') renderOutput(body, p);
      else if (tab === 'messages') renderMessages(body, p);
    },
  });
}

function closeBtn() {
  const b = h('button', { class: 'icon-btn', title: 'Close drawer (d)' }, '×');
  b.addEventListener('click', closeDrawer);
  return b;
}

function subtitle() {
  const p = ui.drawer.payload[ui.drawer.tab] || {};
  if (p.title) return oneLine(p.title, 80);
  if (p.agentId) return agentLabel(p.agentId);
  if (p.event) return `${p.event.type} · seq ${p.event.seq}`;
  return '';
}

// ---- diff / content / log -------------------------------------------------------------

function fetchFor(p) {
  switch (p.kind) {
    case 'agent': return api.agentDiff(p.agentId);
    case 'worktree': return api.worktreeDiff(p.worktreeId);
    case 'file': return api.worktreeDiff(p.worktreeId, p.path);
    case 'content': return api.worktreeFile(p.worktreeId, p.path);
    case 'log': return api.worktreeLog(p.worktreeId, p.path, 100);
    default: return Promise.resolve(null);
  }
}

function renderDiffTab(body, p) {
  if (!p.kind) {
    mount(body, h('div', { class: 'empty' }, 'Open a diff from an agent, worktree or file (Diff / Content / Log buttons in the detail panel).'));
    return;
  }
  const key = JSON.stringify(p);
  let c = diffCache.get(key);
  if (!c) {
    c = { loading: true };
    diffCache.set(key, c);
    fetchFor(p).then((data) => { c.data = data; }).catch((e) => { c.error = e; })
      .finally(() => { c.loading = false; invalidate('diffload'); });
  }
  const reload = h('button', { class: 'btn btn-sm' }, 'Reload');
  reload.addEventListener('click', () => { diffCache.delete(key); invalidate('diffload'); });
  const sbs = h('label', { class: 'toggle' }, h('input', { type: 'checkbox', checked: sideBySide }), h('span', null, 'Side by side'));
  sbs.firstChild.addEventListener('change', (e) => { sideBySide = e.target.checked; invalidate('diffload'); });
  const label = { agent: 'Agent diff', worktree: 'Worktree diff', file: 'File diff', content: 'File content', log: 'Git log' }[p.kind];
  const bar = h('div', { class: 'drawer-toolbar' }, h('b', null, label), h('span', { class: 'mono small muted' }, p.path || p.title || ''),
    h('span', { class: 'spacer' }), ['agent', 'worktree', 'file'].includes(p.kind) ? sbs : null, reload);
  let content;
  if (c.loading) content = h('div', { class: 'empty' }, 'Loading…');
  else if (c.error) content = h('div', { class: 'empty warn' }, c.error.notAvailable ? `${label} is not available: this daemon build does not implement the endpoint yet (${c.error.message}).` : `Failed: ${c.error.message}`);
  else if (p.kind === 'content') content = renderContent(textOf(c.data, 'content'));
  else if (p.kind === 'log') content = renderLog(c.data);
  else content = renderDiffData(c.data, p);
  mount(body, bar, h('div', { class: 'drawer-scroll' }, content));
}

function renderDiffData(data, p) {
  if (data == null) return h('div', { class: 'empty' }, 'No changes.');
  if (typeof data === 'string') return renderDiff(data, { sideBySide, fallbackName: p.path });
  if (Array.isArray(data) || Array.isArray(data.files)) {
    const list = Array.isArray(data) ? data : data.files;
    if (!list.length) return h('div', { class: 'empty' }, 'No changes attributed.');
    const text = list.map((f) => {
      const t = textOf(f, 'diff', 'patch');
      return /^diff --git|^--- /m.test(t) ? t : `--- a/${f.path || ''}\n+++ b/${f.path || ''}\n${t}`;
    }).join('\n');
    return renderDiff(text, { sideBySide });
  }
  const t = textOf(data, 'diff', 'patch');
  return renderDiff(t, { sideBySide, fallbackName: p.path });
}

function renderLog(data) {
  const list = Array.isArray(data) ? data : Array.isArray(data?.commits) ? data.commits : null;
  if (!list) return h('pre', { class: 'pre' }, textOf(data, 'log'));
  if (!list.length) return h('div', { class: 'empty' }, 'No commits.');
  return h('table', { class: 'ptable' },
    h('thead', null, h('tr', null, h('th', null, 'SHA'), h('th', null, 'Subject'), h('th', null, 'Author'), h('th', null, 'Date'))),
    h('tbody', null, list.map((c) => h('tr', null,
      h('td', null, shaLink(data?.worktreeId, String(c.sha || c.hash || ''), 10)),
      h('td', null, c.subject || c.message || ''),
      h('td', { class: 'muted' }, c.author || c.authorName || ''),
      h('td', { class: 'mono muted' }, c.at || c.date || c.committedAt ? dateTime(c.at || c.date || c.committedAt) : '')))));
}

// ---- terminal ---------------------------------------------------------------------------

let term = null;   // { dispose(), fit() }

async function renderTerminal(body, p) {
  if (term) term.dispose();
  const a = p.agentId ? get('agent', p.agentId) : null;
  const tid = p.terminalId || a?.terminalId;   // a session launched from the dashboard is opened by terminal id
  body._waiting = !a && !p.terminalId;
  if (!a && !tid) { mount(body, h('div', { class: 'empty' }, 'Select an agent and press "Terminal".')); return; }
  const t = tid ? get('terminal', tid) : null;
  if (!tid) { renderCommandOutput(body, a); return; }

  const status = h('span', { class: 'small muted' }, 'connecting…');
  const allow = h('label', { class: 'toggle', title: 'When on, keystrokes are sent to the agent\'s PTY' }, h('input', { type: 'checkbox', checked: !!p.allowInput }), h('span', null, 'Allow input'));
  const host = h('div', { class: 'term-host' });
  mount(body, h('div', { class: 'drawer-toolbar' }, h('b', null, 'Live terminal'), h('span', { class: 'mono small' }, tid),
    t ? h('span', { class: 'small muted' }, t.alive ? '· alive' : `· exited ${t.exitCode ?? ''}`) : null, evBadge('observed', 'Raw PTY output from a session started with agentctl run'),
    status, h('span', { class: 'spacer' }), allow), host);

  let mod, fitMod;
  try {
    [mod, fitMod] = await Promise.all([import('../../vendor/xterm/xterm.mjs'), import('../../vendor/xterm-addon-fit/addon-fit.mjs')]);
  } catch (e) {
    mount(host, h('div', { class: 'empty warn' }, 'Could not load the terminal emulator: ' + e.message));
    return;
  }
  const css = getComputedStyle(document.documentElement);
  const x = new mod.Terminal({
    convertEol: false, cursorBlink: false, scrollback: 5000, fontSize: 12,
    fontFamily: css.getPropertyValue('--mono').trim() || 'monospace',
    theme: { background: css.getPropertyValue('--term-bg').trim() || '#0b0e13', foreground: css.getPropertyValue('--text').trim() || '#e6edf3' },
  });
  const fit = new fitMod.FitAddon();
  x.loadAddon(fit);
  x.open(host);
  if (p.allowInput) x.focus();
  try { fit.fit(); } catch { /* not visible yet */ }
  let ws = null, closed = false, retry = 0;
  const connect = () => {
    ws = new WebSocket(wsUrl('/pty/view', { id: tid }));
    ws.binaryType = 'arraybuffer';
    ws.onopen = () => { status.textContent = '· connected'; retry = 0; };
    ws.onmessage = (m) => {
      if (typeof m.data === 'string') { try { const j = JSON.parse(m.data); if (j.t === 'exit') status.textContent = `· exited ${j.code ?? ''}`; } catch { x.write(m.data); } return; }
      x.write(new Uint8Array(m.data));
    };
    ws.onclose = (ev) => {
      if (closed) return;
      status.textContent = ev.code === 1006 && retry === 0 ? '· not available (endpoint /pty/view missing or terminal gone)' : `· disconnected (${ev.code})`;
      if (retry++ < (p.terminalId ? 15 : 5)) setTimeout(() => { if (!closed) { x.reset(); connect(); } }, Math.min(1000 * retry, 3000));
    };
  };
  connect();
  const sub = x.onData((data) => {
    if (!allow.firstChild.checked || !ws || ws.readyState !== 1) return;
    ws.send(JSON.stringify({ t: 'input', data: b64encodeUtf8(data) }));
  });
  const ro = new ResizeObserver(() => { try { fit.fit(); } catch { /* ignore */ } });
  ro.observe(host);
  term = {
    fit: () => { try { fit.fit(); } catch { /* ignore */ } },
    dispose: () => { closed = true; ro.disconnect(); sub.dispose(); try { ws?.close(); } catch { /* ignore */ } x.dispose(); term = null; },
  };
}

async function renderCommandOutput(body, a) {
  const list = h('div', { class: 'cmd-list' }, h('div', { class: 'empty' }, 'Loading command output…'));
  mount(body, h('div', { class: 'drawer-toolbar' }, h('b', null, 'Command output'), h('span', { class: 'small muted' }, agentName(a))),
    h('div', { class: 'term-note small' }, 'No live terminal for this agent — a live terminal is only available for sessions started with ',
      h('code', null, 'agentctl run -- claude'), '. Showing tool/command output reported by the agent instead.'),
    h('div', { class: 'drawer-scroll' }, list));
  try {
    const events = await api.events({ agent: a.id, type: 'command.*', order: 'desc', limit: 100 });
    const done = events.filter((e) => e.type !== 'command.started');
    if (!done.length) { mount(list, h('div', { class: 'empty' }, 'No command output recorded for this agent.')); return; }
    mount(list, done.map(cmdBlock));
  } catch (e) {
    mount(list, h('div', { class: 'empty warn' }, 'Could not load command events: ' + e.message));
  }
}

function cmdBlock(e) {
  const d = e.data || {};
  return h('div', { class: 'cmd' + (e.type === 'command.failed' ? ' cmd-bad' : '') },
    h('div', { class: 'cmd-head mono' }, h('span', { class: 'muted' }, clock(e.ts)), ' $ ', d.command,
      h('span', { class: 'spacer' }), h('span', { class: e.type === 'command.failed' ? 'del' : 'add' }, `exit ${d.exitCode ?? '?'}`), d.durationMs != null ? h('span', { class: 'muted' }, ' · ' + duration(d.durationMs)) : null),
    d.stdout ? h('pre', { class: 'pre term-out' }, d.stdout) : null,
    d.stderr ? h('pre', { class: 'pre term-out err' }, d.stderr) : null,
    !d.stdout && !d.stderr ? h('div', { class: 'small muted' }, '(no output captured)') : null);
}

// ---- output / messages ------------------------------------------------------------------

function renderOutput(body, p) {
  const e = p.event;
  if (!e) { mount(body, h('div', { class: 'empty' }, 'Click an event in Activity or Timeline to see its full payload here.')); return; }
  const d = e.data || {};
  const dsc = describe(e);
  const raw = h('pre', { class: 'pre' }, JSON.stringify(e, null, 2));
  const rawSec = h('details', { class: 'raw' }, h('summary', null, 'Raw event JSON'), raw);
  const agentBtn = e.agentId ? h('button', { class: 'link' }, agentLabel(e.agentId)) : null;
  agentBtn?.addEventListener('click', () => select('agent', e.agentId));
  mount(body, h('div', { class: 'drawer-scroll output' },
    h('div', { class: 'out-head' }, h('span', { class: 'ev-icon ' + dsc.iconCls }, dsc.icon), h('b', null, dsc.text)),
    h('table', { class: 'kv' },
      h('tr', null, h('td', null, 'Type'), h('td', { class: 'mono' }, e.type)),
      h('tr', null, h('td', null, 'Time'), h('td', { class: 'mono' }, dateTime(e.ts))),
      h('tr', null, h('td', null, 'Source / evidence'), h('td', null, e.source, ' ', evBadge(e.evidence))),
      agentBtn ? h('tr', null, h('td', null, 'Agent'), h('td', null, agentBtn)) : null,
      e.filePath ? h('tr', null, h('td', null, 'File'), h('td', { class: 'mono' }, e.filePath)) : null,
      h('tr', null, h('td', null, 'Seq'), h('td', { class: 'mono' }, String(e.seq)))),
    d.command ? h('div', null, h('div', { class: 'out-label' }, 'Command'), h('pre', { class: 'pre' }, d.command)) : null,
    d.stdout ? h('div', null, h('div', { class: 'out-label' }, 'stdout'), h('pre', { class: 'pre term-out' }, d.stdout)) : null,
    d.stderr ? h('div', null, h('div', { class: 'out-label' }, 'stderr'), h('pre', { class: 'pre term-out err' }, d.stderr)) : null,
    d.output ? h('div', null, h('div', { class: 'out-label' }, 'Output'), h('pre', { class: 'pre term-out' }, d.output)) : null,
    d.error ? h('div', null, h('div', { class: 'out-label' }, 'Error'), h('pre', { class: 'pre term-out err' }, d.error)) : null,
    d.text ? h('div', null, h('div', { class: 'out-label' }, 'Text'), h('pre', { class: 'pre' }, d.text)) : null,
    d.patch ? h('div', null, h('div', { class: 'out-label' }, 'Patch'), renderDiff(d.patch, { fallbackName: e.filePath })) : null,
    rawSec));
}

function renderMessages(body, p) {
  const a = p.agentId ? get('agent', p.agentId) : null;
  if (!a) { mount(body, h('div', { class: 'empty' }, 'Select an agent to see its message thread.')); return; }
  const msgs = all('message').filter((m) => m.agentId === a.id).sort((x, y) => Date.parse(x.sentAt) - Date.parse(y.sentAt));
  mount(body, h('div', { class: 'drawer-toolbar' }, h('b', null, 'Messages'), h('span', { class: 'small muted' }, agentName(a), ' · compose in the agent panel →')),
    h('div', { class: 'drawer-scroll thread' }, msgs.length ? msgs.map(messageItem) : h('div', { class: 'empty' }, 'No messages yet.')));
}

