// Commits: every commit observed on any worktree, newest first, with repo/branch, author agent and a
// link to the commit on the git host. Respects the repository / agent / worktree / branch filters.
import { h, mount, agoEl, oneLine, dateTime } from '../util.js';
import { register, get, all } from '../store.js';
import { ui, select } from '../ui.js';
import { agentName, evBadge, shaLink, repoName, worktreeRef, agentColor, commitsForAgent } from '../model.js';
import { filterCtx } from '../filters.js';

export function initCommits() {
  const root = document.getElementById('view-commits');
  const info = h('span', { class: 'muted small' });
  const body = h('div', { class: 'ptable-wrap' });
  mount(root,
    h('div', { class: 'view-toolbar' }, h('span', { class: 'view-title' }, 'Commits'), info, h('span', { class: 'spacer' }),
      h('span', { class: 'muted small' }, 'Click a hash to open it on the git host; click a row for its worktree.')),
    body);

  register({
    name: 'commits', deps: ['commit', 'worktree', 'repository', 'agent', 'tab', 'filters'],
    render() {
      if (ui.tab !== 'commits') return;
      const ctx = filterCtx();
      const f = ui.filters;
      // With an agent filter: its own + sub-agents' commits + unattributed commits in worktrees they used.
      const scoped = f.agent && get('agent', f.agent) ? new Set(commitsForAgent(get('agent', f.agent)).map((x) => x.c.sha)) : null;
      const rows = all('commit')
        .filter((c) => !scoped || scoped.has(c.sha))
        .map((c) => ({ c, wt: get('worktree', c.worktreeId) }))
        .filter(({ c, wt }) =>
          (!f.repo || wt?.repositoryId === f.repo) &&
          (!f.worktree || c.worktreeId === f.worktree) &&
          (!f.branch || c.branch === f.branch) &&
          (!ctx.from || Date.parse(c.at) >= ctx.from) &&
          (!ctx.q || `${c.subject} ${c.sha} ${c.author || ''}`.toLowerCase().includes(ctx.q)))
        .sort((x, y) => (y.c.at || '').localeCompare(x.c.at || ''));
      info.textContent = `${rows.length} commit${rows.length === 1 ? '' : 's'}`;

      if (!rows.length) {
        mount(body, h('div', { class: 'empty' }, h('div', { class: 'empty-title' }, 'No commits'),
          all('commit').length ? 'No commits match the current filters.' : 'Commits appear here as soon as Git records them on any watched worktree.'));
        return;
      }
      mount(body, h('table', { class: 'ptable commits-table' },
        h('thead', null, h('tr', null, h('th', null, 'When'), h('th', null, 'Commit'), h('th', null, 'Message'),
          h('th', null, 'Repository'), h('th', null, 'Branch'), h('th', null, 'Author'), h('th', null, 'Agent'), h('th', { class: 'num' }, 'Files'))),
        h('tbody', null, rows.slice(0, 500).map(({ c, wt }) => {
          const a = c.agentId ? get('agent', c.agentId) : null;
          const tr = h('tr', { title: dateTime(c.at) },
            h('td', { class: 'nowrap muted' }, agoEl(c.at, '', ' ago')),
            h('td', { class: 'nowrap' }, shaLink(c.worktreeId, c.sha)),
            h('td', null, oneLine(c.subject || '', 110)),
            h('td', { class: 'nowrap' }, wt ? repoName(wt.repositoryId) : ''),
            h('td', { class: 'nowrap mono small' }, c.branch || (wt ? worktreeRef(wt) : '')),
            h('td', { class: 'nowrap muted' }, c.author || ''),
            h('td', { class: 'nowrap' }, a
              ? h('span', null, h('span', { class: 'agent-swatch', style: { background: agentColor(a.id) } }), ' ', oneLine(agentName(a), 34), ' ',
                evBadge('inferred', 'Commit author agent is inferred: the only agent running a shell command in that worktree when the commit appeared'))
              : h('span', { class: 'muted', title: 'Not attributed: several agents (or none) were active in this worktree when it appeared' }, 'unattributed')),
            h('td', { class: 'num' }, c.filesChanged ? String(c.filesChanged) : ''));
          tr.addEventListener('click', () => (a ? select('agent', a.id) : wt && select('worktree', wt.id)));
          return tr;
        }))));
    },
  });
}
