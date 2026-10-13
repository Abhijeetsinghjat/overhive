// Unified diff parser + renderer (unified or side-by-side). Text only via textContent.
import { h } from '../util.js';

/**
 * Parses `git diff` output or bare hunks. Returns
 * [{ oldPath, newPath, meta:[string], hunks:[{ header, lines:[{t:'add'|'del'|'ctx'|'note', o, n, text}] }], added, removed, binary }]
 */
export function parseUnifiedDiff(text, fallbackName = '') {
  const files = [];
  let file = null, hunk = null, o = 0, n = 0;
  const newFile = (oldPath = fallbackName, newPath = fallbackName) => {
    file = { oldPath, newPath, meta: [], hunks: [], added: 0, removed: 0, binary: false };
    files.push(file);
    hunk = null;
  };
  const lines = String(text ?? '').replace(/\r\n/g, '\n').split('\n');
  if (lines.length && lines[lines.length - 1] === '') lines.pop();
  for (const line of lines) {
    const inHunk = hunk && !hunk.done;
    if (!inHunk && line.startsWith('diff --git ')) {
      const m = /^diff --git a\/(.*) b\/(.*)$/.exec(line);
      newFile(m ? m[1] : '', m ? m[2] : '');
      file.meta.push(line);
      continue;
    }
    if (!inHunk && line.startsWith('--- ')) {
      if (!file || file.hunks.length) newFile();
      hunk = null;
      const p = line.slice(4).replace(/^a\//, '').replace(/\t.*$/, '');
      if (p !== '/dev/null') file.oldPath = p;
      file.meta.push(line);
      continue;
    }
    if (!inHunk && line.startsWith('+++ ') && file) {
      const p = line.slice(4).replace(/^b\//, '').replace(/\t.*$/, '');
      file.newPath = p !== '/dev/null' ? p : file.oldPath;
      if (!file.oldPath) file.oldPath = file.newPath;
      file.meta.push(line);
      continue;
    }
    const hm = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$/.exec(line);
    if (hm) {
      if (!file) newFile();
      o = +hm[1]; n = +hm[3];
      hunk = { header: line, lines: [], oldLeft: hm[2] == null ? 1 : +hm[2], newLeft: hm[4] == null ? 1 : +hm[4] };
      if (hunk.oldLeft <= 0 && hunk.newLeft <= 0) hunk.done = true;
      file.hunks.push(hunk);
      continue;
    }
    if (!inHunk) {
      if (!file) newFile();
      if (/^Binary files/.test(line)) file.binary = true;
      file.meta.push(line);
      continue;
    }
    const c = line[0];
    if (c === '+') { hunk.lines.push({ t: 'add', o: null, n: n++, text: line.slice(1) }); file.added++; hunk.newLeft--; }
    else if (c === '-') { hunk.lines.push({ t: 'del', o: o++, n: null, text: line.slice(1) }); file.removed++; hunk.oldLeft--; }
    else if (c === '\\') hunk.lines.push({ t: 'note', o: null, n: null, text: line });
    else { hunk.lines.push({ t: 'ctx', o: o++, n: n++, text: line.slice(1) }); hunk.oldLeft--; hunk.newLeft--; }
    if (hunk.oldLeft <= 0 && hunk.newLeft <= 0) hunk.done = true;
  }
  return files;
}

const MAX_LINES = 20000;

export function renderDiff(text, { sideBySide = false, fallbackName = '', compact = false } = {}) {
  const files = parseUnifiedDiff(text, fallbackName);
  const root = h('div', { class: 'diff' + (compact ? ' diff-compact' : '') });
  if (!files.length || files.every((f) => !f.hunks.length && !f.binary)) {
    root.appendChild(h('div', { class: 'empty small' }, String(text || '').trim() ? 'No hunks in this diff.' : 'No changes.'));
    if (String(text || '').trim()) root.appendChild(h('pre', { class: 'pre' }, text));
    return root;
  }
  let budget = MAX_LINES;
  for (const f of files) {
    const name = f.oldPath && f.newPath && f.oldPath !== f.newPath ? `${f.oldPath} → ${f.newPath}` : f.newPath || f.oldPath || '(patch)';
    const body = h('div', { class: 'diff-body' });
    const head = h('div', { class: 'diff-file' },
      h('span', { class: 'diff-name mono' }, name),
      h('span', { class: 'diff-stat' }, h('span', { class: 'add' }, `+${f.added}`), ' ', h('span', { class: 'del' }, `−${f.removed}`)));
    head.addEventListener('click', () => body.classList.toggle('collapsed'));
    root.append(head, body);
    if (f.binary) { body.appendChild(h('div', { class: 'diff-note' }, 'Binary file')); continue; }
    for (const hk of f.hunks) {
      body.appendChild(h('div', { class: 'diff-hunk mono' }, hk.header));
      if (budget <= 0) continue;
      const tbl = h('table', { class: 'diff-table mono' + (sideBySide ? ' sbs' : '') });
      const rows = sideBySide ? pairRows(hk.lines) : hk.lines;
      for (const l of rows) {
        if (budget-- <= 0) { tbl.appendChild(h('tr', null, h('td', { colspan: 4, class: 'diff-note' }, 'Diff truncated for display.'))); break; }
        if (sideBySide) {
          tbl.appendChild(h('tr', null,
            h('td', { class: 'ln' }, l.l?.o ?? ''), h('td', { class: 'code ' + (l.l ? 'l-' + l.l.t : 'l-empty') }, l.l ? l.l.text : ''),
            h('td', { class: 'ln' }, l.r?.n ?? ''), h('td', { class: 'code ' + (l.r ? 'l-' + l.r.t : 'l-empty') }, l.r ? l.r.text : '')));
        } else {
          const sign = l.t === 'add' ? '+' : l.t === 'del' ? '−' : ' ';
          tbl.appendChild(h('tr', { class: 'l-' + l.t },
            h('td', { class: 'ln' }, l.o ?? ''), h('td', { class: 'ln' }, l.n ?? ''),
            h('td', { class: 'sign' }, l.t === 'note' ? '' : sign), h('td', { class: 'code' }, l.text)));
        }
      }
      body.appendChild(tbl);
    }
  }
  return root;
}

function pairRows(lines) {
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const l = lines[i];
    if (l.t === 'ctx' || l.t === 'note') { out.push({ l, r: l }); i++; continue; }
    const dels = [], adds = [];
    while (i < lines.length && lines[i].t === 'del') dels.push(lines[i++]);
    while (i < lines.length && lines[i].t === 'add') adds.push(lines[i++]);
    for (let k = 0; k < Math.max(dels.length, adds.length); k++) out.push({ l: dels[k] || null, r: adds[k] || null });
  }
  return out;
}

/** Plain file content with line numbers. */
export function renderContent(text) {
  const tbl = h('table', { class: 'diff-table mono content' });
  const lines = String(text ?? '').split('\n');
  const max = Math.min(lines.length, MAX_LINES);
  for (let i = 0; i < max; i++) tbl.appendChild(h('tr', null, h('td', { class: 'ln' }, i + 1), h('td', { class: 'code' }, lines[i])));
  if (lines.length > max) tbl.appendChild(h('tr', null, h('td', { colspan: 2, class: 'diff-note' }, `Truncated: ${lines.length - max} more lines`)));
  return h('div', { class: 'diff' }, tbl);
}
