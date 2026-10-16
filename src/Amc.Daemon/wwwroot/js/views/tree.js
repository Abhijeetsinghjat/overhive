// Left: agent tree (sessions → sub-agents, arbitrary depth) with keyboard navigation.
import { h, mount, keyed, agoEl, oneLine, norm, shortId } from '../util.js';
import { store, register, get, invalidate } from '../store.js';
import { ui, select, toggleCollapsed, isSelected, setCollapsed } from '../ui.js';
import { roots, children, agentName, statusMeta, worktreeOfAgent, worktreeLabel, worktreeRef, agentColor } from '../model.js';
import { agentPasses, filterCtx } from '../filters.js';
import { verifyMark } from './verification.js';

let visible = [];   // flattened [agent, depth, dim]

export function initTree() {
  const left = document.getElementById('left');
  const head = h('div', { class: 'panel-head' });
  const list = h('div', { class: 'tree', role: 'tree', tabindex: '0' });
  mount(left, head, list);

  register({
    name: 'tree', deps: ['agent', 'worktree', 'filters', 'selection', 'tree', 'session'],
    render() {
      const ctx = filterCtx();
      const filtering = !!(ctx.agentSet || ctx.f.status || ctx.f.repo || ctx.f.worktree || ctx.f.branch || ctx.q);
      visible = [];
      const walk = (a, depth, seen) => {
        if (seen.has(a.id)) return false;
        seen.add(a.id);
        const kids = children(a);
        const self = !filtering || agentPasses(a, ctx);
        const start = visible.length;
        visible.push([a, depth, !self]);
        let any = false;
        const collapsed = ui.collapsed.has(a.id) && !filtering;
        if (!collapsed) for (const c of kids) any = walk(c, depth + 1, seen) || any;
        if (!self && !any) { visible.length = start; return false; }
        return true;
      };
      const seen = new Set();
      for (const r of roots()) walk(r, 0, seen);

      const total = store.e.agent.size;
      mount(head, h('span', { class: 'panel-title' }, 'Agents'),
        h('span', { class: 'muted' }, filtering ? `${visible.filter((v) => !v[2]).length} of ${total}` : `${total}`),
        h('span', { class: 'spacer' }),
        iconBtn('⊟', 'Collapse all', () => setCollapsed([...store.e.agent.values()].filter((a) => (a.childIds || []).length).map((a) => a.id))),
        iconBtn('⊞', 'Expand all', () => setCollapsed([])));

      if (!total) {
        mount(list, emptyState());
        list._keyed = null;
        return;
      }
      if (!visible.length) {
        mount(list, h('div', { class: 'empty' }, 'No agents match the current filters.'));
        list._keyed = null;
        return;
      }
      keyed(list, visible, ([a]) => a.id,
        ([a, depth, dim]) => [a.status, a.statusReason, a.title, a.lastActivityAt, a.currentTool, a.currentAction, a.currentWorktreeId,
          depth, dim, ui.collapsed.has(a.id), isSelected('agent', a.id), ui.treeCursor === a.id, (a.childIds || []).length, a.tasks?.length, a.verification?.contradicted].join('|'),
        ([a, depth, dim]) => row(a, depth, dim));
    },
  });
}

function iconBtn(glyph, title, fn) {
  const b = h('button', { class: 'icon-btn', title, 'aria-label': title }, glyph);
  b.addEventListener('click', fn);
  return b;
}

function emptyState() {
  return h('div', { class: 'empty' },
    h('div', { class: 'empty-title' }, 'No agents yet'),
    h('p', null, 'Start any AI coding tool (Claude Code, Codex, Gemini CLI, Grok, Cursor, Copilot, Aider, …) in a repository: running tools are detected automatically.'),
    h('p', null, 'Or press ', h('code', null, '+ New session'), ' to start one in a terminal Overhive owns — with live output and messaging.'),
    h('p', null, 'Each tool signs in with your own account; Overhive never sees those credentials.'));
}

function row(a, depth, dim) {
  const kids = (a.childIds || []).length;
  const collapsed = ui.collapsed.has(a.id);
  const m = statusMeta(a);
  const wt = worktreeOfAgent(a);
  const caret = h('button', { class: 'caret' + (kids ? '' : ' none'), 'aria-label': collapsed ? 'Expand' : 'Collapse', tabindex: '-1' }, kids ? (collapsed ? '▸' : '▾') : '');
  caret.addEventListener('click', (e) => { e.stopPropagation(); if (kids) toggleCollapsed(a.id); });
  const isMain = norm(a.kind) === 'main';
  const current = a.currentTool ? `${a.currentTool}${a.currentAction && a.currentAction !== a.currentTool ? ': ' + oneLine(a.currentAction, 80) : ''}` : null;
  const el = h('div', {
    class: `tnode ${m.cls}${isSelected('agent', a.id) ? ' sel' : ''}${ui.treeCursor === a.id ? ' cursor' : ''}${dim ? ' dim' : ''}`,
    role: 'treeitem', 'aria-expanded': kids ? String(!collapsed) : null, 'aria-selected': isSelected('agent', a.id) ? 'true' : 'false',
    title: `${agentName(a)}\n${a.id}\nStatus: ${m.label}${a.statusReason ? ' — ' + a.statusReason : ''} (${norm(a.statusEvidence) || '?'})`,
  },
  h('div', { class: 'tn-line1' },
    h('span', { class: 'indent', style: { width: depth * 14 + 'px' } }),
    caret,
    h('span', { class: `sdot ${m.cls}`, 'aria-label': m.label }, m.glyph),
    h('span', { class: 'agent-swatch', style: { background: agentColor(a.id) } }),
    h('span', { class: 'tn-name' }, oneLine(agentName(a), 80)),
    h('span', { class: 'tn-ago' }, agoEl(a.lastActivityAt, '', ''))),
  h('div', { class: 'tn-line2', style: { paddingLeft: depth * 14 + 34 + 'px' } },
    h('span', { class: `tn-status ${m.cls}` }, m.label),
    h('span', { class: 'tn-kind' }, isMain ? 'main' : (a.agentType || 'sub')),
    kids ? h('span', { class: 'tn-kids', title: `${kids} sub-agent(s)` }, `↳${kids}`) : null,
    verifyMark(a),
    wt ? h('span', { class: 'chip', title: `Worktree ${wt.root} (inferred from tool paths)` }, '⑂ ', worktreeRef(wt)) :
      a.gitBranch ? h('span', { class: 'chip', title: 'Branch reported by the agent' }, '⑂ ', a.gitBranch) : null,
    null),
  current ? h('div', { class: 'tn-line3', style: { paddingLeft: depth * 14 + 34 + 'px' }, title: 'Current tool call — reported by the agent' },
    h('span', { class: 'tn-cur' }, '▸ ', current)) : null);
  el.addEventListener('click', () => select('agent', a.id));
  return el;
}

// ---- keyboard navigation (called from main.js) ----------------------------------------

export function treeKey(key) {
  if (!visible.length) return false;
  const idx = visible.findIndex(([a]) => a.id === ui.treeCursor);
  const move = (i) => {
    const [a] = visible[Math.max(0, Math.min(visible.length - 1, i))];
    ui.treeCursor = a.id;
    requestAnimationFrame(() => document.querySelector('#left .tree')?._keyed?.get(a.id)?.scrollIntoView({ block: 'nearest' }));
  };
  switch (key) {
    case 'j': case 'ArrowDown': move(idx < 0 ? 0 : idx + 1); break;
    case 'k': case 'ArrowUp': move(idx < 0 ? 0 : idx - 1); break;
    case 'Enter': if (idx >= 0) select('agent', visible[idx][0].id); return true;
    case 'h': case 'ArrowLeft': if (idx >= 0) { const a = visible[idx][0]; if ((a.childIds || []).length && !ui.collapsed.has(a.id)) toggleCollapsed(a.id); else if (a.parentId && get('agent', a.parentId)) ui.treeCursor = a.parentId; } break;
    case 'l': case 'ArrowRight': if (idx >= 0) { const a = visible[idx][0]; if (ui.collapsed.has(a.id)) toggleCollapsed(a.id); } break;
    default: return false;
  }
  // Re-render to move the cursor highlight.
  invalidate('tree');
  return true;
}

