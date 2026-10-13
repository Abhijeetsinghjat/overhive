// Toasts and modal dialogs.
import { h } from '../util.js';

let toastHost = null;

export function toast(message, kind = 'info', detail = '', timeout = kind === 'err' ? 9000 : 4500) {
  if (!toastHost) { toastHost = h('div', { class: 'toasts', role: 'status', 'aria-live': 'polite' }); document.body.appendChild(toastHost); }
  const close = h('button', { class: 'toast-x', title: 'Dismiss', 'aria-label': 'Dismiss' }, '×');
  const el = h('div', { class: `toast toast-${kind}` },
    h('div', { class: 'toast-body' }, h('div', { class: 'toast-msg' }, message), detail ? h('div', { class: 'toast-detail' }, detail) : null),
    close);
  const remove = () => el.remove();
  close.addEventListener('click', remove);
  toastHost.appendChild(el);
  if (timeout) setTimeout(remove, timeout);
}

let openModal = null;
export const modalOpen = () => !!openModal;

/**
 * Modal confirmation. If `typeToConfirm` is set, the confirm button only enables once the
 * user typed exactly that string. Resolves true/false.
 */
export function confirmModal({ title, body, confirmLabel = 'Confirm', danger = false, typeToConfirm = null }) {
  return new Promise((resolve) => {
    const input = typeToConfirm ? h('input', { class: 'input mono', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Type to confirm' }) : null;
    const ok = h('button', { class: `btn ${danger ? 'btn-danger' : 'btn-primary'}`, disabled: !!typeToConfirm }, confirmLabel);
    const cancel = h('button', { class: 'btn' }, 'Cancel');
    const dlg = h('div', { class: 'modal', role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
      h('div', { class: 'modal-title' }, title),
      h('div', { class: 'modal-body' }, body,
        typeToConfirm ? h('label', { class: 'modal-confirm' }, 'Type ', h('code', null, typeToConfirm), ' to confirm', input) : null),
      h('div', { class: 'modal-actions' }, cancel, ok));
    const back = h('div', { class: 'modal-back' }, dlg);
    const done = (v) => { back.remove(); openModal = null; document.removeEventListener('keydown', onKey, true); resolve(v); };
    const onKey = (e) => {
      if (e.key === 'Escape') { e.stopPropagation(); done(false); }
      if (e.key === 'Enter' && !ok.disabled && document.activeElement !== cancel) { e.preventDefault(); done(true); }
    };
    input?.addEventListener('input', () => { ok.disabled = input.value.trim() !== typeToConfirm; });
    ok.addEventListener('click', () => done(true));
    cancel.addEventListener('click', () => done(false));
    back.addEventListener('mousedown', (e) => { if (e.target === back) done(false); });
    document.addEventListener('keydown', onKey, true);
    document.body.appendChild(back);
    openModal = back;
    (input || ok).focus();
  });
}

export function infoModal(title, body) {
  return new Promise((resolve) => {
    const ok = h('button', { class: 'btn btn-primary' }, 'Close');
    const back = h('div', { class: 'modal-back' },
      h('div', { class: 'modal modal-wide', role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
        h('div', { class: 'modal-title' }, title), h('div', { class: 'modal-body' }, body), h('div', { class: 'modal-actions' }, ok)));
    const done = () => { back.remove(); openModal = null; document.removeEventListener('keydown', onKey, true); resolve(); };
    const onKey = (e) => { if (e.key === 'Escape' || e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); done(); } };
    ok.addEventListener('click', done);
    back.addEventListener('mousedown', (e) => { if (e.target === back) done(); });
    document.addEventListener('keydown', onKey, true);
    document.body.appendChild(back);
    openModal = back;
    ok.focus();
  });
}
