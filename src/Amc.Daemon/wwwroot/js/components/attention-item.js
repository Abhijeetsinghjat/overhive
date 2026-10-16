// One attention item (title, tool, evidence, age, detail, actions). Used by the dashboard inbox, the Overview
// panel and the phone page; the caller decides what each action does (onAction(action, item)).
import { h, agoEl } from '../util.js';
import { evBadge, toolName } from '../model.js';

const PRIORITY = {
  urgent: { glyph: '!', label: 'Urgent', cls: 'attn-urgent' },
  normal: { glyph: '◷', label: 'Waiting', cls: 'attn-normal' },
  low: { glyph: '✓', label: 'FYI', cls: 'attn-low' },
};

export const SNOOZES = [[15, '15 min'], [60, '1 hour'], [240, '4 hours'], [24 * 60, '1 day']];

export function priorityMeta(p) { return PRIORITY[p] || PRIORITY.normal; }

/**
 * opts.onAction(action, item)          primary / secondary action (open-agent, open-terminal, reply, open-conflict)
 * opts.onDismiss(item), opts.onSnooze(item, minutes), opts.onRestore(item)   inbox marks
 * opts.compact                          fewer buttons (Overview panel)
 */
export function attentionItem(item, opts = {}) {
  const m = priorityMeta(item.priority);
  const btn = (label, title, fn, cls = '') => {
    const b = h('button', { class: 'btn btn-sm ' + cls, title, type: 'button' }, label);
    b.addEventListener('click', (e) => { e.stopPropagation(); fn(); });
    return b;
  };
  const actions = [];
  if (item.action) actions.push(btn(item.action.label, actionTitle(item.action), () => opts.onAction?.(item.action, item), 'btn-primary'));
  if (!opts.compact) for (const a of item.moreActions || []) actions.push(btn(a.label, actionTitle(a), () => opts.onAction?.(a, item)));
  const hidden = item.dismissed || item.snoozedUntil;
  if (hidden && opts.onRestore) actions.push(btn('Restore', 'Show this item in the queue again', () => opts.onRestore(item)));
  if (!hidden && opts.onSnooze) {
    const sel = h('select', { class: 'select attn-snooze', title: 'Hide this item for a while; it comes back (and notifies again) afterwards', 'aria-label': 'Snooze' },
      h('option', { value: '' }, 'Snooze…'), SNOOZES.map(([v, l]) => h('option', { value: String(v) }, l)));
    sel.addEventListener('click', (e) => e.stopPropagation());
    sel.addEventListener('change', () => { if (sel.value) opts.onSnooze(item, Number(sel.value)); });
    actions.push(sel);
  }
  if (!hidden && opts.onDismiss) actions.push(btn('Dismiss', 'Hide until this situation happens again', () => opts.onDismiss(item), 'btn-ghost'));

  return h('div', { class: `attn-item ${m.cls}`, 'data-id': item.id },
    h('div', { class: 'attn-line' },
      h('span', { class: 'attn-prio', title: m.label, 'aria-label': m.label }, m.glyph),
      h('span', { class: 'attn-title' }, item.title),
      h('span', { class: 'spacer' }),
      item.tool ? h('span', { class: 'attn-tool' }, toolName(item.tool)) : null,
      evBadge(item.evidence, 'Evidence behind this item'),
      h('span', { class: 'attn-age muted small' }, agoEl(item.since, '', ''))),
    item.detail ? h('div', { class: 'attn-detail small muted' }, item.detail) : null,
    hidden ? h('div', { class: 'small muted' }, item.dismissed ? 'Dismissed' : ['Snoozed until ', new Date(item.snoozedUntil).toLocaleTimeString()]) : null,
    actions.length ? h('div', { class: 'attn-actions' }, actions) : null);
}

function actionTitle(a) {
  switch (a.kind) {
    case 'open-terminal': return 'Open the live terminal of this agent (answer prompts there)';
    case 'reply': return 'Send a message through the agent\'s message transport';
    case 'open-conflict': return 'Show the conflict details';
    default: return 'Open this agent';
  }
}
