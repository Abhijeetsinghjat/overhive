// A Claude Code tool call held for the user's decision (config "Approvals"; docs/AGENT_INTEGRATION.md).
// Used by the "Needs you" inbox, the agent detail panel and the phone page (/m). Allow / Deny answer the
// agent's PreToolUse hook; "Ask in terminal" hands it to Claude Code's own prompt at once. Without a decision before
// the countdown ends, Claude Code's normal permission flow applies (the call is never allowed silently).
import { h } from '../util.js';
import { api } from '../api.js';
import { toast } from './overlay.js';

const drafts = new Map();   // approval id → deny reason being typed (survives re-renders)
const busy = new Set();     // approval ids with a request in flight

function client() { return document.body.classList.contains('attn-m') ? 'phone' : 'dashboard'; }

/** POST /api/approvals/{id}/allow|deny. Resolves to the daemon's answer; toasts the outcome. */
export async function decide(id, decision, { reason = '', allowSimilar = false } = {}) {
  if (busy.has(id)) return null;
  busy.add(id);
  try {
    const r = await api.decideApproval(id, decision, { reason: reason || undefined, allowSimilar, client: client() });
    drafts.delete(id);
    toast(r?.message || ({ allow: 'Allowed', ask: 'Answer in the terminal' }[decision] || 'Denied'), 'ok');
    return r;
  } catch (e) {
    toast(({ allow: 'Allow', ask: 'Ask in terminal' }[decision] || 'Deny') + ' failed', 'err', e.message);
    return null;
  } finally {
    busy.delete(id);
  }
}

function previewLines(text) {
  return text.split('\n').map((l) => h('div', { class: l.startsWith('+ ') ? 'ap-add' : l.startsWith('- ') ? 'ap-del' : 'ap-ctx' }, l || ' '));
}

function countdown(expiresAt) {
  if (!expiresAt) return null;
  const el = h('span', { class: 'ap-timer small muted', title: 'Without a decision by then, Claude Code asks in its terminal as usual' });
  const tick = () => {
    if (!el.isConnected && el._started) { clearInterval(el._t); return; }
    el._started = true;
    const s = Math.max(0, Math.round((new Date(expiresAt).getTime() - Date.now()) / 1000));
    el.textContent = s > 0 ? `falls back to the terminal prompt in ${s}s` : 'timed out: falls back to the terminal prompt';
  };
  tick();
  el._t = setInterval(tick, 1000);
  return el;
}

/** The card body for one pending approval (item.approval in the attention queue, or a state entity). */
export function approvalCard(p, { compact = false } = {}) {
  const btn = (label, title, cls, fn) => {
    const b = h('button', { class: 'btn btn-sm ' + cls, type: 'button', title }, label);
    b.disabled = busy.has(p.id);
    b.addEventListener('click', (e) => { e.stopPropagation(); fn(b); });
    return b;
  };
  const reason = h('input', {
    class: 'input ap-reason', type: 'text', maxlength: 500, placeholder: 'Reason for the agent (optional)', 'aria-label': 'Deny reason',
    value: drafts.get(p.id) || '',
  });
  reason.addEventListener('click', (e) => e.stopPropagation());
  reason.addEventListener('input', () => drafts.set(p.id, reason.value));
  reason.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); decide(p.id, 'deny', { reason: reason.value.trim() }); } });

  const findings = p.findings || [];
  return h('div', { class: 'ap-card', 'data-approval': p.id },
    h('div', { class: 'ap-head small' },
      h('b', null, p.tool),
      p.severity ? h('span', { class: `ap-sev ap-sev-${p.severity}` }, p.severity) : h('span', { class: 'ap-sev' }, 'edit'),
      p.cwd ? h('span', { class: 'muted ap-cwd', title: 'Working directory' }, p.cwd) : null,
      h('span', { class: 'spacer' }),
      countdown(p.expiresAt)),
    findings.length ? h('ul', { class: 'ap-findings small plain' }, findings.map((f) =>
      h('li', null, h('code', null, f.ruleId), ' ', f.explanation, f.target ? h('span', { class: 'muted' }, ' · ' + f.target) : null))) : null,
    p.command ? h('pre', { class: 'ap-cmd' }, p.command) : null,
    p.filePath ? h('div', { class: 'ap-file small' }, h('span', { class: 'muted' }, 'File: '), h('code', null, p.filePath)) : null,
    p.preview && !compact ? h('pre', { class: 'ap-preview' }, previewLines(p.preview)) : null,
    h('div', { class: 'attn-actions ap-actions' },
      btn('Allow', 'Let the agent run this tool call now', 'btn-primary', () => decide(p.id, 'allow')),
      // Never offered for critical findings (the daemon sends no similarLabel then): those are decided one call at a time.
      p.similarLabel ? btn(p.command ? 'Allow this command for this session' : 'Allow edits to this file for this session',
        'Also allow ' + p.similarLabel + ' for the rest of this session, without asking (kept in memory until the session ends or Overhive restarts)', '',
        () => decide(p.id, 'allow', { allowSimilar: true })) : null,
      btn('Ask in terminal', 'Hand this call to Claude Code now: its own permission prompt appears in the terminal', 'btn-ghost', () => decide(p.id, 'ask')),
      reason,
      btn('Deny', 'Block this tool call; the agent sees the reason', 'btn-danger', () => decide(p.id, 'deny', { reason: reason.value.trim() }))));
}
