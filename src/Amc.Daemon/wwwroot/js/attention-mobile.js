// Phone page (/m): only the attention queue, large touch targets, live over the WebSocket, inline replies.
// Reached through the user's own SSH tunnel or `tailscale serve` (docs/REMOTE_ACCESS.md); no cloud relay.
import { h, mount, tickAgo } from './util.js';
import { api, TOKEN_MISSING } from './api.js';
import { toast } from './components/overlay.js';
import { attentionItem } from './components/attention-item.js';
import { initEffects } from './effects.js';
import {
  attention, onAttention, refreshAttention, dismiss, snooze, notifyPrefs, setNotifyPref,
  enableNotifications, notificationPermission, onNotificationClick,
} from './attention-store.js';

const head = document.getElementById('m-head');
const list = document.getElementById('m-list');
const conn = { state: 'connecting' };
const composers = new Map(); // item id → composer element (typing survives re-renders)

try { const t = JSON.parse(localStorage.getItem('amc.theme') || '"dark"'); document.documentElement.dataset.theme = t === 'light' ? 'light' : 'dark'; } catch { /* default */ }
initEffects(); // amc.effects3d set in the dashboard also applies here

function dashboardLink(action) {
  if (action.kind === 'open-risks') return '/#' + new URLSearchParams({ tab: 'risks' }).toString();
  const sel = action.kind === 'open-conflict' ? 'conflict:' + action.target : 'agent:' + action.target;
  const drawer = action.kind === 'open-terminal' ? '&drawer=terminal' : '';
  return '/#' + new URLSearchParams({ tab: 'graph', sel }).toString() + drawer;
}

function onAction(action, item) {
  if (action.kind === 'reply') {
    if (composers.has(item.id)) composers.delete(item.id); else composers.set(item.id, composer(action.target, item.id));
    render();
    composers.get(item.id)?.querySelector('textarea')?.focus();
    return;
  }
  location.href = dashboardLink(action);
}

function composer(agentId, itemId) {
  const ta = h('textarea', { class: 'input', rows: 3, placeholder: 'Message to the agent…', 'aria-label': 'Message' });
  const send = h('button', { class: 'btn btn-primary', type: 'button' }, 'Send');
  send.addEventListener('click', async () => {
    const text = ta.value.trim();
    if (!text) return;
    send.disabled = true;
    try {
      const r = await api.sendMessage(agentId, text);
      toast(r?.message || 'Message sent', 'ok', r?.detail || '');
      composers.delete(itemId);
      render();
    } catch (e) {
      toast('Message failed', 'err', e.message);
    } finally { send.disabled = false; }
  });
  return h('div', { class: 'attn-m-composer' }, ta, h('div', { class: 'attn-actions' }, send));
}

const handlers = {
  onAction,
  onDismiss: (i) => dismiss(i.id).catch((e) => toast('Dismiss failed', 'err', e.message)),
  onSnooze: (i, min) => snooze(i.id, min).catch((e) => toast('Snooze failed', 'err', e.message)),
};

function notifyControl() {
  const perm = notificationPermission();
  if (perm === 'unsupported') return null;
  if (!notifyPrefs.enabled || perm !== 'granted') {
    const b = h('button', { class: 'btn', type: 'button', disabled: perm === 'denied' }, perm === 'denied' ? 'Notifications blocked' : 'Enable notifications');
    b.addEventListener('click', () => enableNotifications());
    return b;
  }
  const mute = h('label', { class: 'toggle' }, h('input', { type: 'checkbox', checked: notifyPrefs.muted }), ' Mute');
  mute.firstChild.addEventListener('change', (e) => setNotifyPref('muted', e.target.checked));
  return mute;
}

function render() {
  const label = { live: 'Live', connecting: 'Connecting…', reconnecting: 'Reconnecting…', offline: 'Offline' }[conn.state] || conn.state;
  mount(head,
    h('img', { class: 'logo-img', src: '/logo.svg', alt: '' }),
    h('div', { class: 'attn-m-title' }, h('b', null, 'Needs you'),
      h('span', { class: 'small muted' }, ` ${attention.urgent} urgent · ${attention.items.length} total`)),
    h('span', { class: 'spacer' }),
    h('span', { class: `conn conn-${conn.state === 'live' ? 'live' : 'reconnecting'}`, title: 'Connection to the daemon' }, h('span', { class: 'conn-dot' }), label),
    h('div', { class: 'attn-m-tools' }, notifyControl(), h('a', { class: 'btn', href: '/' }, 'Dashboard')));
  if (TOKEN_MISSING) { mount(list, h('div', { class: 'attn-empty muted' }, 'Open this page through the daemon (…/m).')); return; }
  mount(list,
    attention.error ? h('div', { class: 'small bad' }, 'Could not load: ' + attention.error) : null,
    attention.items.length
      ? attention.items.map((i) => [attentionItem(i, handlers), composers.get(i.id) || null])
      : h('div', { class: 'attn-empty muted' }, attention.loaded ? 'Nothing needs you right now.' : 'Loading…'),
    attention.hidden ? h('div', { class: 'small muted attn-m-foot' }, `${attention.hidden} snoozed or dismissed (manage them in the dashboard)`) : null);
}

// ---- live updates: a light poll of /api/attention (a few hundred bytes) instead of the full event stream ----

const POLL_MS = 4000;
async function poll() {
  if (TOKEN_MISSING) { conn.state = 'offline'; render(); return; }
  await refreshAttention();
  const next = attention.error ? 'reconnecting' : 'live';
  if (next !== conn.state) { conn.state = next; render(); }
}

onAttention(render);
onNotificationClick((item) => document.querySelector(`[data-id="${CSS.escape(item.id)}"]`)?.scrollIntoView({ block: 'center' }));
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') poll(); });
setInterval(() => { if (document.visibilityState === 'visible') poll(); }, POLL_MS);
setInterval(() => tickAgo(list), 1000);
render();
poll();
