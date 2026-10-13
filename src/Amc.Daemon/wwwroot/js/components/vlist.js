// Fixed-row-height virtual list. Only rows in (and near) the viewport exist in the DOM;
// rows are cached by key and rebuilt only when their signature changes.

export class VList {
  /**
   * @param {HTMLElement} scroller  element with overflow:auto
   * @param {{rowHeight:number, keyOf:(it)=>string|number, sigOf?:(it)=>string, render:(it)=>HTMLElement, overscan?:number, onRange?:(first,last)=>void}} opts
   */
  constructor(scroller, opts) {
    this.el = scroller;
    this.o = { overscan: 12, sigOf: () => '', ...opts };
    this.items = [];
    this.rows = new Map();
    this.spacer = document.createElement('div');
    this.spacer.className = 'vl-spacer';
    this.el.classList.add('vlist');
    this.el.appendChild(this.spacer);
    this.pending = false;
    this.el.addEventListener('scroll', () => this.schedule(), { passive: true });
    this.ro = new ResizeObserver(() => this.schedule());
    this.ro.observe(this.el);
  }

  setItems(items) {
    this.items = items;
    this.spacer.style.height = items.length * this.o.rowHeight + 'px';
    this.update();
  }

  schedule() {
    if (this.pending) return;
    this.pending = true;
    requestAnimationFrame(() => { this.pending = false; this.update(); });
  }

  /** Re-render visible rows even if their signature is unchanged (e.g. selection changed). */
  refresh() { for (const r of this.rows.values()) r._sig = null; this.update(); }

  scrollToIndex(i) {
    const rh = this.o.rowHeight;
    const top = i * rh;
    if (top < this.el.scrollTop) this.el.scrollTop = top;
    else if (top + rh > this.el.scrollTop + this.el.clientHeight) this.el.scrollTop = top + rh - this.el.clientHeight;
  }

  update() {
    const { rowHeight: rh, overscan, keyOf, sigOf, render } = this.o;
    const n = this.items.length;
    const h = this.el.clientHeight || 600;
    const first = Math.max(0, Math.floor(this.el.scrollTop / rh) - overscan);
    const last = Math.min(n, Math.ceil((this.el.scrollTop + h) / rh) + overscan);
    const keep = new Map();
    for (let i = first; i < last; i++) {
      const it = this.items[i];
      const k = keyOf(it);
      const sig = sigOf(it);
      let row = this.rows.get(k);
      if (!row || row._sig !== sig) {
        const fresh = render(it, i);
        fresh._sig = sig;
        fresh.classList.add('vl-row');
        fresh.style.height = rh + 'px';
        if (row) row.replaceWith(fresh); else this.el.appendChild(fresh);
        row = fresh;
      }
      if (row._i !== i) { row.style.transform = `translateY(${i * rh}px)`; row._i = i; }
      keep.set(k, row);
    }
    for (const [k, row] of this.rows) if (!keep.has(k)) row.remove();
    this.rows = keep;
    this.o.onRange?.(first, last, n);
  }
}
