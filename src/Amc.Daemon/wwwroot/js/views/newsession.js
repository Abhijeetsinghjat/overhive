// "New session": start any configured AI tool in a Overhive-owned terminal and open it in the drawer.
// Each tool signs in with its own account; Overhive never sees those credentials.
import { h, mount } from '../util.js';
import { all } from '../store.js';
import { openDrawer } from '../ui.js';
import { api } from '../api.js';
import { confirmModal, toast } from '../components/overlay.js';
import { repoName } from '../model.js';

let toolsCache = null;
export async function tools() {
  if (!toolsCache) { try { toolsCache = await api.tools(); } catch { toolsCache = []; } }
  return toolsCache;
}

export async function newSessionDialog() {
  const list = await tools();
  const installed = list.filter((t) => t.installed);
  const tool = h('select', { class: 'select ns-field', id: 'ns-tool' },
    (installed.length ? installed : list).map((t) => h('option', { value: t.id }, `${t.name}${t.installed ? '' : ' (not installed)'}`)),
    installed.length && installed.length < list.length
      ? h('optgroup', { label: 'Not installed' }, list.filter((t) => !t.installed).map((t) => h('option', { value: t.id, disabled: true }, t.name)))
      : null);
  const wts = all('worktree').filter((w) => !w.removed)
    .sort((a, b) => repoName(a.repositoryId).localeCompare(repoName(b.repositoryId)) || (b.isMain - a.isMain) || a.root.localeCompare(b.root));
  const dir = dirPicker(wts);
  const model = h('input', { class: 'input ns-field', id: 'ns-model', list: 'ns-models', placeholder: 'Default model', spellcheck: 'false' });
  const models = h('datalist', { id: 'ns-models' });
  const prompt = h('input', { class: 'input ns-field', id: 'ns-prompt', placeholder: 'Optional first message', maxlength: 4000 });
  const modelRow = h('label', null, h('span', null, 'Model'), model, models);
  const promptRow = h('label', null, h('span', null, 'First message'), prompt);
  const hint = h('div', { class: 'small muted' });
  const sync = () => {
    const t = list.find((x) => x.id === tool.value);
    modelRow.hidden = !t?.canPickModel;
    promptRow.hidden = !t?.takesPrompt;
    models.replaceChildren(...(t?.models || []).map((m) => h('option', { value: m })));
    hint.textContent = `Runs ${t ? t.command : 'the tool'} in a terminal Overhive owns, signed in with your own ${t ? t.name : ''} account. ` +
      'It opens below; type into it directly. Answer any first-run questions (such as trusting the folder) there.';
  };
  tool.addEventListener('change', sync);
  sync();
  const wt = worktreeSection(dir);
  const body = h('div', { class: 'ns-form' },
    h('label', null, h('span', null, 'Tool'), tool),
    h('div', { class: 'ns-row' }, h('label', { for: 'ns-dir' }, h('span', null, 'Working directory')), dir.el), wt.el, modelRow, promptRow, hint);

  if (!list.length) { toast('Could not load the tool list', 'err'); return; }
  const go = await confirmModal({ title: 'New agent session', body, confirmLabel: 'Start session', wide: true, onReady: wt.bindConfirm });
  wt.dispose();
  if (!go) return;
  const t = list.find((x) => x.id === tool.value);
  const cwd = dir.input.value.trim();
  const worktree = wt.spec();
  if (worktree) toast('Creating the worktree…', 'info', worktree.runSetup ? 'Then running the setup command; this can take a while.' : '');
  try {
    const r = await api.launchSession({ tool: tool.value, cwd, worktree, model: (!modelRow.hidden && model.value.trim()) || null, prompt: (!promptRow.hidden && prompt.value.trim()) || null });
    toast(`${t?.name || 'Session'} starting`, 'ok', [r.detail || '', ...(r.warnings || [])].filter(Boolean).join(' '));
    openDrawer('terminal', { terminalId: r.terminalId, title: `${t?.name || 'Session'} — ${r.worktreePath || cwd}`, allowInput: true });
  } catch (e) {
    toast('Could not start the session', 'err', e.message);
  }
}

/**
 * "Run in: this folder | a new worktree" (shown once the working directory is inside a git repository). For a new
 * worktree: branch name (new, or an existing one), base (default: the remote default branch, e.g. origin/main),
 * location (default from the server, editable), "fetch base first" and the repository's setup command (both off by
 * default). Every change is validated by the daemon (GET /api/new-worktree/plan); the confirm button stays disabled
 * while something is invalid.
 */
function worktreeSection(dir) {
  const here = h('input', { type: 'radio', name: 'ns-runin', value: 'here', checked: true });
  const fresh = h('input', { type: 'radio', name: 'ns-runin', value: 'new', id: 'ns-runin-new' });
  const runIn = h('div', { class: 'ns-runin', role: 'radiogroup', 'aria-label': 'Run in' },
    h('span', { class: 'ns-runin-label' }, 'Run in'),
    h('label', { class: 'ns-check' }, here, 'this folder'),
    h('label', { class: 'ns-check' }, fresh, 'a new worktree'));
  const branch = h('input', { class: 'input mono ns-field', id: 'ns-wt-branch', placeholder: 'e.g. feat/login-form', spellcheck: 'false', autocomplete: 'off', maxlength: 200 });
  const existing = h('input', { type: 'checkbox', id: 'ns-wt-existing' });
  const base = refPicker();
  const baseRow = h('div', { class: 'ns-row' }, h('label', { for: 'ns-wt-base' }, h('span', null, 'Base (the new branch starts here)')), base.el);
  const path = h('input', { class: 'input mono ns-field', id: 'ns-wt-path', spellcheck: 'false', autocomplete: 'off', placeholder: 'Default location' });
  const fetch = h('input', { type: 'checkbox', id: 'ns-wt-fetch' });
  const fetchRow = h('label', { class: 'ns-check' }, fetch, 'Fetch the base from its remote first (network)');
  const setup = h('input', { type: 'checkbox', id: 'ns-wt-setup' });
  const setupText = h('code', null, '');
  const setupRow = h('label', { class: 'ns-check', hidden: true }, setup, 'Run setup first: ', setupText);
  const status = h('div', { class: 'ns-wt-status small', 'aria-live': 'polite' });
  const fields = h('div', { class: 'ns-wt', hidden: true },
    h('label', null, h('span', null, 'Branch name'), branch),
    h('label', { class: 'ns-check' }, existing, 'Use an existing branch'),
    baseRow,
    h('label', null, h('span', null, 'Location'), path),
    fetchRow, setupRow, status,
    h('div', { class: 'small muted' }, 'A new worktree is a fresh checkout: ignored files such as node_modules or .env are not there. ',
      'Configure NewWorktree:SetupCommand for this repository to install dependencies before the agent starts.'));
  const el = h('div', { class: 'ns-wt-wrap', hidden: true }, runIn, fields);

  let plan = null;          // last plan for the current folder
  let planFor = '';         // folder the plan belongs to
  let pathTouched = false;  // the user typed a location: keep it
  let ok = null;            // confirm button
  let seq = 0;
  let timer = 0;
  let valid = true;

  const isNew = () => !el.hidden && fresh.checked;
  const setValid = (v) => { valid = v; if (ok) ok.disabled = !v; };
  const show = (lines, bad) => {
    mount(status, lines.map(([cls, text]) => h('div', { class: cls, text })));
    setValid(!bad);
  };

  const refresh = async () => {
    const cwd = dir.input.value.trim();
    const my = ++seq;
    if (cwd !== planFor) {
      plan = null; planFor = cwd; pathTouched = false;
      if (!cwd) { el.hidden = true; setValid(true); return; }
      let p = null;
      try { p = await api.newWorktreePlan({ cwd }); } catch { p = null; }
      if (my !== seq) return;
      plan = p;
      el.hidden = !p;
      if (!p) { setValid(true); return; }
      base.setItems(p.localBranches || [], p.remoteBranches || []);
      base.input.value = p.defaultBase || '';
      base.input.placeholder = p.defaultBase || 'HEAD';
      setupRow.hidden = !p.setupCommand;
      setupText.textContent = p.setupCommand || '';
      if (!p.setupCommand) setup.checked = false;
    }
    sync();
    if (!isNew()) { setValid(true); return; }
    const name = branch.value.trim();
    if (!name) { show([['muted', 'Enter a branch name.']], true); return; }
    let r;
    try {
      r = await api.newWorktreePlan({ cwd, branch: name, base: existing.checked ? null : base.input.value.trim(), path: pathTouched ? path.value.trim() : null, existing: existing.checked });
    } catch (e) {
      if (my === seq) show([['err', e.message]], true);
      return;
    }
    if (my !== seq) return;
    if (!pathTouched) path.value = r.path || '';
    const lines = [];
    if (r.branchError) lines.push(['err', r.branchError]);
    if (!existing.checked && r.baseError && !fetch.checked) lines.push(['err', r.baseError]);
    if (r.pathError) lines.push(['err', r.pathError]);
    (r.warnings || []).forEach((w) => lines.push(['warn', w]));
    const bad = !!(r.branchError || r.pathError || (!existing.checked && r.baseError && !fetch.checked));
    if (!bad) lines.unshift(['muted', existing.checked ? `Creates ${r.path} on the existing branch ${name}.` : `Creates ${r.path} on a new branch ${name}, branched off ${r.base}.`]);
    show(lines, bad);
  };
  const later = (ms = 250) => { clearTimeout(timer); timer = setTimeout(refresh, ms); };
  const sync = () => {
    fields.hidden = !fresh.checked;
    baseRow.hidden = existing.checked;
    fetchRow.hidden = existing.checked;
  };

  [here, fresh, existing, fetch].forEach((x) => x.addEventListener('change', () => later(0)));
  branch.addEventListener('input', () => later());
  path.addEventListener('input', () => { pathTouched = path.value.trim() !== ''; later(); });
  base.onChange = () => later();
  dir.onChange = () => later(300);
  later(0);

  return {
    el,
    bindConfirm: (button) => { ok = button; ok.disabled = !valid; },
    dispose: () => { clearTimeout(timer); seq++; },
    spec: () => isNew() ? {
      branch: branch.value.trim(), existingBranch: existing.checked,
      base: existing.checked ? null : (base.input.value.trim() || null),
      path: pathTouched ? path.value.trim() : null,
      fetch: !existing.checked && fetch.checked, runSetup: !setupRow.hidden && setup.checked,
    } : null,
  };
}

/** Searchable list of local and remote branches for the base (free text such as a commit or tag is accepted too). */
function refPicker() {
  const input = h('input', { class: 'input mono ns-field', id: 'ns-wt-base', type: 'text', role: 'combobox', autocomplete: 'off', spellcheck: 'false',
    'aria-autocomplete': 'list', 'aria-expanded': 'false', 'aria-controls': 'ns-wt-base-list' });
  const list = h('div', { class: 'ns-list', id: 'ns-wt-base-list', role: 'listbox', 'aria-label': 'Branches', hidden: true });
  const el = h('div', { class: 'ns-combo' }, input, list);
  const self = { el, input, onChange: null, setItems: null };
  let items = [];
  let shown = [];
  let active = -1;
  let filtering = false;
  const setActive = (i) => {
    active = i;
    list.querySelectorAll('.ns-opt').forEach((o, j) => o.classList.toggle('active', j === i));
    const o = list.querySelector(`#ns-wt-base-opt-${i}`);
    if (o) {
      input.setAttribute('aria-activedescendant', o.id);
      if (o.offsetTop < list.scrollTop) list.scrollTop = o.offsetTop - 4;
      else if (o.offsetTop + o.offsetHeight > list.scrollTop + list.clientHeight) list.scrollTop = o.offsetTop + o.offsetHeight - list.clientHeight + 4;
    } else input.removeAttribute('aria-activedescendant');
  };
  const render = () => {
    const q = filtering ? input.value.trim().toLowerCase() : '';
    shown = (q ? items.filter((it) => it.name.toLowerCase().includes(q)) : items).slice(0, 300);
    mount(list, shown.map((it, i) => {
      const o = h('div', { class: 'ns-opt', id: `ns-wt-base-opt-${i}`, role: 'option', title: it.name },
        h('span', { class: 'ns-opt-label' }, h('span', { class: 'ns-opt-branch mono' }, it.name), h('span', { class: 'ns-opt-tag' }, it.remote ? 'remote' : 'local')));
      o.addEventListener('mousedown', (e) => e.preventDefault());
      o.addEventListener('click', () => pick(i));
      return o;
    }), shown.length ? null : h('div', { class: 'ns-empty small muted' }, 'No branch matches; the text is used as typed (a tag or commit works too).'));
    setActive(shown.length ? 0 : -1);
  };
  const open = () => { list.hidden = false; input.setAttribute('aria-expanded', 'true'); render(); };
  const close = () => { list.hidden = true; input.setAttribute('aria-expanded', 'false'); input.removeAttribute('aria-activedescendant'); filtering = false; };
  const pick = (i) => { const it = shown[i]; if (!it) return; input.value = it.name; close(); self.onChange?.(); };
  input.addEventListener('input', () => { filtering = true; open(); self.onChange?.(); });
  input.addEventListener('focus', () => input.select());
  input.addEventListener('click', () => { if (list.hidden) open(); });
  input.addEventListener('blur', close);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (list.hidden) { open(); return; }
      if (!shown.length) return;
      setActive(e.key === 'ArrowDown' ? (active + 1) % shown.length : (active - 1 + shown.length) % shown.length);
    } else if (e.key === 'Enter' && !list.hidden) {
      e.preventDefault();
      if (active >= 0) pick(active); else close();
    } else if (e.key === 'Escape' && !list.hidden) {
      e.preventDefault(); e.stopPropagation(); close();
    } else if (e.key === 'Tab') close();
  });
  self.setItems = (local, remote) => {
    items = [...remote.map((name) => ({ name, remote: true })), ...local.map((name) => ({ name, remote: false }))];
  };
  return self;
}

/**
 * Working-directory field: a text input (any absolute path) with a suggestion list of the discovered
 * repositories and worktrees. The list is part of the dialog (not a native <select>/<datalist> popup),
 * so it stays inside the dialog, scrolls when long and shortens long paths in the middle.
 */
function dirPicker(wts) {
  const items = wts.map((w) => ({
    path: w.root,
    repo: repoName(w.repositoryId),
    branch: w.branch || 'detached',
    worktree: !w.isMain,
    hay: `${repoName(w.repositoryId)} ${w.branch || ''} ${w.root}`.toLowerCase(),
  }));
  const input = h('input', {
    class: 'input mono ns-field', id: 'ns-dir', type: 'text', role: 'combobox', autocomplete: 'off', spellcheck: 'false',
    'aria-autocomplete': 'list', 'aria-expanded': 'false', 'aria-controls': 'ns-dir-list',
    placeholder: items.length ? 'Pick a repository or type an absolute path' : 'Absolute path inside a repository / workspace root',
    value: items[0]?.path || '',
  });
  const toggle = h('button', { type: 'button', class: 'ns-dir-toggle', tabindex: '-1', 'aria-label': 'Show discovered repositories', title: 'Discovered repositories and worktrees' }, '▾');
  const list = h('div', { class: 'ns-list', id: 'ns-dir-list', role: 'listbox', 'aria-label': 'Discovered repositories and worktrees', hidden: true });
  const el = h('div', { class: 'ns-combo' }, input, items.length ? toggle : null, list);
  let shown = [];
  let active = -1;
  let filtering = false;   // true once the user typed; opening from the arrow or after a pick shows everything

  const isOpen = () => !list.hidden;
  const syncTitle = () => { input.title = input.value; };
  const setActive = (i) => {
    active = i;
    list.querySelectorAll('.ns-opt').forEach((o, j) => o.classList.toggle('active', j === i));
    const o = list.querySelector(`#ns-dir-opt-${i}`);
    if (o) {
      input.setAttribute('aria-activedescendant', o.id);
      // Scroll only the list (scrollIntoView would also scroll the dialog body).
      if (o.offsetTop < list.scrollTop) list.scrollTop = o.offsetTop - 4;
      else if (o.offsetTop + o.offsetHeight > list.scrollTop + list.clientHeight) list.scrollTop = o.offsetTop + o.offsetHeight - list.clientHeight + 4;
    } else input.removeAttribute('aria-activedescendant');
  };
  const render = () => {
    const q = filtering ? input.value.trim().toLowerCase() : '';
    shown = q ? items.filter((it) => it.hay.includes(q)) : items;
    const cur = input.value.trim();
    mount(list,
      shown.map((it, i) => {
        const cut = it.path.lastIndexOf('/', it.path.length - 2) + 1;
        const o = h('div', { class: 'ns-opt' + (it.path === cur ? ' cur' : ''), id: `ns-dir-opt-${i}`, role: 'option', 'aria-selected': it.path === cur ? 'true' : 'false', title: it.path },
          h('span', { class: 'ns-opt-label' }, h('span', { class: 'ns-opt-repo' }, it.repo), h('span', { class: 'ns-opt-branch' }, it.branch),
            it.worktree ? h('span', { class: 'ns-opt-tag' }, 'worktree') : null),
          h('span', { class: 'ns-opt-path' }, h('span', { class: 'ns-path-head' }, it.path.slice(0, cut)), h('span', { class: 'ns-path-tail' }, it.path.slice(cut))));
        o.addEventListener('mousedown', (e) => e.preventDefault());   // keep focus in the input
        o.addEventListener('mousemove', () => { if (active !== i) setActive(i); });
        o.addEventListener('click', () => pick(i));
        return o;
      }),
      shown.length ? null : h('div', { class: 'ns-empty small muted' }, 'No discovered repository matches; the path is used as typed.'));
    setActive(shown.length ? Math.max(0, shown.findIndex((it) => it.path === cur)) : -1);
  };
  // Keep the list inside the dialog body: open downwards, or upwards when there is more room there.
  const place = () => {
    const body = el.closest('.modal-body');
    if (!body) return;
    const b = body.getBoundingClientRect();
    const r = input.getBoundingClientRect();
    const below = b.bottom - r.bottom - 10;
    const above = r.top - b.top - 10;
    const up = below < Math.min(list.scrollHeight, 200) && above > below;
    list.classList.toggle('up', up);
    list.style.maxHeight = Math.max(120, Math.min(300, up ? above : below)) + 'px';
  };
  const open = () => {
    if (!items.length) return;
    list.hidden = false;
    input.setAttribute('aria-expanded', 'true');
    render();
    place();
  };
  const close = () => {
    list.hidden = true;
    input.setAttribute('aria-expanded', 'false');
    input.removeAttribute('aria-activedescendant');
    filtering = false;
  };
  const pick = (i) => {
    const it = shown[i];
    if (!it) return;
    input.value = it.path;
    close();
    syncTitle();
    self.onChange?.();
    input.focus();
    // Show the end of a long path (the repository / worktree folder), not its common prefix.
    input.setSelectionRange(input.value.length, input.value.length);
    input.scrollLeft = input.scrollWidth;
  };

  input.addEventListener('input', () => { filtering = true; syncTitle(); open(); self.onChange?.(); });
  // Focusing selects the prefilled path, so typing a custom path replaces it instead of appending to it.
  let justFocused = false;
  input.addEventListener('mousedown', () => { justFocused = document.activeElement !== input; });
  input.addEventListener('focus', () => input.select());
  input.addEventListener('mouseup', (e) => { if (justFocused) e.preventDefault(); justFocused = false; });
  input.addEventListener('click', () => { if (!isOpen()) open(); });
  input.addEventListener('blur', close);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (!isOpen()) { open(); return; }
      if (!shown.length) return;
      const n = shown.length;
      setActive(e.key === 'ArrowDown' ? (active + 1) % n : (active - 1 + n) % n);
    } else if ((e.key === 'Home' || e.key === 'End') && isOpen() && shown.length && e.ctrlKey) {
      e.preventDefault();
      setActive(e.key === 'Home' ? 0 : shown.length - 1);
    } else if (e.key === 'Enter' && isOpen()) {
      e.preventDefault();
      if (active >= 0) pick(active); else close();
    } else if (e.key === 'Escape' && isOpen()) {
      e.preventDefault();
      e.stopPropagation();
      close();
    } else if (e.key === 'Tab') close();
  });
  toggle.addEventListener('mousedown', (e) => e.preventDefault());
  toggle.addEventListener('click', () => { if (isOpen()) close(); else { input.focus(); filtering = false; open(); } });
  const onResize = () => {
    if (!el.isConnected) { window.removeEventListener('resize', onResize); return; }
    if (isOpen()) place();
  };
  window.addEventListener('resize', onResize);
  syncTitle();
  const self = { el, input, onChange: null };
  return self;
}
