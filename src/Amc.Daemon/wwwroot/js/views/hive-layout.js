// Hive layout (pure: no DOM, no three.js). Positions on the floor plane (x, z), in world units.
//
// Every session is a honeycomb cluster: the main session is the big cell in the middle and its
// sub-agents tile the hex grid around it (ring 2 outwards; ring 1 is under the big cell), each one
// as close to its own parent as a free slot allows, so nested sub-agents stay next to their parent.
// Slots are remembered between renders so cells don't jump when agents come and go. Clusters are
// grouped into zones (one per repository worktree) and the zones are shelf-packed into a rough
// rectangle centred on the origin.

const SQ3 = Math.sqrt(3);
export const GRID = 0.74;          // sub-agent grid: hex centre → corner
export const ROOT_R = 1.3;         // main session cell radius
export const SUB_R = [0.6, 0.5];   // sub-agent radius: depth 1, depth ≥ 2
const CLUSTER_PAD = 0.7, ZONE_PAD = 0.9, ZONE_HEAD = 0.9, ZONE_GAP = 1.6;  // ZONE_HEAD: room for the zone label (front edge)

const slotMemo = new Map();        // agent id → "q,r" of its last slot

const axialXZ = (q, r) => [GRID * SQ3 * (q + r / 2), GRID * 1.5 * r];

const DIRS = [[1, 0], [1, -1], [0, -1], [-1, 0], [-1, 1], [0, 1]];
function ring(k) {
  const out = [];
  let q = -k, r = k; // start at direction 4 × k, walk the six sides
  for (let side = 0; side < 6; side++) {
    for (let i = 0; i < k; i++) { out.push([q, r]); q += DIRS[side][0]; r += DIRS[side][1]; }
  }
  return out;
}

const spiralCache = [];
/** Slots of rings 2..K, nearest first. */
function slots(n) {
  let k = 2, count = 0;
  while (count < n * 1.25 + 6) { count += 6 * k; k++; }
  if (!spiralCache[k]) {
    const out = [];
    for (let i = 2; i < k; i++) for (const [q, r] of ring(i)) { const [x, z] = axialXZ(q, r); out.push({ key: q + ',' + r, x, z, d: Math.hypot(x, z) }); }
    spiralCache[k] = out;
  }
  return spiralCache[k];
}

/** Token count → cell height (log scale, so a 100M-token session is tall but not a skyscraper). */
export function cellHeight(tokens, isRoot) {
  const t = Math.log10(1 + (tokens || 0) / 1000);
  return isRoot ? Math.min(3.2, 0.55 + 0.32 * t) : Math.min(2, 0.28 + 0.2 * t);
}

/**
 * tree: [{ id, zone: { key, label, sub }, tokens, kids: [{ id, tokens, kids: [...] }] }] (roots, in display order).
 * Returns { cells, links, clusters, zones, bounds }; cells carry the node's extra fields through.
 */
export function layoutHive(tree) {
  const zonesByKey = new Map();
  const clusters = [];
  for (const root of tree) {
    const c = cluster(root);
    clusters.push(c);
    let z = zonesByKey.get(root.zone.key);
    if (!z) zonesByKey.set(root.zone.key, (z = { ...root.zone, clusters: [] }));
    z.clusters.push(c);
  }

  // Clusters inside a zone: rows of roughly square shape.
  const zones = [...zonesByKey.values()];
  for (const z of zones) {
    const area = z.clusters.reduce((s, c) => s + (2 * c.radius) ** 2, 0);
    const maxD = Math.max(...z.clusters.map((c) => 2 * c.radius));
    const rowW = Math.max(maxD, Math.sqrt(area) * 1.6);
    let x = 0, y = 0, rowH = 0, w = 0;
    for (const c of z.clusters) {
      const d = 2 * c.radius;
      if (x > 0 && x + d > rowW) { x = 0; y += rowH; rowH = 0; }
      c.lx = x + c.radius; c.lz = y + c.radius;
      x += d; rowH = Math.max(rowH, d); w = Math.max(w, x);
    }
    z.w = w + 2 * ZONE_PAD;
    z.h = y + rowH + 2 * ZONE_PAD + ZONE_HEAD;
  }

  // Zones: shelf packing towards a ~16:10 footprint.
  const total = zones.reduce((s, z) => s + z.w * z.h, 0);
  const maxW = Math.max(0, ...zones.map((z) => z.w));
  const shelfW = Math.max(maxW, Math.sqrt(total * 2));
  let x = 0, y = 0, rowH = 0, W = 0;
  for (const z of zones) {
    if (x > 0 && x + z.w > shelfW) { x = 0; y += rowH + ZONE_GAP; rowH = 0; }
    z.x0 = x; z.z0 = y; x += z.w + ZONE_GAP; rowH = Math.max(rowH, z.h); W = Math.max(W, x - ZONE_GAP);
  }
  const H = y + rowH;
  const ox = -W / 2, oz = -H / 2;

  const cells = [], links = [];
  for (const z of zones) {
    z.x0 += ox; z.z0 += oz; z.x1 = z.x0 + z.w; z.z1 = z.z0 + z.h;
    for (const c of z.clusters) {
      c.x = z.x0 + ZONE_PAD + c.lx;
      c.z = z.z0 + ZONE_PAD + c.lz;
      for (const cell of c.cells) { cell.x += c.x; cell.z += c.z; cells.push(cell); }
      links.push(...c.links);
    }
  }
  return { cells, links, clusters, zones, bounds: { x0: -W / 2, z0: -H / 2, x1: W / 2, z1: H / 2 } };
}

function cluster(root) {
  const cells = [{ ...root, kids: undefined, node: root, depth: 0, rootId: root.id, parentId: null, x: 0, z: 0, r: ROOT_R, h: cellHeight(root.tokens, true) }];
  const links = [];
  // Breadth first, so every parent has its slot before its children look for one.
  const queue = (root.kids || []).map((k) => [k, cells[0]]);
  const all = [];
  for (let i = 0; i < queue.length; i++) {
    const [n, parent] = queue[i];
    all.push([n, parent]);
    for (const k of n.kids || []) queue.push([k, n]);
  }
  const free = slots(all.length);
  const taken = new Set();
  const pos = new Map([[root.id, cells[0]]]);
  for (const [n, parentNode] of all) {
    const parent = pos.get(parentNode.id) || cells[0];
    let slot = null;
    const remembered = slotMemo.get(n.id);
    if (remembered && !taken.has(remembered)) slot = free.find((s) => s.key === remembered) || null;
    if (!slot) {
      let best = Infinity;
      for (const s of free) {
        if (taken.has(s.key)) continue;
        const cost = Math.hypot(s.x - parent.x, s.z - parent.z) + 0.45 * s.d;
        if (cost < best - 1e-9) { best = cost; slot = s; }
      }
    }
    taken.add(slot.key);
    slotMemo.set(n.id, slot.key);
    const depth = parent.depth + 1;
    const cell = { ...n, kids: undefined, node: n, depth, rootId: root.id, parentId: parent.id, x: slot.x, z: slot.z, r: SUB_R[Math.min(depth, 2) - 1], h: cellHeight(n.tokens, false) };
    cells.push(cell);
    pos.set(n.id, cell);
    links.push({ from: parent.id, to: n.id });
  }
  let radius = ROOT_R;
  for (const c of cells) radius = Math.max(radius, Math.hypot(c.x, c.z) + c.r);
  return { rootId: root.id, cells, links, radius: radius + CLUSTER_PAD, x: 0, z: 0 };
}
