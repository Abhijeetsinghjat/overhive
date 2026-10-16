// Attention queue ("needs you") shared by the dashboard and the phone page (/m): the daemon derives the
// items (GET /api/attention, pushed as {t:"attention"} over the WebSocket); this module holds the latest
// snapshot, runs dismiss/snooze, and fires opt-in browser notifications for new urgent items.
import { api } from './api.js';

export const attention = { items: [], urgent: 0, hidden: 0, loaded: false, error: null, hiddenItems: null };

const listeners = [];
/** fn(snapshot) runs after every change (the dashboard maps it to invalidate('attention')). */
export function onAttention(fn) { listeners.push(fn); }

const known = new Set();
let primed = false;

/** Applies a snapshot from the API or the socket. */
export function setAttention(snap) {
  if (!snap || !Array.isArray(snap.items)) return;
  attention.items = snap.items;
  attention.urgent = snap.urgent || 0;
  attention.hidden = snap.hidden || 0;
  if (Array.isArray(snap.hiddenItems)) attention.hiddenItems = snap.hiddenItems;
  attention.loaded = true;
  attention.error = null;
  notifyNew(snap.items);
  for (const fn of listeners) fn(attention);
}

export async function refreshAttention({ all = false } = {}) {
  try {
    setAttention(await api.get('/api/attention' + (all ? '?all=true' : '')));
  } catch (e) {
    attention.error = e.message;
    for (const fn of listeners) fn(attention);
  }
}

export const dismiss = (id) => api.post('/api/attention/dismiss', { id }).then(setAttention);
export const snooze = (id, minutes) => api.post('/api/attention/snooze', { id, minutes }).then(setAttention);
export const restore = (id) => api.post('/api/attention/restore', { id }).then(() => refreshAttention({ all: true }));

export const isUrgent = (i) => i.priority === 'urgent';
export const agentOf = (i) => (i.agentIds || [])[0] || null;

// ---- browser notifications (opt-in; permission is asked only from a click) -----------------

const KEY = 'amc.attentionNotify';
function loadPrefs() {
  try { return { enabled: false, muted: false, includeWaiting: false, ...JSON.parse(localStorage.getItem(KEY) || '{}') }; } catch { return { enabled: false, muted: false, includeWaiting: false }; }
}
export const notifyPrefs = loadPrefs();
function savePrefs() { try { localStorage.setItem(KEY, JSON.stringify(notifyPrefs)); } catch { /* private mode */ } }

export const notificationsSupported = () => typeof Notification !== 'undefined' && window.isSecureContext;
export const notificationPermission = () => (notificationsSupported() ? Notification.permission : 'unsupported');
export const notificationsActive = () => notifyPrefs.enabled && !notifyPrefs.muted && notificationPermission() === 'granted';

/** Must be called from a user gesture (button click). Resolves to the resulting permission. */
export async function enableNotifications() {
  if (!notificationsSupported()) return 'unsupported';
  const p = Notification.permission === 'granted' ? 'granted' : await Notification.requestPermission();
  notifyPrefs.enabled = p === 'granted';
  notifyPrefs.muted = false;
  savePrefs();
  for (const fn of listeners) fn(attention);
  return p;
}

export function setNotifyPref(name, value) {
  notifyPrefs[name] = value;
  savePrefs();
  for (const fn of listeners) fn(attention);
}

let clickHandler = null;
/** What clicking a browser notification does (the dashboard selects the agent; the phone page scrolls to it). */
export function onNotificationClick(fn) { clickHandler = fn; }

function notifyNew(items) {
  const ids = new Set(items.map((i) => i.id));
  const fresh = items.filter((i) => !known.has(i.id) && (isUrgent(i) || (notifyPrefs.includeWaiting && i.priority === 'normal')));
  known.clear();
  for (const id of ids) known.add(id);
  // The first snapshot after loading the page is what is already there: no burst of old notifications.
  if (!primed) { primed = true; return; }
  if (!fresh.length || !notificationsActive()) return;
  // While the page is in front of the user, the badge is enough.
  if (document.visibilityState === 'visible' && document.hasFocus()) return;
  const title = fresh.length === 1 ? fresh[0].title : `${fresh.length} items need you`;
  const body = fresh.length === 1 ? (fresh[0].detail || '') : fresh.slice(0, 4).map((i) => '• ' + i.title).join('\n');
  try {
    const n = new Notification('Overhive: ' + title, { body: body.slice(0, 300), tag: 'overhive-' + fresh[0].id, icon: '/logo.svg' });
    n.onclick = () => { window.focus(); clickHandler?.(fresh[0]); n.close(); };
  } catch { /* some mobile browsers only allow notifications from a service worker */ }
}
