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
  const body = h('div', { class: 'ns-form' },
    h('label', null, h('span', null, 'Tool'), tool),
    h('div', { class: 'ns-row' }, h('label', { for: 'ns-dir' }, h('span', null, 'Working directory')), dir.el), modelRow, promptRow, hint);

  if (!list.length) { toast('Could not load the tool list', 'err'); return; }
  if (!await confirmModal({ title: 'New agent session', body, confirmLabel: 'Start session', wide: true })) return;
  const t = list.find((x) => x.id === tool.value);
  const cwd = dir.input.value.trim();
  try {
    const r = await api.launchSession({ tool: tool.value, cwd, model: (!modelRow.hidden && model.value.trim()) || null, prompt: (!promptRow.hidden && prompt.value.trim()) || null });
    toast(`${t?.name || 'Session'} starting`, 'ok', r.detail || '');
    openDrawer('terminal', { terminalId: r.terminalId, title: `${t?.name || 'Session'} — ${cwd}`, allowInput: true });
  } catch (e) {
    toast('Could not start the session', 'err', e.message);
  }
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
    input.focus();
    // Show the end of a long path (the repository / worktree folder), not its common prefix.
    input.setSelectionRange(input.value.length, input.value.length);
    input.scrollLeft = input.scrollWidth;
  };

  input.addEventListener('input', () => { filtering = true; syncTitle(); open(); });
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
  return { el, input };
}
