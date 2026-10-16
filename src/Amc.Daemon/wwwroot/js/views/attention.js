// Attention inbox in the dashboard: header badge (urgent count), drop-down panel (key "a"), Overview list,
// and the opt-in browser notification controls. Data and notifications live in attention-store.js.
import { h, mount } from '../util.js';
import { store, register, invalidate } from '../store.js';
import { select, openDrawer, setTab } from '../ui.js';
import { toast } from '../components/overlay.js';
import { attentionItem } from '../components/attention-item.js';
import {
  attention, onAttention, refreshAttention, dismiss, snooze, restore, notifyPrefs, setNotifyPref,
  enableNotifications, notificationPermission, onNotificationClick, isUrgent,
} from '../attention-store.js';

let panel = null;
let showHidden = false;

export function initAttention() {
  onAttention(() => invalidate('attention'));
  onNotificationClick((item) => runAction(item.action, item));

  // (Re)load whenever the connection becomes live; poll while the WebSocket is unavailable.
  let lastConn = null;
  register({
    name: 'attention-sync', deps: ['conn'],
    render() {
      if (store.conn.state === 'live' && lastConn !== 'live') refreshAttention();
      lastConn = store.conn.state;
    },
  });
  setInterval(() => { if (store.conn.state === 'polling') refreshAttention(); }, 5000);

  panel = h('div', { class: 'attn-panel', hidden: true, role: 'dialog', 'aria-label': 'Needs you' });
  document.body.appendChild(panel);
  document.addEventListener('mousedown', (e) => {
    if (!panel.hidden && !panel.contains(e.target) && !e.target.closest?.('.attn-badge')) closeAttentionPanel();
  });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !panel.hidden) closeAttentionPanel(); });
  register({ name: 'attention-panel', deps: ['attention', 'agent'], render: () => { if (!panel.hidden) renderPanel(); } });
}

/** Header badge: number of urgent items; the whole queue is in the panel. */
export function attentionBadge() {
  const b = h('button', { class: 'attn-badge zero', type: 'button', title: 'Needs you (a)' });
  b.addEventListener('click', toggleAttentionPanel);
  const render = () => {
    const n = attention.urgent;
    const total = attention.items.length;
    b.className = 'attn-badge' + (n ? '' : ' zero');
    b.title = `${n} urgent, ${total} in the queue${attention.hidden ? `, ${attention.hidden} snoozed/dismissed` : ''} — click or press "a"`;
    mount(b, h('span', { class: 'glyph', 'aria-hidden': 'true' }, '⚑'), h('b', null, String(n)), h('span', { class: 'lbl' }, ' need you'));
  };
  register({ name: 'attention-badge', deps: ['attention'], render });
  render();
  return b;
}

export function toggleAttentionPanel() {
  if (panel.hidden) { panel.hidden = false; renderPanel(); refreshAttention({ all: showHidden }); } else closeAttentionPanel();
}

function closeAttentionPanel() { panel.hidden = true; }

export function runAction(action, item) {
  if (!action) return;
  const target = action.target;
  switch (action.kind) {
    case 'open-terminal': select('agent', target); openDrawer('terminal', { agentId: target }); break;
    case 'reply': select('agent', target); openDrawer('messages', { agentId: target }); break;
    case 'open-conflict': select('conflict', target); break;
    case 'open-risks': setTab('risks'); break;
    default: if (target) select('agent', target);
  }
  if (!panel.hidden && item) closeAttentionPanel();
}

const reloadHidden = () => { if (showHidden) refreshAttention({ all: true }); };

const handlers = {
  onAction: runAction,
  onDismiss: (i) => dismiss(i.id).then(reloadHidden).catch((e) => toast('Dismiss failed', 'err', e.message)),
  onSnooze: (i, min) => snooze(i.id, min).then(reloadHidden).catch((e) => toast('Snooze failed', 'err', e.message)),
  onRestore: (i) => restore(i.id).catch((e) => toast('Restore failed', 'err', e.message)),
};

function notifyControls() {
  const perm = notificationPermission();
  if (perm === 'unsupported') return h('span', { class: 'small muted', title: 'Browser notifications need a secure context (http://127.0.0.1 or https)' }, 'Browser notifications unavailable');
  if (!notifyPrefs.enabled || perm !== 'granted') {
    const b = h('button', { class: 'btn btn-sm', type: 'button', title: 'Asks the browser for permission. Notifications are shown for new urgent items while this tab is in the background.' },
      perm === 'denied' ? 'Notifications blocked in browser settings' : 'Enable notifications');
    b.disabled = perm === 'denied';
    b.addEventListener('click', () => enableNotifications().then((p) => { if (p !== 'granted') toast('Notifications not allowed', 'err', 'The browser did not grant permission.'); }));
    return b;
  }
  const mute = h('label', { class: 'toggle small', title: 'Keep notifications enabled but silent for now' },
    h('input', { type: 'checkbox', checked: notifyPrefs.muted }), ' Mute');
  mute.firstChild.addEventListener('change', (e) => setNotifyPref('muted', e.target.checked));
  const waiting = h('label', { class: 'toggle small', title: 'Also notify when a session finishes its turn and waits for your next prompt' },
    h('input', { type: 'checkbox', checked: notifyPrefs.includeWaiting }), ' Also when waiting');
  waiting.firstChild.addEventListener('change', (e) => setNotifyPref('includeWaiting', e.target.checked));
  return h('span', { class: 'attn-notify' }, h('span', { class: 'small muted' }, '🔔 on'), mute, waiting);
}

function renderPanel() {
  const items = attention.items;
  const urgent = items.filter(isUrgent).length;
  const hiddenToggle = attention.hidden || showHidden
    ? h('button', { class: 'btn btn-sm btn-ghost', type: 'button' }, showHidden ? 'Hide snoozed/dismissed' : `${attention.hidden} snoozed/dismissed — show`)
    : null;
  hiddenToggle?.addEventListener('click', () => { showHidden = !showHidden; if (showHidden) refreshAttention({ all: true }); else renderPanel(); });
  mount(panel,
    h('div', { class: 'attn-head' },
      h('b', null, 'Needs you'), h('span', { class: 'sec-count' }, String(items.length)),
      urgent ? h('span', { class: 'small attn-urgent-txt' }, `${urgent} urgent`) : null,
      h('span', { class: 'spacer' }),
      h('a', { class: 'small', href: '/m', target: '_blank', rel: 'noopener', title: 'Compact view for a phone (see docs/REMOTE_ACCESS.md for safe access)' }, 'Phone view ↗')),
    h('div', { class: 'attn-subhead' }, notifyControls()),
    attention.error ? h('div', { class: 'small bad' }, 'Could not load: ' + attention.error) : null,
    h('div', { class: 'attn-list' },
      items.length ? items.map((i) => attentionItem(i, handlers))
        : h('div', { class: 'muted small attn-empty' }, attention.loaded ? 'Nothing needs you right now.' : 'Loading…'),
      showHidden && attention.hiddenItems?.length ? [h('div', { class: 'attn-sep small muted' }, 'Snoozed / dismissed'), attention.hiddenItems.map((i) => attentionItem(i, handlers))] : null),
    hiddenToggle ? h('div', { class: 'attn-foot' }, hiddenToggle) : null);
}

/** Overview panel section body (same data as the inbox). */
export function attentionOverview() {
  if (!attention.loaded) return h('div', { class: 'muted small' }, 'Loading…');
  if (!attention.items.length) return h('div', { class: 'muted small' }, 'Nothing needs you right now.');
  return h('div', { class: 'attn-list' }, attention.items.map((i) => attentionItem(i, { ...handlers, compact: true })));
}

/** Signature for the Overview re-render check. */
export function attentionSig() {
  return attention.items.map((i) => i.id).join(',') + '/' + attention.hidden;
}
