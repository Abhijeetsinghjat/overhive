// Hive: the 3D view of every session as a honeycomb cluster of its sub-agents (WebGL, three.js).
// This module is small and always loaded: toolbar, data snapshot, labels and tooltips. The scene itself
// (views/hive-scene.js + the vendored three.js) is imported the first time the tab opens and disposed
// when it is left. Same data and filters as the 2D Graph tab, which stays the fallback.
import { h, mount, oneLine, norm, duration, ms, basename } from '../util.js';
import { store, register, get, invalidate } from '../store.js';
import { ui, select, setHive3d } from '../ui.js';
import { toolName, shortModelName, usageRollup, fmtTok, usageTotal, roots, children, agentName, statusMeta, statusOf, isTerminal, agentColor, worktreeOfAgent, worktreeLabel, repoName, evBadge } from '../model.js';
import { agentPasses, filterCtx } from '../filters.js';
import { guardAlertsFor } from './guard.js';
import { toast } from '../components/overlay.js';
import { reducedMotion, onReducedMotionChange, markWebglBroken } from '../hive3d.js';

let scene = null;          // the live scene (hive-scene.js instance) while the tab is open
let loading = null;        // pending import
let hideFinished = false;
let allLabels = load('hiveLabels', false);
let autoOrbit = load('hiveOrbit', false);

function load(key, dflt) { try { const v = localStorage.getItem('amc.' + key); return v == null ? dflt : JSON.parse(v); } catch { return dflt; } }
function save(key, v) { try { localStorage.setItem('amc.' + key, JSON.stringify(v)); } catch { /* private mode */ } }

export function initHive() {
  const root = document.getElementById('view-hive');
  const toolbar = h('div', { class: 'view-toolbar hive-toolbar' });
  const stage = h('div', { class: 'hive-stage', tabindex: '0', role: 'application', 'aria-label': 'Hive: 3D map of agents. Arrow keys move between agents, Enter opens one.' });
  const empty = h('div', { class: 'hive-empty', hidden: true });
  const status = h('div', { class: 'hive-status', hidden: true });
  const hint = h('div', { class: 'hive-hint' }, 'Drag to orbit · right-drag to pan · scroll to zoom · double-click to focus · ←↑↓→ between agents');
  stage.append(empty, status, hint);
  mount(root, toolbar, stage);

  const check = (label, title, value, onChange) => {
    const box = h('input', { type: 'checkbox', title, checked: value });
    box.addEventListener('change', () => onChange(box.checked));
    return [h('label', { class: 'toggle', title }, box, h('span', { class: 'lbl' }, label)), box];
  };
  const [hideLbl] = check('Hide finished', 'Hide sub-agents that have finished (sessions are always shown)', hideFinished, (v) => { hideFinished = v; invalidate('filters'); });
  const [labelsLbl] = check('All labels', 'Label every sub-agent (otherwise they are labelled when you zoom in, hover or select them)', allLabels, (v) => { allLabels = v; save('hiveLabels', v); scene?.setOptions({ allLabels }); });
  const [orbitLbl, orbitBox] = check('Auto-orbit', 'Slowly circle the hive when idle (off when the system asks for reduced motion)', autoOrbit, (v) => { autoOrbit = v; save('hiveOrbit', v); scene?.setOptions({ autoOrbit: autoOrbit && !reducedMotion() }); });
  const syncMotion = () => { orbitBox.disabled = reducedMotion(); scene?.setOptions({ autoOrbit: autoOrbit && !reducedMotion(), reducedMotion: reducedMotion() }); };
  onReducedMotionChange(syncMotion);
  orbitBox.disabled = reducedMotion();
  const btn = (label, title, f) => { const b = h('button', { class: 'btn btn-sm', title }, label); b.addEventListener('click', f); return b; };
  const lg = (cls, label, title) => h('span', { class: 'lg', title }, h('span', { class: 'hv-lg ' + cls }), label);
  mount(toolbar, h('span', { class: 'view-title' }, 'Hive'),
    hideLbl, labelsLbl, orbitLbl,
    h('div', { class: 'legend legend-nowrap hive-legend' },
      lg('hv-lg-run', 'running', 'Pulses while the agent works'), lg('hv-lg-wait', 'waiting', 'Amber glow: idle at its prompt, or blocked on you (pulsing)'),
      lg('hv-lg-fail', 'failed', 'Red glow'), lg('hv-lg-done', 'finished', 'Dimmed'),
      lg('hv-lg-arc', 'conflict', 'Red arc between agents in conflict'), h('span', { class: 'lg', title: 'Cell height follows token usage' }, '▮ tokens')),
    h('span', { class: 'spacer' }),
    btn('Fit', 'Fit the whole hive in view', () => scene?.fit()),
    btn('2D', 'Turn 3D off and use the 2D graph (turn it back on from the Graph tab)', () => setHive3d(false)));

  // Arrow keys move between agents while the stage has focus (and not the agent tree's j/k).
  stage.addEventListener('keydown', (e) => {
    if (!scene || e.ctrlKey || e.metaKey || e.altKey) return;
    const dir = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down' }[e.key];
    if (dir) {
      e.preventDefault(); e.stopPropagation();
      const cur = ui.selection?.kind === 'agent' ? ui.selection.id : null;
      const next = scene.neighbour(cur, dir);
      if (next) select('agent', next);
    } else if (e.key === 'Enter' && ui.selection?.kind === 'agent') { e.stopPropagation(); scene.focus(ui.selection.id); }
  });

  const hooks = {
    select: (id) => { select('agent', id); stage.focus({ preventScroll: true }); },
    tooltip,
    fatal(msg) {
      dispose();
      markWebglBroken();
      toast('3D Hive view unavailable', 'err', msg + ' — showing the 2D graph instead.');
      setHive3d(false, { persist: false });
    },
  };

  register({
    name: 'hive', deps: ['agent', 'worktree', 'repository', 'session', 'filters', 'selection', 'tab', 'conflict', 'guard', 'theme', 'hive3d'],
    render(d) {
      if (ui.tab !== 'hive') { dispose(); return; }
      const model = snapshot();
      empty.hidden = !!model.tree.length;
      if (!model.tree.length) {
        mount(empty, store.e.agent.size
          ? 'No agents match the current filters or session.'
          : [h('div', { class: 'empty-title' }, 'No agents yet'), 'Start any AI coding tool (Claude Code, Codex, Gemini CLI, …) or press ', h('code', null, '+ New session'), '. Each session becomes a cell of the hive, with its sub-agents around it.']);
      }
      if (scene) { scene.update(model, { theme: d.has('theme'), selection: d.has('selection') }); return; }
      if (loading) return;
      status.hidden = false; status.textContent = 'Loading the 3D view…';
      loading = import('./hive-scene.js')
        .then((mod) => {
          if (ui.tab !== 'hive') return;
          scene = mod.createHive(stage, hooks, { allLabels, autoOrbit: autoOrbit && !reducedMotion(), reducedMotion: reducedMotion() });
          if (scene) invalidate('hive3d');
        })
        .catch((err) => { console.error('hive: load failed', err); hooks.fatal(err?.message || String(err)); })
        .finally(() => { loading = null; status.hidden = true; });
    },
  });

  // Leaving the tab frees the GPU context and every geometry; coming back rebuilds (cheap).
  function dispose() { if (scene) { scene.dispose(); scene = null; } }
}

/** "3D" button for the 2D graph toolbar: turns the Hive back on (only offered when WebGL works). */
export function hiveToggleButton(available) {
  if (!available) return null;
  const b = h('button', { class: 'btn btn-sm', title: 'Show the 3D Hive view (and make it the default tab again)' }, '3D');
  b.addEventListener('click', () => setHive3d(true));
  return b;
}

// ---- data snapshot -------------------------------------------------------------------

function subtreeAny(a, pred, seen = new Set()) {
  if (seen.has(a.id)) return false; seen.add(a.id);
  return pred(a) || children(a).some((c) => subtreeAny(c, pred, seen));
}

function zoneOf(r) {
  const wt = worktreeOfAgent(r);
  if (wt) return { key: 'wt:' + wt.id, label: repoName(wt.repositoryId) || basename(wt.root), sub: worktreeLabel(wt), title: wt.root };
  if (r.currentRepositoryId) return { key: 'repo:' + r.currentRepositoryId, label: repoName(r.currentRepositoryId), sub: '', title: '' };
  return { key: 'none', label: 'Outside a repository', sub: '', title: 'Sessions whose working directory is not in a Git repository' };
}

function conflictCount(id) {
  let n = 0;
  for (const c of store.e.conflict.values()) if ((c.agentIds || []).includes(id)) n++;
  return n;
}

/** Everything the scene draws, from the store + filters (same rules as the 2D graph). */
function snapshot() {
  const ctx = filterCtx();
  const filtering = !!(ctx.agentSet || ctx.f.status || ctx.f.repo || ctx.f.worktree || ctx.f.branch || ctx.q);
  let forest = ui.session ? [get('agent', ui.session)].filter(Boolean) : roots();
  if (filtering) forest = forest.filter((r) => subtreeAny(r, (a) => agentPasses(a, ctx)));
  const visibleKids = (a) => children(a).filter((c) => !hideFinished || subtreeAny(c, (x) => !isTerminal(x)));
  const seen = new Set();
  const node = (a, isRoot) => {
    if (seen.has(a.id)) return null;
    seen.add(a.id);
    const tokens = usageTotal(isRoot ? usageRollup(a) : a.usage);
    const n = {
      id: a.id, a, isRoot, tokens, color: agentColor(a.id), status: statusOf(a),
      dim: filtering && !agentPasses(a, ctx),
      risk: a.risks?.critical || 0, contradicted: a.verification?.contradicted || 0, conflicts: conflictCount(a.id),
      kids: visibleKids(a).map((c) => node(c, false)).filter(Boolean),
    };
    n.label = isRoot ? rootLabel : subLabel;
    n.labelSig = [n.status, a.title, a.description, a.provider, a.agentType, a.model, fmtTok(tokens), n.risk, n.contradicted, n.conflicts, (a.childIds || []).length, guardSigLite(a)].join('|');
    return n;
  };
  const tree = forest.map((r) => { const n = node(r, true); n.zone = zoneOf(r); n.guard = guardLevel(r.id); return n; });

  const visible = new Set(seen);
  const conflicts = [];
  for (const c of store.e.conflict.values()) {
    const ids = (c.agentIds || []).filter((id) => visible.has(id));
    for (let i = 0; i < ids.length; i++) for (let j = i + 1; j < ids.length; j++) conflicts.push({ key: c.key + '|' + ids[i] + '|' + ids[j], from: ids[i], to: ids[j], title: c.path || c.kind });
  }
  return { tree, conflicts, selected: ui.selection?.kind === 'agent' ? ui.selection.id : null };
}

function guardLevel(rootId) {
  const alerts = guardAlertsFor(rootId);
  if (!alerts.length) return null;
  return alerts.some((x) => norm(x.severity) === 'critical') ? 'critical' : 'warn';
}
function guardSigLite(a) { return a.parentId ? '' : guardAlertsFor(a.id).map((x) => x.key + x.severity).join(','); }

// ---- labels + tooltip (crisp HTML, positioned by the scene's CSS2D layer) --------------

function badges(n) {
  return [
    n.conflicts ? h('span', { class: 'hv-b hv-b-conf', title: `${n.conflicts} conflict(s) involve this agent` }, '⚠ ' + n.conflicts) : null,
    n.risk ? h('span', { class: 'hv-b hv-b-risk', title: `${n.risk} critical risky action(s)` }, '⛔ ' + n.risk) : null,
    n.contradicted ? h('span', { class: 'hv-b hv-b-vf', title: `${n.contradicted} claim(s) contradicted by what Overhive observed` }, '✗ ' + n.contradicted) : null,
  ];
}

function rootLabel(n) {
  const a = n.a, m = statusMeta(a);
  const kids = (a.childIds || []).length;
  const g = guardLevel(a.id);
  return h('div', { class: `hv-label hv-root ${m.cls}` },
    h('div', { class: 'hv-l1' },
      h('span', { class: 'hv-tool', style: { '--ac': n.color } }, toolName(a.provider)),
      h('span', { class: 'hv-st' }, h('span', { class: 'glyph' }, m.glyph), m.label),
      g ? h('span', { class: `hv-b hv-b-guard${g === 'critical' ? ' crit' : ''}`, title: 'Runaway guard alert on this session' }, '⚠ runaway') : null,
      badges(n)),
    h('div', { class: 'hv-l2' }, oneLine(agentName(a), 56)),
    h('div', { class: 'hv-l3' }, '◈ ' + fmtTok(n.tokens), a.model ? ' · ' + shortModelName(a.model) : '', kids ? ` · ${kids} sub-agent${kids === 1 ? '' : 's'}` : ''));
}

function subLabel(n) {
  const a = n.a, m = statusMeta(a);
  return h('div', { class: `hv-label hv-sub ${m.cls}` },
    h('span', { class: 'glyph' }, m.glyph), h('span', { class: 'hv-sub-t' }, oneLine(a.title || a.agentType || agentName(a), 34)), badges(n));
}

function tooltip(id) {
  const a = get('agent', id);
  if (!a) return null;
  const m = statusMeta(a);
  const isMain = norm(a.kind) === 'main';
  const wt = worktreeOfAgent(a);
  const u = isMain ? usageRollup(a) : (a.usage || {});
  const st = statusOf(a);
  const act = a.currentTool ? `▸ ${a.currentTool} ${oneLine(a.currentAction && a.currentAction !== a.currentTool ? a.currentAction : a.currentFile || a.currentCommand || '', 80)}`
    : st === 'blocked' ? '⚠ Needs your input' + (a.statusReason ? ': ' + oneLine(a.statusReason, 70) : '')
      : st === 'failed' && a.statusReason ? '✕ ' + oneLine(a.statusReason, 80) : '';
  const n = { conflicts: conflictCount(a.id), risk: a.risks?.critical || 0, contradicted: a.verification?.contradicted || 0 };
  return h('div', { class: 'hv-tip-in' },
    h('div', { class: 'hv-tip-head' },
      h('span', { class: `pill ${m.cls}` }, h('span', { class: 'glyph' }, m.glyph), m.label), evBadge(a.statusEvidence, 'Applies to the status'),
      h('span', { class: 'spacer' }), h('span', { class: 'muted small' }, isMain ? toolName(a.provider) : a.agentType || 'sub-agent')),
    h('div', { class: 'hv-tip-title', style: { borderColor: agentColor(a.id) } }, oneLine(agentName(a), 90)),
    act ? h('div', { class: 'hv-tip-act' }, act) : null,
    h('div', { class: 'hv-tip-meta' },
      `◈ ${fmtTok(usageTotal(u))} tokens${isMain ? ' (session)' : ''}`, a.model ? ` · ${shortModelName(a.model)}` : '',
      ` · ${a.toolCount || 0} tools`, a.toolFailures ? ` · ${a.toolFailures} failed` : '',
      ' · ', duration((isTerminal(a) ? ms(a.completedAt || a.lastActivityAt) : Date.now()) - ms(a.startedAt || a.createdAt))),
    wt ? h('div', { class: 'hv-tip-meta' }, '⑂ ', repoName(wt.repositoryId), ' · ', worktreeLabel(wt)) : null,
    (n.conflicts || n.risk || n.contradicted) ? h('div', { class: 'hv-tip-badges' }, badges(n)) : null,
    h('div', { class: 'hv-tip-foot muted small' }, 'Click to open · double-click to focus'));
}
