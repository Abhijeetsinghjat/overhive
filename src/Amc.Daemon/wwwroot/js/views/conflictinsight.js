// Conflict overlap (hunk-level, observed) and the "Warn agents" action. Used by the conflict banner and detail panel.
import { h, norm, shortId, agoEl } from '../util.js';
import { get } from '../store.js';
import { api, qs } from '../api.js';
import { agentName, worktreeLabel, evBadge } from '../model.js';
import { toast, confirmModal } from '../components/overlay.js';

const overlapOf = (c) => (norm(c.kind) === 'crossworktree' ? c.insight?.overlap : null) || null;
export const canWarn = (c) => norm(c.kind) === 'crossworktree' && !!c.path;

/** Headline for a cross-worktree conflict once its hunks were compared; null = keep the generic one. */
export function insightHeadline(c) {
  const o = overlapOf(c);
  switch (norm(o?.verdict)) {
    case 'overlapping': return 'Overlapping edits in several worktrees — merging will likely conflict';
    case 'separate': return 'Same file in several worktrees, different regions — likely merges cleanly';
    default: return null;
  }
}

function spans(list) {
  if (!list || !list.length) return '';
  const s = list.slice(0, 3).map((x) => (x.start === x.end ? String(x.start) : `${x.start}–${x.end}`)).join(', ');
  return (list.length === 1 && list[0].start === list[0].end ? 'line ' : 'lines ') + s + (list.length > 3 ? ` +${list.length - 3}` : '');
}

/** "Codex, Claude (feat/x)" — the agents attributed to the file in that worktree, plus its branch. */
function sideLabel(c, wtId) {
  const f = get('file', wtId + '|' + c.path);
  const names = Object.keys(f?.agents || {}).map((id) => { const a = get('agent', id); return a ? agentName(a) : shortId(id); });
  const w = get('worktree', wtId);
  return `${names.length ? names.join(', ') : 'unattributed'} (${w ? worktreeLabel(w) : shortId(wtId)})`;
}

/** One sentence per worktree pair: "Codex (feat/x) lines 40–80 overlaps Gemini (fix/y) lines 52–70". */
export function pairSentences(c) {
  const o = overlapOf(c);
  if (!o) return [];
  return (o.pairs || []).map((p) => {
    const a = sideLabel(c, p.worktreeA);
    const b = sideLabel(c, p.worktreeB);
    if (p.note && !p.overlapping) return `${a} vs ${b}: not compared (${p.note})`;
    if (p.overlapping) return `${a} ${spans(p.overlapA)} overlaps ${b} ${spans(p.overlapB)}`.replace(/\s+/g, ' ') + (p.note ? ` (${p.note})` : '');
    return `${a} ${spans(p.changedA)} · ${b} ${spans(p.changedB)} — different regions`;
  });
}

/** Compact line for the banner row (null when not analysed yet). */
export function overlapLine(c) {
  const lines = pairSentences(c);
  if (!lines.length) return null;
  const o = overlapOf(c);
  return h('div', { class: 'ci-line' + (norm(o.verdict) === 'overlapping' ? ' ci-bad' : '') },
    h('span', { class: 'mono' }, c.path), ' — ', lines.join('; '), ' ',
    evBadge(o.evidence || 'observed', 'Line ranges from git diff against the merge base (committed + uncommitted changes)'));
}

/** Detail panel content: verdict, pairs, merge-tree confirmation and warnings sent. */
export function insightContent(c) {
  const o = overlapOf(c);
  const warnings = c.insight?.warnings || [];
  const out = [];
  if (!o) {
    out.push(h('div', { class: 'muted small' }, canWarn(c) ? 'Line ranges not compared yet (analysed a few seconds after the file is quiet).' : 'Hunk comparison applies to cross-worktree conflicts only.'));
  } else {
    out.push(h('div', { class: 'small' }, h('b', null, { overlapping: 'Overlapping changes', separate: 'Different regions', unknown: 'Not determined' }[norm(o.verdict)] || o.verdict),
      ` · severity ${o.severity || '?'} · `, evBadge(o.evidence || 'observed', 'Computed from git diff -U0 against the merge base'), ' · analysed ', agoEl(o.analyzedAt)));
    for (const [i, s] of pairSentences(c).entries()) {
      const p = o.pairs[i];
      out.push(h('div', { class: 'ci-pair' + (p.overlapping ? ' ci-bad' : '') }, s,
        p.committedConflict === true ? h('div', { class: 'small warn' }, 'git merge-tree: the committed heads conflict on this path (observed)') : null,
        p.committedConflict === false ? h('div', { class: 'small muted' }, 'git merge-tree: committed heads merge cleanly on this path; uncommitted changes are not part of that check') : null));
    }
    out.push(h('div', { class: 'small muted' }, `Changes separated by fewer than ${o.margin ?? 3} unchanged lines count as overlapping.`, o.note ? ' ' + o.note + '.' : ''));
  }
  if (warnings.length) {
    out.push(h('div', { class: 'small ci-warned' }, h('b', null, 'Warnings sent'),
      warnings.slice().reverse().map((w) => h('div', null, agoEl(w.at), w.auto ? ' (automatic) ' : ' ',
        (w.recipients || []).map((r) => { const a = get('agent', r.agentId); return `${a ? agentName(a) : shortId(r.agentId)}: ${r.outcome}`; }).join(' · ')))));
  }
  return out;
}

export function warnButton(c, cls = 'btn btn-sm') {
  if (!canWarn(c)) return null;
  const b = h('button', { class: cls, title: 'Send each involved agent a short heads-up through its message transport' }, 'Warn agents');
  b.addEventListener('click', (e) => { e.stopPropagation(); warnAgents(c.key); });
  return b;
}

/** Shows exactly who gets which text, asks for confirmation, sends, reports per agent. */
export async function warnAgents(key) {
  let detail;
  try { detail = await api.get('/api/conflicts/detail?' + qs({ key })); } catch (e) { toast('Could not load the conflict', 'err', e.message); return; }
  const w = detail?.warn;
  if (!w || w.error) { toast('Cannot warn about this conflict', 'err', w?.error || ''); return; }
  if (!w.recipients.length) { toast('No active agent is attributed to this file', 'warn'); return; }
  const ok = await confirmModal({
    title: `Warn ${w.recipients.length} agent${w.recipients.length === 1 ? '' : 's'} about ${w.path}?`,
    body: h('div', null,
      h('p', { class: 'small muted' }, 'Each agent that can receive messages gets this text through its usual transport. It is recorded in the audit log.'),
      w.recipients.map((r) => h('div', { class: 'ci-preview' },
        h('div', null, h('b', null, r.name), ` (${r.tool}${r.branch ? ', ' + r.branch : ''}) `,
          r.canReceive ? h('span', { class: 'muted small' }, 'via ' + r.transport) : h('span', { class: 'warn small' }, 'cannot receive messages')),
        h('div', { class: 'ci-text' + (r.canReceive ? '' : ' muted') }, r.text)))),
    confirmLabel: 'Send warnings',
  });
  if (!ok) return;
  try {
    const r = await api.post('/api/conflicts/warn', { key, confirm: true });
    toast(r.detail, 'ok', r.agents.map((a) => `${a.name}: ${a.outcome}`).join(' · '));
  } catch (e) {
    const agents = e.body?.agents || [];
    toast('Warning not sent: ' + e.message, 'err', agents.map((a) => `${a.name}: ${a.outcome} — ${a.detail}`).join(' · '));
  }
}
