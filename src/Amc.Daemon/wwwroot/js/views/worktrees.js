// Worktrees & Files: worktrees (left) → agents (middle) → files (right), SVG edges coloured per agent.
import { h, s, mount, keyed, agoEl, oneLine, norm, basename, dirname, ms } from '../util.js';
import { store, register, get, all } from '../store.js';
import { ui, select, isSelected, setWt } from '../ui.js';
import { agentName, statusMeta, agentColor, evBadge, fileStatusLetter, worktreeLabel, repoName } from '../model.js';
import { filterCtx, worktreePasses, agentPasses, filePasses } from '../filters.js';

export function initWorktrees() {
  const root = document.getElementById('view-worktrees');
  const toolbar = h('div', { class: 'view-toolbar' });
  const scroller = h('div', { class: 'wt-scroll' });
  const grid = h('div', { class: 'wt-grid' });
  const svg = s('svg', { class: 'wt-edges' });
  const colW = h('div', { class: 'wt-col' });
  const colA = h('div', { class: 'wt-col' });
  const colF = h('div', { class: 'wt-col' });
  grid.append(svg, colW, colA, colF);
  scroller.appendChild(grid);
  mount(root, toolbar, scroller);

  const allT = toggle('All worktrees', 'Show worktrees without agent activity or changes too', ui.wt.allWorktrees, (v) => setWt('allWorktrees', v));
  const readsT = toggle('Show reads', 'Include files agents only read (from file.read events in the loaded window)', ui.wt.showReads, (v) => setWt('showReads', v));
  const legend = h('div', { class: 'legend' });
  mount(toolbar, h('span', { class: 'view-title' }, 'Worktrees & files'), allT, readsT, h('span', { class: 'spacer' }), legend);

  let edgeList = [];
  const drawEdges = () => {
    // Layout offsets, not client rects: cards may be mid-transform (tab entry, hover lift, tilt).
    const box = (el) => {
      let x = 0, y = 0;
      for (let n = el; n && n !== grid; n = n.offsetParent) { x += n.offsetLeft; y += n.offsetTop; }
      return { left: x, top: y, right: x + el.offsetWidth, height: el.offsetHeight };
    };
    const width = grid.scrollWidth, height = grid.scrollHeight;
    svg.setAttribute('width', width);
    svg.setAttribute('height', height);
    const paths = [];
    for (const e of edgeList) {
      const a = e.from._el, b = e.to._el;
      if (!a || !b || !a.isConnected || !b.isConnected) continue;
      const ra = box(a), rb = box(b);
      const x1 = ra.right, y1 = ra.top + Math.min(ra.height / 2, 22);
      const x2 = rb.left, y2 = rb.top + Math.min(rb.height / 2, 18);
      const dx = (x2 - x1) / 2;
      const p = s('path', { d: `M${x1},${y1} C${x1 + dx},${y1} ${x2 - dx},${y2} ${x2},${y2}`, class: 'wt-edge ' + e.cls });
      if (e.color) p.style.stroke = e.color;
      p._ends = [e.from.key, e.to.key];
      paths.push(p);
    }
    svg.replaceChildren(...paths);
  };
  new ResizeObserver(() => { if (ui.tab === 'worktrees') drawEdges(); }).observe(grid);

  const highlight = (key) => {
    for (const p of svg.children) {
      const on = key && p._ends.includes(key);
      p.classList.toggle('hl', !!on);
      p.classList.toggle('fade', !!key && !on);
    }
  };

  register({
    name: 'worktrees', deps: ['worktree', 'agent', 'file', 'repository', 'filters', 'selection', 'wtopts', 'tab', 'events', 'conflict'],
    render(d) {
      if (ui.tab !== 'worktrees') return;
      if (d.size === 1 && d.has('events') && !ui.wt.showReads) return;
      allT.querySelector('input').checked = ui.wt.allWorktrees;
      readsT.querySelector('input').checked = ui.wt.showReads;
      const ctx = filterCtx();

      // Files (changes + optionally reads)
      const files = all('file').filter((f) => filePasses(f, ctx));
      const fileNodes = files.map((f) => ({ key: 'f:' + f.key, f, read: false }));
      const readsByAgent = [];
      if (ui.wt.showReads) {
        const known = new Set(files.map((f) => f.key));
        for (const [key, agents] of store.reads) {
          const [wtId, ...rest] = key.split('|');
          const path = rest.join('|');
          const pseudo = { key, worktreeId: wtId, path, agents: {} };
          if (!filePasses(pseudo, ctx)) continue;
          if (!known.has(key)) fileNodes.push({ key: 'f:' + key, f: pseudo, read: true });
          for (const [aid] of agents) readsByAgent.push([aid, key]);
        }
      }

      // Worktrees
      const wts = all('worktree').filter((w) => !w.removed || ui.wt.allWorktrees).filter((w) => worktreePasses(w, ctx))
        .filter((w) => ui.wt.allWorktrees || Object.keys(w.agents || {}).length || fileNodes.some((n) => n.f.worktreeId === w.id) || dirtyCount(w))
        .sort((a, b) => repoName(a.repositoryId).localeCompare(repoName(b.repositoryId)) || (b.isMain - a.isMain) || a.root.localeCompare(b.root));
      const wtSet = new Set(wts.map((w) => w.id));
      const shownFiles = fileNodes.filter((n) => wtSet.has(n.f.worktreeId));
      const wtOrder = new Map(wts.map((w, i) => [w.id, i]));
      shownFiles.sort((a, b) => wtOrder.get(a.f.worktreeId) - wtOrder.get(b.f.worktreeId) || (a.read - b.read) || ms(b.f.lastChangedAt) - ms(a.f.lastChangedAt) || a.f.path.localeCompare(b.f.path));

      // Agents touching the shown worktrees / files
      const agentIds = new Set();
      for (const w of wts) for (const id of Object.keys(w.agents || {})) agentIds.add(id);
      for (const n of shownFiles) for (const id of Object.keys(n.f.agents || {})) agentIds.add(id);
      for (const [aid, key] of readsByAgent) if (wtSet.has(key.split('|')[0])) agentIds.add(aid);
      const agents = [...agentIds].map((id) => get('agent', id)).filter(Boolean).filter((a) => agentPasses(a, ctx));
      const firstWt = (a) => Math.min(...Object.keys(a.worktrees || {}).map((id) => wtOrder.get(id) ?? 999), 999);
      agents.sort((a, b) => firstWt(a) - firstWt(b) || ms(a.createdAt) - ms(b.createdAt));
      const agentSet = new Set(agents.map((a) => a.id));

      if (!wts.length) {
        colW._keyed = colA._keyed = colF._keyed = null;
        mount(colW, h('div', { class: 'empty' },
          h('div', { class: 'empty-title' }, store.e.worktree.size ? 'Nothing to show' : 'No repositories discovered yet'),
          store.e.worktree.size ? 'No worktree has agent activity or changes for the current filters. Toggle "All worktrees" to see every worktree.'
            : ['Repositories are discovered under the configured workspace roots (default ', h('code', null, '~/dev'), ') and from paths agents touch.']));
        colA.replaceChildren(); colF.replaceChildren(); svg.replaceChildren();
        return;
      }

      // Columns (keyed)
      const colHead = (title, n) => h('div', { class: 'wt-colhead' }, title, h('span', { class: 'muted' }, ` ${n}`));
      const wItems = [{ head: true, key: 'h', label: colHead('Worktrees', wts.length) }, ...wts.map((w) => ({ key: 'w:' + w.id, w }))];
      const aItems = [{ head: true, key: 'h', label: colHead('Agents', agents.length) }, ...agents.map((a) => ({ key: 'a:' + a.id, a }))];
      const fItems = [{ head: true, key: 'h', label: colHead('Files', shownFiles.length) }, ...shownFiles];

      keyed(colW, wItems, (x) => x.key, (x) => x.head ? 'h' + wts.length : wtSig(x.w), (x) => x.head ? x.label : wtCard(x.w, highlight));
      keyed(colA, aItems, (x) => x.key, (x) => x.head ? 'h' + agents.length : agentSig(x.a), (x) => x.head ? x.label : agentCard(x.a, highlight));
      keyed(colF, fItems, (x) => x.key, (x) => x.head ? 'h' + shownFiles.length : fileSig(x), (x) => x.head ? x.label : fileCard(x, highlight));

      const nodeOf = (col, key) => ({ key, _el: col._keyed.get(key) });
      edgeList = [];
      for (const a of agents) {
        for (const wid of Object.keys(a.worktrees || {})) {
          if (!wtSet.has(wid)) continue;
          edgeList.push({ from: nodeOf(colW, 'w:' + wid), to: nodeOf(colA, 'a:' + a.id), cls: 'e-wt' });
        }
      }
      for (const n of shownFiles) {
        for (const [aid, att] of Object.entries(n.f.agents || {})) {
          if (!agentSet.has(aid)) continue;
          const ev = norm(att.evidence);
          edgeList.push({ from: nodeOf(colA, 'a:' + aid), to: nodeOf(colF, n.key), cls: 'e-edit' + (ev === 'inferred' ? ' e-inferred' : ''), color: agentColor(aid) });
        }
      }
      for (const [aid, key] of readsByAgent) {
        if (!agentSet.has(aid) || !wtSet.has(key.split('|')[0])) continue;
        const f = get('file', key);
        if (f && f.agents && f.agents[aid]) continue; // edit edge already shown
        edgeList.push({ from: nodeOf(colA, 'a:' + aid), to: nodeOf(colF, 'f:' + key), cls: 'e-read' });
      }
      mount(legend,
        agents.slice(0, 8).map((a) => h('span', { class: 'lg' }, h('span', { class: 'lg-line', style: { background: agentColor(a.id) } }), 'edited (', oneLine(agentName(a), 22), ')')),
        agents.length > 8 ? h('span', { class: 'lg muted' }, `+${agents.length - 8}`) : null,
        h('span', { class: 'lg' }, h('span', { class: 'lg-line lg-dashed' }), 'inferred'),
        ui.wt.showReads ? h('span', { class: 'lg' }, h('span', { class: 'lg-line lg-dotted' }), 'read') : null);
      drawEdges();
    },
  });
}

function toggle(label, title, checked, onChange) {
  const input = h('input', { type: 'checkbox', checked });
  input.addEventListener('change', () => onChange(input.checked));
  return h('label', { class: 'toggle', title }, input, h('span', null, label));
}

const dirtyCount = (w) => (w.modified || 0) + (w.added || 0) + (w.deletedFiles || 0) + (w.untracked || 0) + (w.conflicted || 0);

function hover(el, key, highlight) {
  el.addEventListener('mouseenter', () => highlight(key));
  el.addEventListener('mouseleave', () => highlight(null));
}

function wtSig(w) {
  return JSON.stringify([w.branch, w.head, w.modified, w.added, w.deletedFiles, w.untracked, w.conflicted, w.ahead, w.behind, w.operation, w.removed, w.locked,
    Object.keys(w.agents || {}).length, isSelected('worktree', w.id), filesIn(w.id), commitsIn(w.id)]);
}
const filesIn = (id) => { let n = 0; for (const f of store.e.file.values()) if (f.worktreeId === id) n++; return n; };
const commitsIn = (id) => { let n = 0; for (const c of store.e.commit.values()) if (c.worktreeId === id) n++; return n; };

function wtCard(w, highlight) {
  const dirty = dirtyCount(w);
  const nAgents = Object.keys(w.agents || {}).length;
  const chips = [];
  if (w.modified) chips.push(h('span', { class: 'fs fs-m', title: 'modified' }, `${w.modified}M`));
  if (w.added) chips.push(h('span', { class: 'fs fs-a', title: 'added' }, `${w.added}A`));
  if (w.deletedFiles) chips.push(h('span', { class: 'fs fs-d', title: 'deleted' }, `${w.deletedFiles}D`));
  if (w.untracked) chips.push(h('span', { class: 'fs fs-a', title: 'untracked' }, `${w.untracked}?`));
  if (w.conflicted) chips.push(h('span', { class: 'fs fs-u', title: 'conflicted' }, `${w.conflicted}U`));
  const el = h('div', { class: 'wt-card wt-w' + (isSelected('worktree', w.id) ? ' sel' : '') + (w.removed ? ' dim' : ''), tabindex: '0' },
    h('div', { class: 'wc-row' },
      h('span', { class: 'wc-branch' }, '⑂ ', worktreeLabel(w)),
      h('span', { class: 'tag' + (w.isMain ? ' tag-main' : '') }, w.isMain ? 'main worktree' : 'linked worktree'),
      w.operation ? h('span', { class: 'tag tag-bad' }, w.operation) : null,
      w.locked ? h('span', { class: 'tag' }, 'locked') : null,
      w.removed ? h('span', { class: 'tag tag-bad' }, 'removed') : null),
    h('div', { class: 'wc-path mono', title: w.root }, '\u200e' + w.root),
    h('div', { class: 'wc-row small' },
      h('span', { class: 'muted' }, repoName(w.repositoryId)),
      w.head ? h('span', { class: 'mono muted', title: 'HEAD (observed)' }, w.head.slice(0, 7)) : null,
      w.ahead || w.behind ? h('span', { class: 'muted', title: `vs ${w.upstream || 'upstream'}` }, `↑${w.ahead || 0} ↓${w.behind || 0}`) : null),
    h('div', { class: 'wc-row small' },
      dirty ? h('span', { class: 'wc-dirty' }, `${dirty} uncommitted`) : h('span', { class: 'wc-clean' }, '✓ clean'),
      ...chips),
    h('div', { class: 'wc-row small muted' }, `${commitsIn(w.id)} commits · ${filesIn(w.id)} files · ${nAgents} agent${nAgents === 1 ? '' : 's'}`));
  el.addEventListener('click', () => select('worktree', w.id));
  hover(el, 'w:' + w.id, highlight);
  return el;
}

function agentSig(a) {
  return [a.status, a.title, a.currentTool, a.filesChanged, a.lastActivityAt, isSelected('agent', a.id)].join('|');
}

function agentCard(a, highlight) {
  const m = statusMeta(a);
  const el = h('div', { class: `wt-card wt-a ${m.cls}` + (isSelected('agent', a.id) ? ' sel' : ''), style: { borderLeftColor: agentColor(a.id) }, tabindex: '0' },
    h('div', { class: 'wc-row' },
      h('span', { class: `sdot ${m.cls}`, title: m.label }, m.glyph),
      h('span', { class: 'wc-name' }, oneLine(agentName(a), 46)),
      h('span', { class: 'spacer' }), h('span', { class: 'tn-ago' }, agoEl(a.lastActivityAt, '', ''))),
    h('div', { class: 'wc-row small' },
      h('span', { class: `tn-status ${m.cls}` }, m.label),
      h('span', { class: 'tn-kind' }, norm(a.kind) === 'main' ? 'main' : a.agentType || 'sub'),
      h('span', { class: 'muted' }, `${a.filesChanged || 0} files`)),
    a.currentTool ? h('div', { class: 'wc-cur small', title: 'Current tool (reported)' }, '▸ ', a.currentTool, ' ', oneLine(a.currentFile || '', 40)) : null);
  el.addEventListener('click', () => select('agent', a.id));
  hover(el, 'a:' + a.id, highlight);
  return el;
}

function fileSig(n) {
  const f = n.f;
  return JSON.stringify([n.read, f.gitStatus, f.lastChangeKind, f.added, f.removed, f.editCount, f.agents, isSelected('file', f.key), conflicted(f)]);
}

function conflicted(f) {
  for (const c of store.e.conflict.values()) if (c.path === f.path && (c.worktreeId === f.worktreeId || (c.worktreeIds || []).includes(f.worktreeId))) return true;
  return false;
}

function fileCard(n, highlight) {
  const f = n.f;
  const st = n.read ? { letter: 'R', cls: 'fs-r', tip: 'Read only (no change reported)' } : fileStatusLetter(f);
  const atts = Object.entries(f.agents || {});
  const conf = !n.read && conflicted(f);
  const el = h('div', { class: 'wt-card wt-f' + (isSelected('file', f.key) ? ' sel' : '') + (n.read ? ' read' : '') + (conf ? ' conflict' : ''), tabindex: '0', title: f.path },
    h('div', { class: 'wc-row' },
      h('span', { class: 'fs ' + st.cls, title: st.tip }, st.letter),
      h('span', { class: 'wc-file' }, basename(f.path)),
      conf ? h('span', { class: 'tag tag-bad', title: 'Conflict: several agents touch this file' }, '⚠') : null,
      h('span', { class: 'spacer' }),
      !n.read && (f.added != null || f.removed != null) ? h('span', { class: 'small nowrap', title: 'Lines (git numstat when observed)' },
        h('span', { class: 'add' }, `+${f.added ?? 0}`), ' ', h('span', { class: 'del' }, `−${f.removed ?? 0}`)) : null),
    h('div', { class: 'wc-row small' },
      h('span', { class: 'wc-dir mono muted' }, '\u200e' + (dirname(f.path) || '.')),
      h('span', { class: 'spacer' }),
      atts.map(([aid, att]) => h('span', { class: 'att', title: `${agentName(get('agent', aid)) || aid}: ${att.edits} edit(s), ${norm(att.evidence)}` },
        h('span', { class: 'agent-swatch', style: { background: agentColor(aid) } }), norm(att.evidence)[0]))));
  el.addEventListener('click', () => { if (!n.read) select('file', f.key); else select('worktree', f.worktreeId); });
  hover(el, n.key, highlight);
  return el;
}
