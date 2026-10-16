// "Claimed vs observed": an agent's own claims (tests pass, committed, created X, build succeeds)
// checked against what Overhive saw. Section for the agent panel + a small ✗ N mark for cards/rows.
import { h, clock, dateTime, oneLine } from '../util.js';
import { invalidate } from '../store.js';
import { api } from '../api.js';
import { evBadge } from '../model.js';

const cache = new Map(); // agentId → { data, error, loading, sig }

const STATUS = {
  confirmed: { glyph: '✓', label: 'confirmed', tip: 'What Overhive saw agrees with the claim' },
  contradicted: { glyph: '✗', label: 'contradicted', tip: 'What Overhive saw disagrees with the claim' },
  unverified: { glyph: '?', label: 'unverified', tip: 'Overhive saw nothing that settles the claim either way' },
};
const KIND = {
  tests_pass: 'Tests pass', build_succeeds: 'Build succeeds', committed: 'Committed',
  file_created: 'Created file', file_updated: 'Changed file', file_deleted: 'Deleted file',
};
const ORDER = { contradicted: 0, unverified: 1, confirmed: 2 };

/** Re-fetch when the agent's counts or activity change. */
function sigOf(a) { return JSON.stringify(a.verification) + '|' + a.toolCount + '|' + a.filesChanged + '|' + (a.lastOutput || '').length; }

function ensure(a) {
  const sig = sigOf(a);
  const c = cache.get(a.id);
  if (c && (c.sig === sig || c.loading)) return c;
  const entry = { loading: true, sig, data: c?.data, error: null };
  cache.set(a.id, entry);
  api.get(`/api/agents/${encodeURIComponent(a.id)}/verification`)
    .then((r) => { entry.data = r; })
    .catch((e) => { entry.error = e; })
    .finally(() => { entry.loading = false; invalidate('detail'); });
  return entry;
}

/** For the detail panel's render signature. */
export function verificationSig(agentId) {
  const c = cache.get(agentId);
  return c ? [c.loading, c.error?.message, JSON.stringify(c.data)].join('§') : '';
}

/** "✗ N" when claims of this agent were contradicted; null otherwise. */
export function verifyMark(a) {
  const n = a?.verification?.contradicted || 0;
  if (!n) return null;
  return h('span', { class: 'vf-mark', title: `${n} claim${n === 1 ? '' : 's'} by this agent contradicted by what Overhive observed. See Verification in the agent panel.` }, `✗ ${n}`);
}

/** Text with `code` spans rendered as code. */
function withCode(text) {
  return String(text).split('`').map((part, i) => (i % 2 ? h('code', null, part) : part));
}

function checkItem(c) {
  const st = STATUS[c.status] || STATUS.unverified;
  return h('div', { class: `vf-item vf-${c.status}` },
    h('div', { class: 'vf-row' },
      h('span', { class: `vf-badge vf-b-${c.status}`, title: st.tip }, st.glyph, ' ', st.label),
      h('span', { class: 'vf-kind' }, KIND[c.kind] || c.kind),
      c.path ? h('span', { class: 'vf-path mono', title: c.path }, '‎' + c.path) : null,
      h('span', { class: 'spacer' }),
      h('span', { class: 'muted small', title: dateTime(c.claimedAt) }, clock(c.claimedAt))),
    h('div', { class: 'vf-claim' }, '“', oneLine(c.claim, 240), '” ', evBadge('reported', 'The agent\'s own words')),
    h('div', { class: 'vf-obs small' }, '→ ', withCode(c.observation), ' ',
      c.evidence ? evBadge(c.evidence, 'Evidence for the observation') : h('span', { class: 'muted' }, '(nothing observed)'),
      c.observedAt ? h('span', { class: 'muted', title: dateTime(c.observedAt) }, ' · ', clock(c.observedAt)) : null));
}

function flagItem(f) {
  return h('div', { class: 'vf-item vf-flag' },
    h('div', { class: 'vf-row' },
      h('span', { class: 'vf-badge vf-b-flag', title: 'A review hint, not an accusation' }, '⚑ review hint'),
      evBadge(f.evidence, 'Inferred from the order of test runs and file changes in this session'),
      h('span', { class: 'spacer' }),
      h('span', { class: 'muted small', title: dateTime(f.at) }, clock(f.at))),
    h('div', { class: 'small' }, f.message),
    (f.files || []).length ? h('ul', { class: 'vf-files mono small' }, f.files.map((p) => h('li', null, p))) : null);
}

/** The "Verification" section, or null when the agent made no checkable claim and has no hint. */
export function verificationSection(a, section) {
  const sum = a.verification;
  if (!sum) return null;
  const c = ensure(a);
  const total = (sum.confirmed || 0) + (sum.contradicted || 0) + (sum.unverified || 0);
  const data = c.data;
  let body;
  if (c.error && !data) body = h('div', { class: 'warn small' }, `Verification unavailable: ${c.error.message}`);
  else if (!data) body = h('div', { class: 'muted small' }, 'Loading…');
  else {
    const checks = [...(data.checks || [])].sort((x, y) => (ORDER[x.status] - ORDER[y.status]) || (Date.parse(y.claimedAt) - Date.parse(x.claimedAt)));
    body = [
      ...(data.flags || []).map(flagItem),
      ...checks.slice(0, 30).map(checkItem),
      checks.length > 30 ? h('div', { class: 'muted small' }, `${checks.length - 30} older claim(s) not shown`) : null,
    ];
  }
  return section('a-verify', 'Verification', sum.contradicted ? `✗ ${sum.contradicted}` : total,
    h('div', { class: 'vf-summary small' },
      h('span', { class: 'vf-b-confirmed' }, `✓ ${sum.confirmed || 0}`), ' ',
      h('span', { class: 'vf-b-contradicted' }, `✗ ${sum.contradicted || 0}`), ' ',
      h('span', { class: 'vf-b-unverified' }, `? ${sum.unverified || 0}`),
      h('span', { class: 'muted' }, ' · what the agent said, checked against commands, Git and files')),
    body);
}
