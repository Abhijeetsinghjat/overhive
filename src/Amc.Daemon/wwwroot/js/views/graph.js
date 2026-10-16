// Graph: left-to-right tree of sessions → sub-agents (HTML cards over an SVG edge layer).
// Depth maps to columns and siblings stack vertically, so wide fan-outs (many sub-agents)
// stay compact; the view fits itself to the available width until the user zooms.
import { h, s, mount, keyed, agoEl, oneLine, norm, duration, ms } from '../util.js';
import { store, register, get, invalidate } from '../store.js';
import { ui, select, isSelected } from '../ui.js';
import { toolName, shortModelName, usageRollup, fmtTok, usageTotal, usageTitle, roots, children, agentName, statusMeta, statusOf, isActive, isTerminal, evBadge, agentColor, worktreeOfAgent, worktreeLabel, describe } from '../model.js';
import { agentPasses, filterCtx } from '../filters.js';
import { riskBadge } from './risks.js';
import { verifyMark } from './verification.js';
import { guardCardRow, guardSig } from './guard.js';

const W = 280, H = 150, COL_GAP = 56, ROW_GAP = 16, PAD = 20, BAND_GAP = 36;
const MIN_ZOOM = 0.35, MAX_ZOOM = 1.6, MIN_FIT = 0.6;

let zoom = 1;
let autoFit = true;      // fit to width until the user zooms manually
let hideFinished = false;
let size = { w: 0, h: 0 };

export function initGraph() {
  const root = document.getElementById('view-graph');
  const toolbar = h('div', { class: 'view-toolbar' });
  const scroller = h('div', { class: 'graph-scroll' });
  const sizer = h('div', { class: 'graph-sizer' });       // takes the scaled size so scrollbars are right
  const canvas = h('div', { class: 'graph-canvas' });
  const edges = s('svg', { class: 'graph-edges' });
  const cards = h('div', { class: 'graph-cards' });
  canvas.append(edges, cards);
  sizer.appendChild(canvas);
  scroller.appendChild(sizer);
  mount(root, toolbar, scroller);

  const zoomLabel = h('span', { class: 'muted small zoom-label' });
  const apply = () => {
    if (autoFit && size.w) {
      const avail = Math.max(200, scroller.clientWidth - 8);
      zoom = Math.max(MIN_FIT, Math.min(1, avail / size.w)); // below MIN_FIT cards get unreadable: scroll instead
    }
    canvas.style.transform = `scale(${zoom})`;
    sizer.style.width = Math.ceil(size.w * zoom) + 'px';
    sizer.style.height = Math.ceil(size.h * zoom) + 'px';
    zoomLabel.textContent = (autoFit ? 'fit ' : '') + Math.round(zoom * 100) + '%';
  };
  const setZoom = (z) => { autoFit = false; zoom = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, z)); apply(); };
  const zoomBtn = (label, title, f) => { const b = h('button', { class: 'btn btn-sm', title }, label); b.addEventListener('click', f); return b; };
  const fitBtn = zoomBtn('Fit', 'Fit the graph to the available width (default)', () => { autoFit = true; apply(); scroller.scrollTo(0, 0); });
  const hideBox = h('input', { type: 'checkbox', title: 'Hide sub-agents that have finished (sessions are always shown)' });
  hideBox.addEventListener('change', () => { hideFinished = hideBox.checked; invalidate('filters'); });
  const legend = h('div', { class: 'legend legend-nowrap' },
    h('span', { class: 'lg' }, h('span', { class: 'lg-dash' }), 'running'),
    h('span', { class: 'lg' }, h('span', { class: 'lg-solid' }), 'finished'),
    h('span', { class: 'lg lg-ev' }, evBadge('reported'), evBadge('observed'), evBadge('inferred')));
  mount(toolbar, h('span', { class: 'view-title' }, 'Agent graph'),
    h('label', { class: 'toggle' }, hideBox, h('span', { class: 'lbl' }, 'Hide finished')),
    legend, h('span', { class: 'spacer' }),
    zoomBtn('−', 'Zoom out (Ctrl+wheel)', () => setZoom(zoom - 0.1)), zoomLabel,
    zoomBtn('+', 'Zoom in (Ctrl+wheel)', () => setZoom(zoom + 0.1)), fitBtn);
  scroller.addEventListener('wheel', (e) => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    setZoom(zoom - Math.sign(e.deltaY) * 0.1);
  }, { passive: false });
  new ResizeObserver(() => { if (autoFit) apply(); }).observe(scroller);

  register({
    name: 'graph', deps: ['agent', 'worktree', 'session', 'filters', 'selection', 'events', 'tab', 'conflict', 'guard', 'guard-live'],
    render(d) {
      if (ui.tab !== 'graph') return;
      const ctx = filterCtx();
      const filtering = !!(ctx.agentSet || ctx.f.status || ctx.f.repo || ctx.f.worktree || ctx.f.branch || ctx.q);
      let forest = ui.session ? [get('agent', ui.session)].filter(Boolean) : roots();
      if (filtering) forest = forest.filter((r) => subtreeAny(r, (a) => agentPasses(a, ctx)));

      if (!forest.length) {
        cards._keyed = null;
        mount(cards, h('div', { class: 'empty graph-empty' }, store.e.agent.size
          ? 'No agents match the current filters or session.'
          : [h('div', { class: 'empty-title' }, 'No agents yet'), 'Start any AI coding tool (Claude Code, Codex, Gemini CLI, …) or press ', h('code', null, '+ New session'), '. Running tools are detected automatically; sessions appear here as a tree of their sub-agents.']));
        edges.replaceChildren();
        size = { w: 0, h: 0 }; sizer.style.width = sizer.style.height = '';
        canvas.style.width = '100%'; canvas.style.height = '100%'; canvas.style.transform = '';
        return;
      }

      // Layout: each session is a band; inside it a left-to-right tree. Leaves take the next
      // free row; a parent sits level with its first child. Columns = depth.
      const nodes = [];
      const links = [];
      let y = PAD, maxDepth = 0;
      const visibleKids = (a) => children(a).filter((c) => !hideFinished || subtreeAny(c, (x) => !isTerminal(x)));
      for (const r of forest) {
        const placed = new Set();
        const place = (a, depth) => {
          if (placed.has(a.id)) return null; placed.add(a.id);
          maxDepth = Math.max(maxDepth, depth);
          const node = { a, x: PAD + depth * (W + COL_GAP), y: 0, dim: filtering && !agentPasses(a, ctx) };
          nodes.push(node);
          const kids = visibleKids(a).map((c) => place(c, depth + 1)).filter(Boolean);
          if (kids.length) {
            node.y = kids[0].y; // aligned with its first child: roots stay top-left, never below the fold
            for (const k of kids) links.push({ from: node, to: k });
          } else { node.y = y; y += H + ROW_GAP; }
          return node;
        };
        place(r, 0);
        y += BAND_GAP - ROW_GAP;
      }
      const width = PAD * 2 + (maxDepth + 1) * W + maxDepth * COL_GAP;
      const height = y - BAND_GAP + PAD;
      size = { w: width, h: height };
      canvas.style.width = width + 'px';
      canvas.style.height = height + 'px';
      edges.setAttribute('width', width);
      edges.setAttribute('height', height);
      edges.setAttribute('viewBox', `0 0 ${width} ${height}`);

      // Edges: parent's right edge → child's left edge.
      const paths = [];
      for (const { from, to } of links) {
        const x1 = from.x + W, y1 = from.y + H / 2, x2 = to.x, y2 = to.y + H / 2;
        const mx = (x1 + x2) / 2;
        const m = statusMeta(to.a);
        paths.push(s('path', {
          d: `M${x1},${y1} C${mx},${y1} ${mx},${y2} ${x2},${y2}`,
          class: `edge ${m.cls}${isActive(to.a) ? ' edge-live' : ''}${to.dim ? ' dim' : ''}`,
        }));
        paths.push(s('circle', { cx: x2, cy: y2, r: 3, class: `edge-end ${m.cls}` }));
      }
      edges.replaceChildren(...paths);

      // Cards (keyed; rebuilt only if their content signature changed)
      keyed(cards, nodes, (n) => n.a.id, (n) => cardSig(n), (n) => card(n));
      if (!nodes.length) cards._keyed = null;
      for (const n of nodes) {
        const el = cards._keyed.get(n.a.id);
        el.style.left = n.x + 'px';
        el.style.top = n.y + 'px';
      }
      apply();
      if (d.has('selection') && ui.selection?.kind === 'agent') {
        const el = cards._keyed?.get(ui.selection.id);
        el?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
      }
    },
  });
}


function tokensSpan(a, isMain) {
  const u = isMain ? usageRollup(a) : (a.usage || {});
  return h('span', { class: 'gc-tok', title: usageTitle(u, isMain ? 'Session incl. sub-agents' : 'This agent') }, '◈ ' + fmtTok(usageTotal(u)));
}

function subtreeAny(a, pred, seen = new Set()) {
  if (seen.has(a.id)) return false; seen.add(a.id);
  return pred(a) || children(a).some((c) => subtreeAny(c, pred, seen));
}

function cardSig(n) {
  const a = n.a;
  const lt = store.lastTool.get(a.id);
  return [a.status, a.model, usageTotal(usageRollup(a)), (a.childIds || []).length, a.statusReason, a.statusEvidence, a.title, a.description, a.lastPrompt, a.currentTool, a.currentAction, a.toolCount, a.toolFailures,
    a.filesChanged, a.lastActivityAt, a.completedAt, JSON.stringify(a.tasks?.map((t) => t.state)), lt?.seq, n.dim, isSelected('agent', a.id),
    a.currentWorktreeId, (a.childIds || []).length, conflictFor(a.id), a.risks?.critical, a.verification?.contradicted, guardSig(a.id)].join('|');
}

function conflictFor(id) {
  let n = 0;
  for (const c of store.e.conflict.values()) if ((c.agentIds || []).includes(id)) n++;
  return n;
}

function card(n) {
  const a = n.a;
  const m = statusMeta(a);
  const st = statusOf(a);
  const isMain = norm(a.kind) === 'main';
  const wt = worktreeOfAgent(a);
  const lt = store.lastTool.get(a.id);
  const tasks = a.tasks || [];
  const done = tasks.filter((t) => norm(t.state) === 'completed').length;
  const nConf = conflictFor(a.id);

  let activity;
  if (a.currentTool) {
    activity = h('div', { class: 'gc-act live', title: 'Current tool call — reported by the agent' },
      h('span', { class: 'gc-act-k' }, '▸ ' + a.currentTool), ' ', oneLine(a.currentAction && a.currentAction !== a.currentTool ? a.currentAction : a.currentFile || a.currentCommand || '', 90));
  } else if (lt) {
    const dsc = describe(lt);
    activity = h('div', { class: 'gc-act', title: 'Last tool call seen (reported)' },
      h('span', { class: 'gc-act-k' }, 'last '), oneLine(dsc.text, 80), ' · ', agoEl(lt.ts));
  } else activity = h('div', { class: 'gc-act muted' }, a.toolCount ? `No tool call in the loaded event window` : isTerminal(a) ? 'No tool calls recorded' : 'No tool activity seen yet');

  const el = h('div', {
    class: `gcard ${m.cls}${isSelected('agent', a.id) ? ' sel' : ''}${n.dim ? ' dim' : ''}${isMain ? ' main' : ''}`,
    style: { width: W + 'px', height: H + 'px', borderTopColor: agentColor(a.id) },
    tabindex: '0', role: 'button', 'aria-label': `${agentName(a)}, ${m.label}`,
  },
  h('div', { class: 'gc-head' },
    h('span', { class: `pill ${m.cls}`, title: a.statusReason || m.label }, h('span', { class: 'glyph' }, m.glyph), m.label),
    evBadge(a.statusEvidence, 'Applies to the status'),
    nConf ? h('span', { class: 'pill st-blocked', title: `${nConf} conflict(s) involve this agent`, 'aria-label': 'conflict' }, '⚠') : null,
    riskBadge(a),
    verifyMark(a),
    h('span', { class: 'spacer' }),
    a.model ? h('span', { class: 'gc-model', title: `Model: ${a.model} (reported)` }, shortModelName(a.model)) : null,
    h('span', { class: 'gc-kind', title: isMain ? 'Tool' : 'Sub-agent type' }, isMain ? toolName(a.provider) : a.agentType || 'sub-agent')),
  h('div', { class: 'gc-title', title: agentName(a) }, oneLine(agentName(a), 70)),
  guardCardRow(a) || h('div', { class: 'gc-desc' }, oneLine(isMain ? a.lastPrompt || a.cwd || '' : a.description || a.lastPrompt || '', 140)),
  st === 'blocked' ? h('div', { class: 'gc-banner', title: a.statusReason || '' }, '⚠ Needs your input', a.statusReason ? ': ' + oneLine(a.statusReason, 70) : '') :
    st === 'failed' && a.statusReason ? h('div', { class: 'gc-banner bad' }, '✕ ', oneLine(a.statusReason, 80)) :
      st === 'disconnected' ? h('div', { class: 'gc-banner disc' }, '⌁ ', oneLine(a.statusReason || 'Process vanished', 80)) : activity,
  tasks.length ? h('div', { class: 'gc-tasks', title: `${done}/${tasks.length} tasks completed (reported by the agent's task list)` },
    h('span', { class: 'bar' }, h('span', { class: 'bar-fill', style: { width: (100 * done / tasks.length) + '%' } })),
    `${done}/${tasks.length} tasks`) : null,
  h('div', { class: 'gc-foot' },
    tokensSpan(a, isMain), ' · ',
    h('span', { title: 'Tool calls (reported)' }, `${a.toolCount || 0} tools`),
    ' · ', h('span', { class: a.toolFailures ? 'bad' : '', title: 'Failed tool calls' }, `${a.toolFailures || 0} failed`),
    ' · ', h('span', { title: 'Duration' }, duration((isTerminal(a) ? ms(a.completedAt || a.lastActivityAt) : Date.now()) - ms(a.startedAt || a.createdAt))),
    wt ? [' · ', h('span', { class: 'chip chip-sm', title: wt.root }, '⑂ ' + worktreeLabel(wt))] : null,
    h('span', { class: 'spacer' }),
    agoEl(a.lastActivityAt, '', ' ago')));
  el.addEventListener('click', () => select('agent', a.id));
  el.addEventListener('keydown', (e) => { if (e.key === 'Enter') select('agent', a.id); });
  return el;
}
