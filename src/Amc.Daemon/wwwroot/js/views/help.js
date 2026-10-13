import { h } from '../util.js';
import { infoModal } from '../components/overlay.js';
import { STATUS, STATUS_DESCRIPTIONS, EVIDENCE, evBadge } from '../model.js';

const KEYS = [
  ['/', 'Focus search'], ['1 – 6', 'Switch tab (Graph, Worktrees, Activity, Timeline, Processes, Health)'],
  ['j / k', 'Move in the agent tree'], ['Enter', 'Open the highlighted agent'], ['h / l', 'Collapse / expand tree node'],
  ['d', 'Toggle bottom drawer'], ['f', 'Toggle "follow active"'], ['Esc', 'Close dialog / clear search / close drawer'], ['?', 'This help'],
];

export function showHelp() {
  const statuses = Object.entries(STATUS).filter(([k]) => !['starting', 'created'].includes(k));
  infoModal('Overhive — reference', h('div', { class: 'help' },
    h('h4', null, 'Keyboard'),
    h('table', { class: 'kv' }, KEYS.map(([k, d]) => h('tr', null, h('td', null, h('kbd', null, k)), h('td', null, d)))),
    h('h4', null, 'Statuses'),
    h('table', { class: 'kv' }, statuses.map(([k, m]) => h('tr', null,
      h('td', null, h('span', { class: `pill ${m.cls}` }, h('span', { class: 'glyph' }, m.glyph), m.label)),
      h('td', null, STATUS_DESCRIPTIONS[k] || '')))),
    h('h4', null, 'Evidence'),
    h('p', { class: 'muted' }, 'Every fact carries how it is known. Overhive never shows an agent "working on" something without evidence.'),
    h('table', { class: 'kv' }, Object.entries(EVIDENCE).map(([k, m]) => h('tr', null, h('td', null, evBadge(k)), h('td', null, m.tip))))));
}
