// Merged worktrees: the "Merged — clean up" badge/button and the confirm dialog (plan + live safety checks).
import { h, agoEl } from '../util.js';
import { api } from '../api.js';
import { evBadge } from '../model.js';
import { toast, confirmModal, infoModal } from '../components/overlay.js';

export const isMerged = (w) => !!(w && w.merged && !w.removed && !w.isMain);

/** One line about how the merge is known: observed via gh (PR) or inferred from git ("likely merged"). */
export function mergedSummary(w) {
  const m = w.merged;
  if (!m) return null;
  return h('span', null,
    m.pr ? [m.url ? h('a', { href: m.url, target: '_blank', rel: 'noopener noreferrer' }, `PR #${m.pr}`) : `PR #${m.pr}`, ' merged'] : `Likely merged: ${m.reason || 'inferred from git'}`,
    ' ', evBadge(m.evidence || (m.pr ? 'observed' : 'inferred'), m.pr ? 'Reported by GitHub through the gh CLI' : 'Inferred from git alone (no gh): weaker than a merged PR'),
    ' · detected ', agoEl(m.detectedAt),
    w.lastCleanup ? h('div', { class: 'small warn' }, `Last cleanup ${w.lastCleanup.outcome}: ${w.lastCleanup.detail || ''}`) : null);
}

export function cleanupButton(w, cls = 'btn btn-sm') {
  if (!isMerged(w)) return null;
  const likely = !w.merged.pr;
  const b = h('button', { class: cls + ' btn-merged', title: (likely ? 'Likely merged (inferred from git)' : `PR #${w.merged.pr} merged`) + ': review and remove this worktree' },
    likely ? 'Likely merged — clean up' : 'Merged — clean up');
  b.addEventListener('click', (e) => { e.stopPropagation(); cleanupWorktree(w.id); });
  return b;
}

const path = (id) => `/api/worktrees/${encodeURIComponent(id)}/cleanup`;

/** Loads the plan (checks run live on the daemon), shows exactly what would be removed, asks, removes, reports per step. */
export async function cleanupWorktree(id) {
  let plan;
  try { plan = await api.get(path(id)); } catch (e) { toast('Could not check the worktree', 'err', e.message); return; }
  const checks = plan.checks || [];
  const ok = checks.every((c) => c.ok || c.name === 'ignored-files');
  const ignored = plan.ignoredFiles || [];
  const ignoredTotal = plan.ignoredTotal || ignored.length;
  const body = h('div', null,
    h('table', { class: 'kv' },
      h('tr', null, h('td', null, 'Worktree folder'), h('td', { class: 'mono' }, plan.root)),
      plan.branch ? h('tr', null, h('td', null, 'Local branch'), h('td', { class: 'mono' }, plan.branch)) : null,
      h('tr', null, h('td', null, 'Remote branch'), h('td', { class: plan.remoteBranch ? 'mono' : 'muted' }, plan.remoteBranch || 'kept (WorktreeCleanup:DeleteRemoteBranch is off)'))),
    ignoredTotal ? h('div', { class: 'cleanup-ignored' },
      h('div', null, h('b', null, `⚠ Also deletes ${ignoredTotal} ignored path${ignoredTotal === 1 ? '' : 's'} that cannot be regenerated:`)),
      h('ul', { class: 'mono small' }, ignored.map((p) => h('li', null, p)), ignoredTotal > ignored.length ? h('li', null, `… and ${ignoredTotal - ignored.length} more`) : null),
      h('div', { class: 'small' }, 'Git does not track these (e.g. .env, keys, local config, data). Move anything you need before removing.')) : null,
    h('div', { class: 'small muted' }, 'Runs git worktree remove (never --force), then git branch -d. Regenerable ignored output (node_modules, bin, dist, caches …) is deleted with the folder.'),
    h('div', { class: 'cleanup-checks' }, checks.map((c) => h('div', { class: 'small' + (c.ok ? '' : ' warn') }, c.ok ? '✓ ' : '✗ ', h('b', null, c.name), ' — ', c.detail))));
  if (!ok) { await infoModal('This worktree cannot be removed now', body); return; }
  const go = await confirmModal({
    title: `Remove worktree ${plan.branch || plan.root}?`, body, confirmLabel: 'Remove worktree', danger: true, wide: true,
    typeToConfirm: ignoredTotal ? 'delete ignored files' : null,
  });
  if (!go) return;
  try {
    const r = await api.post(path(id), { confirm: true, confirmIgnored: ignoredTotal > 0 });
    toast(r.detail, 'ok', (r.steps || []).map((s) => `${s.command}: ${s.outcome}`).join(' · '));
  } catch (e) {
    const steps = e.body?.steps || [];
    toast('Cleanup did not complete: ' + e.message, 'err', steps.map((s) => `${s.command}: ${s.outcome}${s.detail ? ' — ' + s.detail : ''}`).join(' · '));
  }
}
