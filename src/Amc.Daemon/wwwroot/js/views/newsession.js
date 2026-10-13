// "New session": start any configured AI tool in a Overhive-owned terminal and open it in the drawer.
// Each tool signs in with its own account; Overhive never sees those credentials.
import { h } from '../util.js';
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
  const dir = h('select', { class: 'select ns-field', id: 'ns-dir' },
    wts.length ? wts.map((w) => h('option', { value: w.root }, `${repoName(w.repositoryId)} · ${w.branch || 'detached'}${w.isMain ? '' : ' (worktree)'} — ${w.root}`))
      : h('option', { value: '' }, 'No repositories discovered yet'));
  const custom = h('input', { class: 'input mono ns-field', id: 'ns-path', placeholder: 'or an absolute path inside a repository / workspace root', spellcheck: 'false' });
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
    h('label', null, h('span', null, 'Working directory'), dir), custom, modelRow, promptRow, hint);

  if (!list.length) { toast('Could not load the tool list', 'err'); return; }
  if (!await confirmModal({ title: 'New agent session', body, confirmLabel: 'Start session' })) return;
  const t = list.find((x) => x.id === tool.value);
  const cwd = custom.value.trim() || dir.value;
  try {
    const r = await api.launchSession({ tool: tool.value, cwd, model: (!modelRow.hidden && model.value.trim()) || null, prompt: (!promptRow.hidden && prompt.value.trim()) || null });
    toast(`${t?.name || 'Session'} starting`, 'ok', r.detail || '');
    openDrawer('terminal', { terminalId: r.terminalId, title: `${t?.name || 'Session'} — ${cwd}`, allowInput: true });
  } catch (e) {
    toast('Could not start the session', 'err', e.message);
  }
}
