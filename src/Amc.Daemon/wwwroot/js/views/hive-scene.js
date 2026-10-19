// Hive scene: WebGL rendering of the hive (three.js, vendored, MIT). Imported lazily by views/hive.js
// the first time the Hive tab opens; createHive() builds renderer + scene into the stage and returns
// { update, setOptions, fit, focus, neighbour, dispose }.
//
// Cells are hexagonal prisms: colour = agent colour, height = token usage, glow = status (running pulses,
// waiting/blocked amber, failed red, finished dimmed). Edges are glowing arcs from parent to sub-agent;
// conflicts are red arcs over the hive; runaway-guard alerts ring the session's cluster; critical risks
// float a red marker over the cell. Text is HTML (CSS2D) so it stays crisp.
//
// Rendering is on demand: a frame is drawn only while something moves (camera, transitions, pulses), and
// the loop stops while the browser tab is hidden. Everything is disposed when the Hive tab is left.
import * as THREE from '../../vendor/three/three.module.min.js';
import { OrbitControls } from '../../vendor/three/addons/controls/OrbitControls.js';
import { CSS2DRenderer, CSS2DObject } from '../../vendor/three/addons/renderers/CSS2DRenderer.js';
import { EffectComposer } from '../../vendor/three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from '../../vendor/three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from '../../vendor/three/addons/postprocessing/UnrealBloomPass.js';
import { OutputPass } from '../../vendor/three/addons/postprocessing/OutputPass.js';
import { RoomEnvironment } from '../../vendor/three/addons/environments/RoomEnvironment.js';
import { layoutHive } from './hive-layout.js';

const MAX_DPR = 2;
const SUB_LABEL_DIST = 16;     // sub-agent labels show when the camera is closer than this
const EASE = 7;                // layout transition speed (1/s)
const ARC_PTS = 28;          // tube segments of a parent → sub-agent link

// ---- shared resources (created once per module, re-uploaded by each renderer) -------------------

const HEX = new THREE.CylinderGeometry(1, 1, 1, 6, 1).translate(0, 0.5, 0);
const HEX_EDGES = new THREE.EdgesGeometry(HEX);
const CAP = new THREE.CylinderGeometry(0.8, 0.8, 0.04, 6, 1);
const HEX_RING = new THREE.RingGeometry(1, 1.09, 6, 1, Math.PI / 2).rotateX(-Math.PI / 2);
const RISK = new THREE.OctahedronGeometry(0.22, 0);
let glowTex = null;

function glowTexture() {
  if (glowTex) return glowTex;
  const c = document.createElement('canvas');
  c.width = c.height = 128;
  const g = c.getContext('2d');
  const grd = g.createRadialGradient(64, 64, 0, 64, 64, 64);
  grd.addColorStop(0, 'rgba(255,255,255,1)');
  grd.addColorStop(0.25, 'rgba(255,255,255,.45)');
  grd.addColorStop(0.6, 'rgba(255,255,255,.08)');
  grd.addColorStop(1, 'rgba(255,255,255,0)');
  g.fillStyle = grd;
  g.fillRect(0, 0, 128, 128);
  glowTex = new THREE.CanvasTexture(c);
  glowTex.colorSpace = THREE.SRGBColorSpace;
  return glowTex;
}

// ---- theme --------------------------------------------------------------------------

function palette() {
  const cs = getComputedStyle(document.documentElement);
  const v = (n, d) => cs.getPropertyValue(n).trim() || d;
  const dark = document.documentElement.dataset.theme !== 'light';
  return {
    dark,
    bg: new THREE.Color(v('--bg', dark ? '#11141a' : '#f4f6f9')),
    floor: new THREE.Color(v('--bg2', dark ? '#151921' : '#eef1f5')),
    line: new THREE.Color(v('--border2', dark ? '#363f4e' : '#c9d0db')),
    accent: new THREE.Color(v('--accent', '#e8a33d')),
    amber: new THREE.Color(v('--accent', '#e8a33d')),
    orange: new THREE.Color(v('--orange', '#f08c4a')),
    red: new THREE.Color(v('--red', '#ef6b6b')),
    grey: new THREE.Color(v('--grey', '#98a1b0')),
    text: new THREE.Color(v('--text', '#e8ebf1')),
  };
}

/** Status → glow colour, intensity and pulse speed (0 = steady). */
function look(n, P) {
  const base = new THREE.Color(n.color);
  switch (n.status) {
    case 'running': case 'starting': return { glow: base, k: 1.25, pulse: 2.4, ripple: true, done: false };
    case 'blocked': return { glow: P.orange, k: 1.5, pulse: 5, ripple: true, done: false };
    case 'waiting': case 'paused': return { glow: P.amber, k: 0.6, pulse: 0, ripple: false, done: false };
    case 'failed': return { glow: P.red, k: 1.15, pulse: 0, ripple: false, done: false };
    case 'disconnected': return { glow: P.grey, k: 0.25, pulse: 0, ripple: false, done: true };
    case 'completed': case 'cancelled': return { glow: base, k: 0.12, pulse: 0, ripple: false, done: true };
    default: return { glow: base, k: 0.25, pulse: 0, ripple: false, done: true };
  }
}

// ---- floor (procedural honeycomb) -------------------------------------------------------------

function floorMaterial(P) {
  return new THREE.ShaderMaterial({
    transparent: true, depthWrite: false,
    uniforms: {
      uLine: { value: P.line.clone() }, uGlow: { value: P.accent.clone() }, uFloor: { value: P.floor.clone() },
      uRadius: { value: 40 }, uAlpha: { value: P.dark ? 0.55 : 0.75 },
    },
    vertexShader: /* glsl */`
      varying vec3 vW;
      void main() { vec4 w = modelMatrix * vec4(position, 1.0); vW = w.xyz; gl_Position = projectionMatrix * viewMatrix * w; }`,
    fragmentShader: /* glsl */`
      uniform vec3 uLine; uniform vec3 uGlow; uniform vec3 uFloor; uniform float uRadius; uniform float uAlpha;
      varying vec3 vW;
      float hexDist(vec2 p) { p = abs(p); return max(dot(p, normalize(vec2(1.0, 1.7320508))), p.x); }
      void main() {
        vec2 uv = vW.xz / 2.2;
        vec2 r = vec2(1.0, 1.7320508), hh = r * 0.5;
        vec2 a = mod(uv, r) - hh, b = mod(uv - hh, r) - hh;
        vec2 gv = dot(a, a) < dot(b, b) ? a : b;
        float e = 0.5 - hexDist(gv);
        float w = fwidth(e) * 1.2;
        float line = 1.0 - smoothstep(0.012, 0.012 + w, e);
        float d = length(vW.xz);
        float fade = 1.0 - smoothstep(uRadius * 0.55, uRadius * 1.25, d);
        float centre = 1.0 - smoothstep(0.0, uRadius * 0.9, d);
        vec3 col = mix(uFloor, uLine, line * 0.9) + uGlow * centre * 0.05;
        gl_FragColor = vec4(col, (0.35 + line * 0.65) * fade * uAlpha);
      }`,
  });
}

// ---- the scene ---------------------------------------------------------------------------

export function createHive(stage, hooks, opts) {
  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance' });
  } catch (err) {
    hooks.fatal('WebGL could not start (' + (err?.message || err) + ')');
    return null;
  }
  const options = { allLabels: false, autoOrbit: false, reducedMotion: false, ...opts };
  renderer.setPixelRatio(Math.min(MAX_DPR, window.devicePixelRatio || 1));
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.05;
  const canvas = renderer.domElement;
  canvas.className = 'hive-canvas';
  const labels = new CSS2DRenderer();
  labels.domElement.className = 'hive-labels';
  const tip = document.createElement('div');
  tip.className = 'hive-tip';
  tip.hidden = true;
  stage.prepend(canvas, labels.domElement, tip);

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(40, 1, 0.1, 4000);
  camera.position.set(0, 30, 34);
  const controls = new OrbitControls(camera, canvas);
  controls.enableDamping = true;
  controls.dampingFactor = 0.09;
  controls.maxPolarAngle = Math.PI * 0.47;   // never under the floor
  controls.minDistance = 3;
  controls.maxDistance = 600;
  controls.screenSpacePanning = false;       // pan along the floor
  controls.autoRotateSpeed = 0.35;

  const pmrem = new THREE.PMREMGenerator(renderer);
  const envTex = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
  scene.environment = envTex;
  scene.environmentIntensity = 0.55;
  const hemi = new THREE.HemisphereLight(0xffffff, 0x444444, 0.7);
  const key = new THREE.DirectionalLight(0xffffff, 1.6);
  key.position.set(-18, 40, 22);
  scene.add(hemi, key);

  let P = palette();
  const floor = new THREE.Mesh(new THREE.PlaneGeometry(1, 1).rotateX(-Math.PI / 2), floorMaterial(P));
  floor.renderOrder = -2;
  floor.position.y = -0.01;
  scene.add(floor);
  const world = new THREE.Group();   // cells, edges, zones, overlays
  scene.add(world);

  const composer = new EffectComposer(renderer);
  composer.addPass(new RenderPass(scene, camera));
  const bloom = new UnrealBloomPass(new THREE.Vector2(256, 256), 0.42, 0.45, 0.8);
  composer.addPass(bloom);
  composer.addPass(new OutputPass());

  const cells = new Map();     // id → Cell
  const links = new Map();     // "from>to" → Arc
  const arcs = new Map();      // conflict key → Arc
  const zones = new Map();     // zone key → Zone
  const guards = new Map();    // root id → ring
  let selected = null, hovered = null;
  let autoFit = true, lastBounds = null, camAnim = null;
  let dirty = true, raf = 0, last = 0, disposed = false, idleSince = performance.now();
  const clock0 = performance.now();

  // ---- theme ----

  function applyTheme() {
    P = palette();
    scene.background = P.bg;
    scene.fog = new THREE.Fog(P.bg, 60, 220);
    const u = floor.material.uniforms;
    u.uLine.value.copy(P.line); u.uGlow.value.copy(P.accent); u.uFloor.value.copy(P.floor); u.uAlpha.value = P.dark ? 0.55 : 0.32;
    hemi.groundColor.set(P.dark ? 0x30343c : 0xb8bec8);
    hemi.intensity = P.dark ? 0.55 : 0.75;
    key.intensity = P.dark ? 1.5 : 1.5;
    scene.environmentIntensity = P.dark ? 0.45 : 0.35;
    renderer.toneMappingExposure = P.dark ? 1.05 : 1.0;
    for (const c of cells.values()) c.style();
    for (const z of zones.values()) z.style();
    for (const a of links.values()) a.style();
    for (const a of arcs.values()) a.style();
    dirty = true;
  }

  // ---- cells ----

  class Cell {
    constructor(n) {
      this.id = n.id;
      this.n = n;
      this.group = new THREE.Group();
      this.group.userData.cellId = n.id;
      this.bodyMat = new THREE.MeshStandardMaterial({ metalness: 0.25, roughness: 0.32, transparent: true });
      this.capMat = new THREE.MeshStandardMaterial({ metalness: 0.1, roughness: 0.25, transparent: true });
      this.edgeMat = new THREE.LineBasicMaterial({ transparent: true });
      this.body = new THREE.Mesh(HEX, this.bodyMat);
      this.body.userData.cellId = n.id;
      this.edges = new THREE.LineSegments(HEX_EDGES, this.edgeMat);
      this.cap = new THREE.Mesh(CAP, this.capMat);
      this.cap.userData.cellId = n.id;
      this.haloMat = new THREE.SpriteMaterial({ map: glowTexture(), transparent: true, depthWrite: false });
      this.halo = new THREE.Sprite(this.haloMat);
      this.ringMat = new THREE.MeshBasicMaterial({ transparent: true, depthWrite: false, side: THREE.DoubleSide });
      this.ripple = new THREE.Mesh(HEX_RING, this.ringMat);
      this.selMat = new THREE.MeshBasicMaterial({ transparent: true, depthWrite: false, side: THREE.DoubleSide });
      this.sel = new THREE.Mesh(HEX_RING, this.selMat);
      this.sel.position.y = 0.02;
      this.group.add(this.body, this.edges, this.cap, this.halo, this.ripple, this.sel);
      this.label = new CSS2DObject(document.createElement('div'));
      this.label.center.set(0.5, 1);
      this.label.element.className = 'hive-label-anchor';
      this.label.element.addEventListener('click', (e) => { e.stopPropagation(); hooks.select(this.id); });
      this.label.element.addEventListener('dblclick', (e) => { e.stopPropagation(); focus(this.id); });
      this.label.element.addEventListener('pointerenter', () => setHover(this.id));
      this.label.element.addEventListener('pointerleave', () => { if (hovered === this.id) setHover(null); });
      this.label.element.addEventListener('wheel', (e) => { canvas.dispatchEvent(new WheelEvent('wheel', e)); e.preventDefault(); }, { passive: false });
      this.group.add(this.label);
      this.cur = { x: n.x, z: n.z, r: 0, h: 0, o: 0 };
      this.phase = Math.random() * Math.PI * 2;
      this.risk = null;
      this.labelSig = null;
      this.leaving = false;
      world.add(this.group);
      this.set(n);
    }
    set(n) {
      this.n = n;
      this.leaving = false;
      this.t = { x: n.x, z: n.z, r: n.r, h: n.h, o: n.dim ? 0.22 : 1 };
      if (options.reducedMotion) Object.assign(this.cur, this.t);
      if (this.labelSig !== n.labelSig) {
        this.labelSig = n.labelSig;
        this.label.element.replaceChildren(n.label(n));
        this.labelSize = null;
        this.titleLen = (this.label.element.querySelector('.hv-l2')?.textContent || '').length;
      }
      if (n.risk && !this.risk) {
        this.risk = new THREE.Mesh(RISK, new THREE.MeshStandardMaterial({ color: P.red, emissive: P.red, emissiveIntensity: 1.6 }));
        this.group.add(this.risk);
      } else if (!n.risk && this.risk) { this.group.remove(this.risk); this.risk.material.dispose(); this.risk = null; }
      this.style();
    }
    style() {
      const n = this.n;
      this.look = look(n, P);
      const col = new THREE.Color(n.color);
      const L = this.look;
      if (P.dark) {
        this.bodyMat.color.copy(col).lerp(P.bg, L.done ? 0.72 : 0.5);
        this.bodyMat.emissive.copy(col).multiplyScalar(L.done ? 0.02 : 0.08);
        this.edgeMat.color.copy(col).lerp(new THREE.Color(0xffffff), 0.25).multiplyScalar(L.done ? 0.35 : 1.05);
      } else {
        this.bodyMat.color.copy(col).lerp(new THREE.Color(0xffffff), L.done ? 0.6 : 0.05).multiplyScalar(L.done ? 1 : 0.88);
        this.bodyMat.emissive.setRGB(0, 0, 0);
        this.edgeMat.color.copy(col).multiplyScalar(L.done ? 0.7 : 0.5);
      }
      if (L.done) { const g = (this.bodyMat.color.r + this.bodyMat.color.g + this.bodyMat.color.b) / 3; this.bodyMat.color.lerp(new THREE.Color(g, g, g), 0.55); }
      this.capMat.color.copy(L.done ? this.bodyMat.color : col);
      this.capMat.emissive.copy(L.glow);
      this.haloMat.color.copy(L.glow);
      this.haloMat.blending = P.dark ? THREE.AdditiveBlending : THREE.NormalBlending;
      this.ringMat.color.copy(L.glow);
      this.selMat.color.copy(P.dark ? new THREE.Color(0xffffff) : P.accent);
      this.label.element.classList.toggle('dim', !!n.dim);
      this.label.element.classList.toggle('root', !!n.isRoot);
      dirty = true;
    }
    /** Ease towards the target; returns true while still moving. */
    step(k) {
      const c = this.cur, t = this.t;
      let moving = false;
      for (const p of ['x', 'z', 'r', 'h', 'o']) {
        const d = t[p] - c[p];
        if (Math.abs(d) > 0.001) { c[p] += d * k; moving = true; } else c[p] = t[p];
      }
      return moving;
    }
    /** Apply the current values + time-based effects. */
    apply(time, sel, hov) {
      const c = this.cur, L = this.look, n = this.n;
      const r = Math.max(c.r, 0.0001), hgt = Math.max(c.h, 0.0001);
      this.group.position.set(c.x, 0, c.z);
      this.body.scale.set(r, hgt, r);
      this.edges.scale.set(r * 1.002, hgt * 1.002, r * 1.002);
      this.cap.scale.set(r, 1, r);
      this.cap.position.y = hgt + 0.02;
      const pulse = L.pulse && !options.reducedMotion ? 0.62 + 0.38 * Math.sin(time * L.pulse + this.phase) : 1;
      const dimK = n.dim ? 0.15 : 1;
      const em = L.k * pulse * dimK * (hov ? 1.3 : 1) * (P.dark ? 0.62 : 0.4);
      this.capMat.emissiveIntensity = em;
      this.bodyMat.opacity = c.o;
      this.capMat.opacity = c.o;
      this.edgeMat.opacity = c.o * (L.done ? 0.5 : 0.85) * (hov || sel ? 1.25 : 1);
      this.halo.visible = !L.done && !n.dim && c.o > 0.3;
      this.halo.position.y = hgt * 0.5 + 0.2;
      this.halo.scale.setScalar(r * (n.isRoot ? 5 : 4.4) * (0.85 + 0.15 * pulse));
      this.haloMat.opacity = (P.dark ? 0.32 : 0.22) * Math.min(1, L.k) * pulse * c.o;
      this.ripple.visible = L.ripple && !options.reducedMotion && !n.dim;
      if (this.ripple.visible) {
        const f = ((time * (L.pulse > 3 ? 0.9 : 0.45) + this.phase) % 1);
        this.ripple.scale.setScalar(r * (1.05 + f * 0.9));
        this.ringMat.opacity = (1 - f) * (P.dark ? 0.55 : 0.45) * c.o;
        this.ripple.position.y = 0.015;
      }
      this.sel.visible = sel || hov;
      this.sel.scale.setScalar(r * (sel ? 1.28 : 1.18));
      this.selMat.opacity = sel ? 0.95 : 0.45;
      if (this.risk) {
        // Beside the cell, in front, so the label above the cell never hides it.
        this.risk.position.set(r * 0.95, hgt * 0.6 + 0.35 + (options.reducedMotion ? 0 : Math.sin(time * 2 + this.phase) * 0.08), r * 0.75);
        if (!options.reducedMotion) this.risk.rotation.y = time * 1.4;
      }
      this.label.position.y = hgt + 0.12;
    }
    get animated() { return !options.reducedMotion && (this.look.pulse > 0 || this.look.ripple || !!this.risk) && !this.n.dim; }
    dispose() {
      world.remove(this.group);
      this.label.element.remove();
      for (const m of [this.bodyMat, this.capMat, this.edgeMat, this.haloMat, this.ringMat, this.selMat, this.risk?.material]) m?.dispose();
    }
  }

  // ---- arcs (parent → sub-agent links and conflict arcs) ----

  class Arc {
    constructor(kind) {
      this.kind = kind;
      this.mat = new THREE.MeshBasicMaterial({ transparent: true, depthWrite: false });
      this.tube = new THREE.Mesh(new THREE.BufferGeometry(), this.mat);
      this.sparkMat = new THREE.SpriteMaterial({ map: glowTexture(), transparent: true, depthWrite: false, blending: THREE.AdditiveBlending });
      this.spark = new THREE.Sprite(this.sparkMat);
      this.spark.visible = false;
      this.curve = new THREE.QuadraticBezierCurve3(new THREE.Vector3(), new THREE.Vector3(), new THREE.Vector3());
      this.key = '';
      world.add(this.tube, this.spark);
      this.phase = Math.random();
    }
    set(from, to, info) { this.from = from; this.to = to; this.info = info; this.style(); }
    style() {
      if (this.kind === 'conflict') {
        this.mat.color.copy(P.red).multiplyScalar(P.dark ? 1.35 : 1);
        this.sparkMat.color.copy(P.red);
      } else {
        const c = new THREE.Color(this.info.color);
        this.mat.color.copy(c).lerp(P.dark ? new THREE.Color(0xffffff) : new THREE.Color(0x000000), P.dark ? 0.12 : 0.18);
        this.sparkMat.color.copy(c);
      }
      this.sparkMat.blending = P.dark ? THREE.AdditiveBlending : THREE.NormalBlending;
      dirty = true;
    }
    /** Follow the two cells; the tube is rebuilt only when an end actually moved. */
    apply(time) {
      const a = cells.get(this.from), b = cells.get(this.to);
      if (!a || !b) { this.tube.visible = this.spark.visible = false; return; }
      const conflict = this.kind === 'conflict';
      const key = [a.cur.x, a.cur.z, a.cur.h, b.cur.x, b.cur.z, b.cur.h].map((v) => v.toFixed(3)).join();
      if (key !== this.key) {
        this.key = key;
        const p0 = this.curve.v0.set(a.cur.x, a.cur.h + 0.03, a.cur.z);
        const p2 = this.curve.v2.set(b.cur.x, b.cur.h + 0.03, b.cur.z);
        const d = p0.distanceTo(p2);
        this.curve.v1.set((p0.x + p2.x) / 2, Math.max(p0.y, p2.y) + (conflict ? 1.6 + d * 0.32 : 0.3 + d * 0.2), (p0.z + p2.z) / 2);
        this.tube.geometry.dispose();
        this.tube.geometry = new THREE.TubeGeometry(this.curve, conflict ? 40 : ARC_PTS, conflict ? 0.06 : 0.032, 6, false);
      }
      const o = Math.min(a.cur.o, b.cur.o);
      this.tube.visible = o > 0.02;
      const live = conflict || this.info.live;
      const beat = conflict && !options.reducedMotion ? 0.7 + 0.3 * Math.sin(time * 3 + this.phase * 6) : 1;
      this.mat.opacity = (conflict ? 0.9 * beat : (this.info.done ? 0.25 : 0.7) * (this.info.dim ? 0.4 : 1)) * o;
      this.spark.visible = live && !options.reducedMotion && this.tube.visible;
      if (this.spark.visible) {
        const f = (time * (conflict ? 0.3 : 0.45) + this.phase) % 1;
        this.curve.getPoint(f, this.spark.position);
        this.spark.scale.setScalar(conflict ? 0.9 : 0.6);
        this.sparkMat.opacity = (P.dark ? 0.95 : 0.7) * Math.sin(f * Math.PI) * o;
      }
    }
    dispose() { world.remove(this.tube, this.spark); this.tube.geometry.dispose(); this.mat.dispose(); this.sparkMat.dispose(); }
  }

  // ---- zones (one plate per repository worktree) ----

  class Zone {
    constructor(z) {
      this.mat = new THREE.MeshBasicMaterial({ transparent: true, depthWrite: false });
      this.lineMat = new THREE.LineBasicMaterial({ transparent: true });
      this.plate = new THREE.Mesh(new THREE.BufferGeometry(), this.mat);
      this.plate.renderOrder = -1;
      this.outline = new THREE.LineLoop(new THREE.BufferGeometry(), this.lineMat);
      this.label = new CSS2DObject(document.createElement('div'));
      this.label.center.set(0, 0.5);
      this.label.element.className = 'hv-zone';
      world.add(this.plate, this.outline, this.label);
      this.set(z);
      this.style();
    }
    set(z) {
      const sig = [z.x0, z.z0, z.x1, z.z1].map((x) => x.toFixed(2)).join();
      if (sig !== this.sig) {
        this.sig = sig;
        const shape = roundRect(z.x0, z.z0, z.x1 - z.x0, z.z1 - z.z0, 0.9);
        this.plate.geometry.dispose();
        this.plate.geometry = new THREE.ShapeGeometry(shape, 6).rotateX(Math.PI / 2);
        this.outline.geometry.dispose();
        this.outline.geometry = new THREE.BufferGeometry().setFromPoints(shape.getPoints(6).map((p) => new THREE.Vector3(p.x, 0.006, p.y)));
        this.label.position.set(z.x0 + 0.75, 0.02, z.z1 - 0.55);
      }
      this.plate.position.y = 0.004;
      const textSig = z.label + '|' + z.sub;
      if (textSig !== this.textSig) {
        this.textSig = textSig;
        const el = this.label.element;
        el.replaceChildren();
        const a = document.createElement('span'); a.className = 'hv-zone-repo'; a.textContent = '⛁ ' + z.label;
        el.append(a);
        if (z.sub) { const b = document.createElement('span'); b.className = 'hv-zone-wt'; b.textContent = '⑂ ' + z.sub; el.append(b); }
        el.title = z.title || '';
      }
    }
    style() {
      this.mat.color.copy(P.dark ? P.floor.clone().lerp(new THREE.Color(0xffffff), 0.035) : new THREE.Color(0xffffff));
      this.mat.opacity = P.dark ? 0.75 : 0.9;
      this.lineMat.color.copy(P.line).lerp(P.accent, P.dark ? 0.12 : 0.3).multiplyScalar(P.dark ? 1 : 0.85);
      this.lineMat.opacity = P.dark ? 0.9 : 1;
    }
    dispose() { world.remove(this.plate, this.outline, this.label); this.label.element.remove(); this.plate.geometry.dispose(); this.outline.geometry.dispose(); this.mat.dispose(); this.lineMat.dispose(); }
  }

  function roundRect(x, y, w, hgt, r) {
    const s = new THREE.Shape();
    s.moveTo(x + r, y); s.lineTo(x + w - r, y); s.quadraticCurveTo(x + w, y, x + w, y + r);
    s.lineTo(x + w, y + hgt - r); s.quadraticCurveTo(x + w, y + hgt, x + w - r, y + hgt);
    s.lineTo(x + r, y + hgt); s.quadraticCurveTo(x, y + hgt, x, y + hgt - r);
    s.lineTo(x, y + r); s.quadraticCurveTo(x, y, x + r, y);
    return s;
  }

  // ---- guard rings (runaway alerts around a session's cluster) ----

  function guardRing(c, level) {
    let g = guards.get(c.rootId);
    if (!g) {
      const mat = new THREE.MeshBasicMaterial({ transparent: true, depthWrite: false, side: THREE.DoubleSide });
      const mesh = new THREE.Mesh(new THREE.RingGeometry(1, 1.035, 6, 1, Math.PI / 2).rotateX(-Math.PI / 2), mat);
      const haloMat = new THREE.MeshBasicMaterial({ transparent: true, depthWrite: false, side: THREE.DoubleSide, blending: THREE.AdditiveBlending });
      const halo = new THREE.Mesh(new THREE.RingGeometry(0.93, 1.1, 6, 1, Math.PI / 2).rotateX(-Math.PI / 2), haloMat);
      world.add(mesh, halo);
      g = { mesh, halo, phase: Math.random() * 6 };
      guards.set(c.rootId, g);
    }
    g.level = level;
    g.mesh.position.set(c.x, 0.03, c.z);
    g.halo.position.set(c.x, 0.025, c.z);
    g.mesh.scale.setScalar(c.radius * 1.02);
    g.halo.scale.setScalar(c.radius * 1.02);
    const col = level === 'critical' ? P.red : P.orange;
    g.mesh.material.color.copy(col);
    g.halo.material.color.copy(col);
    g.halo.material.blending = P.dark ? THREE.AdditiveBlending : THREE.NormalBlending;
  }

  // ---- update from the store ----

  function update(m, { theme = false } = {}) {
    if (disposed) return;
    if (theme) applyTheme();
    const layout = layoutHive(m.tree);
    const selChanged = selected !== m.selected;
    selected = m.selected;

    const seen = new Set();
    for (const n of layout.cells) {
      seen.add(n.id);
      const c = cells.get(n.id);
      if (c) c.set(n); else cells.set(n.id, new Cell(n));
    }
    for (const [id, c] of cells) if (!seen.has(id) && !c.leaving) { c.leaving = true; c.t = { ...c.t, r: 0, h: 0, o: 0 }; if (options.reducedMotion) Object.assign(c.cur, c.t); }

    const byId = new Map(layout.cells.map((n) => [n.id, n]));
    const linkSeen = new Set();
    for (const l of layout.links) {
      const k = l.from + '>' + l.to;
      linkSeen.add(k);
      const child = byId.get(l.to);
      const info = { color: child.color, live: ['running', 'starting', 'blocked'].includes(child.status), done: look(child, P).done, dim: child.dim };
      let a = links.get(k);
      if (!a) links.set(k, (a = new Arc('link')));
      a.set(l.from, l.to, info);
    }
    for (const [k, a] of links) if (!linkSeen.has(k)) { a.dispose(); links.delete(k); }

    const arcSeen = new Set();
    for (const c of m.conflicts) {
      if (!byId.has(c.from) || !byId.has(c.to)) continue;
      arcSeen.add(c.key);
      let a = arcs.get(c.key);
      if (!a) arcs.set(c.key, (a = new Arc('conflict')));
      a.set(c.from, c.to, c);
    }
    for (const [k, a] of arcs) if (!arcSeen.has(k)) { a.dispose(); arcs.delete(k); }

    const zoneSeen = new Set();
    for (const z of layout.zones) {
      zoneSeen.add(z.key);
      const cur = zones.get(z.key);
      if (cur) cur.set(z); else zones.set(z.key, new Zone(z));
    }
    for (const [k, z] of zones) if (!zoneSeen.has(k)) { z.dispose(); zones.delete(k); }

    const guardSeen = new Set();
    for (const c of layout.clusters) {
      const root = m.tree.find((r) => r.id === c.rootId);
      if (root?.guard) { guardSeen.add(c.rootId); guardRing(c, root.guard); }
    }
    for (const [k, g] of guards) if (!guardSeen.has(k)) { world.remove(g.mesh, g.halo); g.mesh.geometry.dispose(); g.mesh.material.dispose(); g.halo.geometry.dispose(); g.halo.material.dispose(); guards.delete(k); }

    // Floor + camera follow the extent of the hive.
    const b = layout.bounds;
    const span = Math.max(12, Math.hypot(b.x1 - b.x0, b.z1 - b.z0));
    floor.scale.set(span * 6 + 200, 1, span * 6 + 200);
    floor.material.uniforms.uRadius.value = span * 0.9 + 10;
    scene.fog.near = span * 1.2 + 20; scene.fog.far = span * 4 + 120;
    const changed = !lastBounds || Math.abs(lastBounds.x1 - b.x1) > 0.5 || Math.abs(lastBounds.z1 - b.z1) > 0.5;
    lastBounds = { ...b };
    if (autoFit && changed) fit(!camAnim && !last);
    else if (selChanged && selected && cells.has(selected) && !inView(selected)) panTo(selected);
    labelVisibility();
    dirty = true;
    kick();
  }

  // ---- camera ----

  /** Camera pose that shows the whole hive from ~50° above the floor: the closest distance at which every
   *  corner of the footprint (plus room for labels above the cells) projects inside the viewport. */
  function fitPose() {
    const b = lastBounds || { x0: -6, z0: -6, x1: 6, z1: 6 };
    const elev = THREE.MathUtils.degToRad(50), az = THREE.MathUtils.degToRad(-12);
    const dir = new THREE.Vector3(Math.sin(az) * Math.cos(elev), Math.sin(elev), Math.cos(az) * Math.cos(elev));
    const target = new THREE.Vector3((b.x0 + b.x1) / 2, 0, (b.z0 + b.z1) / 2);
    const pts = [];
    for (const x of [b.x0, b.x1]) for (const z of [b.z0, b.z1]) for (const y of [0, 2.5]) pts.push(new THREE.Vector3(x, y, z));
    const cam = camera.clone();
    const place = (dist) => { cam.position.copy(dir).multiplyScalar(dist).add(target); cam.lookAt(target); cam.updateMatrixWorld(); };
    const ys = () => pts.map((p) => p.clone().project(cam).y);
    const fits = (dist) => {
      place(dist);
      return pts.every((p) => { const v = p.clone().project(cam); return Math.abs(v.x) < 0.96 && Math.abs(v.y) < 0.9 && v.z < 1; });
    };
    // Perspective squeezes the far half, so the footprint sits low when the target is its centre: search
    // the distance, then slide the target along the floor until the projection is centred, and repeat.
    const fwd = new THREE.Vector3(-dir.x, 0, -dir.z).normalize();
    const depth = Math.max(4, b.z1 - b.z0);
    let hi = 8;
    for (let pass = 0; pass < 4; pass++) {
      let lo = 4; hi = 8;
      while (!fits(hi) && hi < 3000) { lo = hi; hi *= 1.6; }
      for (let i = 0; i < 18; i++) { const mid = (lo + hi) / 2; if (fits(mid)) hi = mid; else lo = mid; }
      place(hi);
      const y = ys(), mid = (Math.max(...y) + Math.min(...y)) / 2;
      if (Math.abs(mid) < 0.02) break;
      target.addScaledVector(fwd, mid * depth * 0.45);
    }
    return { pos: dir.multiplyScalar(Math.max(9, hi)).add(target), target };
  }

  function animateTo(pos, target, instant = false) {
    if (instant || options.reducedMotion) {
      camera.position.copy(pos); controls.target.copy(target); controls.update(); camAnim = null; dirty = true; return;
    }
    camAnim = { p0: camera.position.clone(), t0: controls.target.clone(), p1: pos, t1: target, start: performance.now(), dur: 900 };
    kick();
  }

  function fit(instant = false) {
    autoFit = true;
    const { pos, target } = fitPose();
    animateTo(pos, target, instant);
  }

  function focus(id) {
    const c = cells.get(id);
    if (!c) return;
    autoFit = false;
    const target = new THREE.Vector3(c.t.x, c.t.h * 0.5, c.t.z);
    const dir = camera.position.clone().sub(controls.target).normalize();
    const dist = c.n.isRoot ? 17 : 10;
    animateTo(target.clone().add(dir.multiplyScalar(dist)), target);
  }

  function panTo(id) {
    const c = cells.get(id);
    const target = new THREE.Vector3(c.t.x, 0, c.t.z);
    const delta = target.clone().sub(controls.target);
    animateTo(camera.position.clone().add(delta), target);
  }

  const frustum = new THREE.Frustum(), projM = new THREE.Matrix4();
  function inView(id) {
    const c = cells.get(id);
    camera.updateMatrixWorld();
    projM.multiplyMatrices(camera.projectionMatrix, camera.matrixWorldInverse);
    frustum.setFromProjectionMatrix(projM);
    return frustum.containsPoint(new THREE.Vector3(c.t.x, c.t.h, c.t.z));
  }

  /**
   * Which labels show: sessions always, sub-agents when close, selected, hovered, blocked or failed (or
   * "All labels"). Then a screen-space pass drops labels that would overlap a more important one, so a
   * big hive stays readable; hovering any cell still shows its tooltip.
   */
  const LABEL_EST = { root: [190, 52], sub: [120, 20] };
  function labelVisibility() {
    const cam = camera.position;
    camera.updateMatrixWorld();
    const w = stage.clientWidth, hh = stage.clientHeight;
    const cand = [];
    for (const c of cells.values()) {
      const n = c.n, id = c.id;
      const dist = Math.hypot(cam.x - c.cur.x, cam.y - c.cur.h, cam.z - c.cur.z);
      const focus = selected === id || hovered === id;
      const want = !c.leaving && (n.isRoot || options.allLabels || dist < SUB_LABEL_DIST || focus || n.status === 'blocked' || n.status === 'failed') && (!n.dim || focus);
      const el = c.label.element;
      el.classList.toggle('sel', selected === id);
      el.classList.toggle('hov', hovered === id);
      const far = n.isRoot && !focus && dist > 34;
      if (el.classList.contains('far') !== far) { el.classList.toggle('far', far); c.labelSize = null; }
      if (!want) { c.label.visible = false; continue; }
      const p = new THREE.Vector3(c.cur.x, c.cur.h + 0.12, c.cur.z).project(camera);
      if (p.z > 1) { c.label.visible = false; continue; }
      const x = (p.x + 1) / 2 * w, y = (1 - p.y) / 2 * hh;
      const est = LABEL_EST[n.isRoot ? 'root' : 'sub'];
      const [lw, lh] = c.labelSize || [est[0], far ? est[1] - 14 : est[1]];
      const prio = (selected === id ? 1e6 : 0) + (hovered === id ? 5e5 : 0) + (n.isRoot ? 1e4 : 0)
        + (n.status === 'blocked' ? 3000 : n.status === 'failed' ? 2000 : 0) + (n.dim ? -500 : 0) - p.z * 10 + Math.log10(1 + n.tokens);
      const box = (bw, bh) => ({ x0: x - bw / 2, x1: x + bw / 2, y0: y - bh - 14, y1: y });
      // Sessions fall back to a one-line chip (title only) before they lose their label.
      const mini = n.isRoot && !focus ? box(Math.min(190, (c.titleLen || 20) * 6.3 + 22), 20) : null;
      cand.push({ c, prio, full: box(lw, lh), mini });
    }
    cand.sort((a, b) => b.prio - a.prio);
    const placed = [];
    const free = (r) => !placed.some((q) => r.x0 < q.x1 + 4 && r.x1 > q.x0 - 4 && r.y0 < q.y1 + 2 && r.y1 > q.y0 - 2);
    for (const k of cand) {
      const el = k.c.label.element;
      let r = k.prio >= 5e5 || free(k.full) ? k.full : k.mini && free(k.mini) ? k.mini : null;
      el.classList.toggle('mini', !!r && r === k.mini);
      k.c.label.visible = !!r;
      if (r) placed.push(r);
    }
  }

  /** Measure labels once they are laid out (sizes feed the overlap pass). */
  function measureLabels() {
    let changed = false;
    for (const c of cells.values()) {
      if (c.labelSize || !c.label.visible || c.label.element.classList.contains('mini')) continue;
      const el = c.label.element;
      if (el.offsetWidth) { c.labelSize = [el.offsetWidth, el.offsetHeight - 14]; changed = true; }
    }
    if (changed) { labelVisibility(); dirty = true; kick(); }
  }

  // ---- interaction ----

  const raycaster = new THREE.Raycaster();
  const pointer = new THREE.Vector2();
  let down = null, lastMove = null;

  function pick(ev) {
    const r = canvas.getBoundingClientRect();
    pointer.set(((ev.clientX - r.left) / r.width) * 2 - 1, -((ev.clientY - r.top) / r.height) * 2 + 1);
    raycaster.setFromCamera(pointer, camera);
    const targets = [];
    for (const c of cells.values()) if (!c.leaving) targets.push(c.body, c.cap);
    const hit = raycaster.intersectObjects(targets, false)[0];
    return hit ? hit.object.userData.cellId : null;
  }

  function setHover(id) {
    if (hovered === id) return;
    hovered = id;
    canvas.style.cursor = id ? 'pointer' : '';
    if (!id) tip.hidden = true;
    else {
      const el = hooks.tooltip(id);
      tip.replaceChildren(...(el ? [el] : []));
      tip.hidden = !el;
    }
    labelVisibility();
    dirty = true; kick();
  }

  function placeTip(ev) {
    if (tip.hidden) return;
    const r = stage.getBoundingClientRect();
    const x = ev.clientX - r.left, y = ev.clientY - r.top;
    const w = tip.offsetWidth, hh = tip.offsetHeight;
    tip.style.left = Math.max(6, Math.min(r.width - w - 6, x + 16)) + 'px';
    tip.style.top = Math.max(6, y + 18 + hh > r.height ? y - hh - 12 : y + 18) + 'px';
  }

  const onMove = (ev) => {
    lastMove = ev;
    if (down && ev.buttons) return; // dragging: no hover changes
    setHover(pick(ev));
    placeTip(ev);
  };
  const onLeave = (ev) => { if (!ev.relatedTarget?.closest?.('.hive-label-anchor')) setHover(null); };
  const onStageMove = (ev) => { lastMove = ev; placeTip(ev); };
  const onDown = (ev) => { down = { x: ev.clientX, y: ev.clientY }; };
  const onUp = (ev) => {
    if (down && Math.hypot(ev.clientX - down.x, ev.clientY - down.y) < 5 && ev.button === 0) {
      const id = pick(ev);
      if (id) hooks.select(id);
    }
    down = null;
  };
  const onDbl = (ev) => { const id = pick(ev); if (id) focus(id); };
  const onStart = () => { autoFit = false; camAnim = null; idleSince = performance.now(); controls.autoRotate = false; };
  const onChange = () => { dirty = true; idleSince = performance.now(); kick(); };
  canvas.addEventListener('pointermove', onMove);
  canvas.addEventListener('pointerleave', onLeave);
  canvas.addEventListener('pointerdown', onDown);
  canvas.addEventListener('pointerup', onUp);
  canvas.addEventListener('dblclick', onDbl);
  stage.addEventListener('pointermove', onStageMove);
  controls.addEventListener('start', onStart);
  controls.addEventListener('change', onChange);

  /** The nearest agent from `id` in a screen direction (arrow-key navigation). */
  function neighbour(id, dir) {
    const list = [...cells.values()].filter((c) => !c.leaving && !c.n.dim);
    if (!list.length) return null;
    const from = cells.get(id);
    if (!from || from.leaving) return (list.find((c) => c.n.isRoot) || list[0]).id;
    camera.updateMatrixWorld();
    const scr = (c) => new THREE.Vector3(c.t.x, c.t.h, c.t.z).project(camera);
    const p0 = scr(from);
    const want = { left: [-1, 0], right: [1, 0], up: [0, 1], down: [0, -1] }[dir];
    let best = null, bestScore = Infinity;
    for (const c of list) {
      if (c === from) continue;
      const p = scr(c);
      const dx = (p.x - p0.x) * camera.aspect, dy = p.y - p0.y;
      const along = dx * want[0] + dy * want[1];
      if (along <= 0.001) continue;
      const across = Math.abs(dx * want[1] - dy * want[0]);
      const score = along + across * 2.2;
      if (score < bestScore) { bestScore = score; best = c; }
    }
    if (best && !inView(best.id)) panTo(best.id);
    return best?.id || null;
  }

  // ---- size, visibility, loop ----

  function resize() {
    const w = Math.max(1, stage.clientWidth), hh = Math.max(1, stage.clientHeight);
    const dpr = Math.min(MAX_DPR, window.devicePixelRatio || 1);
    renderer.setPixelRatio(dpr);
    renderer.setSize(w, hh, false);
    composer.setPixelRatio(dpr);
    composer.setSize(w, hh);
    labels.setSize(w, hh);
    camera.aspect = w / hh;
    camera.updateProjectionMatrix();
    if (autoFit && lastBounds) fit(true);
    dirty = true; kick();
  }
  const ro = new ResizeObserver(resize);
  ro.observe(stage);

  const onVisibility = () => { if (!document.hidden) { last = 0; dirty = true; kick(); } };
  document.addEventListener('visibilitychange', onVisibility);

  const onLost = (e) => { e.preventDefault(); hooks.fatal('The GPU context was lost'); };
  canvas.addEventListener('webglcontextlost', onLost);

  function kick() { if (!raf && !disposed && !document.hidden) raf = requestAnimationFrame(frame); }

  function frame(now) {
    raf = 0;
    if (disposed || document.hidden) return;
    const dt = last ? Math.min(0.1, (now - last) / 1000) : 0.016;
    last = now;
    const time = (now - clock0) / 1000;

    controls.autoRotate = options.autoOrbit && !options.reducedMotion && !camAnim && now - idleSince > 6000;
    let cam = false;
    if (camAnim) {
      const f = Math.min(1, (now - camAnim.start) / camAnim.dur);
      const e = f < 0.5 ? 4 * f * f * f : 1 - Math.pow(-2 * f + 2, 3) / 2;
      camera.position.lerpVectors(camAnim.p0, camAnim.p1, e);
      controls.target.lerpVectors(camAnim.t0, camAnim.t1, e);
      if (f >= 1) camAnim = null;
      cam = true;
    }
    cam = controls.update(dt) || cam;

    const k = options.reducedMotion ? 1 : 1 - Math.exp(-EASE * dt);
    let moving = false;
    let animated = !options.reducedMotion && (arcs.size > 0 || guards.size > 0);
    for (const [id, c] of cells) {
      if (c.step(k)) moving = true;
      if (c.leaving && c.cur.o < 0.02 && c.cur.r < 0.02) { c.dispose(); cells.delete(id); continue; }
      c.apply(time, selected === id, hovered === id);
      if (c.animated) animated = true;
    }
    if (moving || animated || dirty || cam) {
      for (const a of links.values()) { a.apply(time); if (a.info.live) animated = animated || !options.reducedMotion; }
      for (const a of arcs.values()) a.apply(time);
      for (const g of guards.values()) {
        const p = options.reducedMotion ? 1 : 0.6 + 0.4 * Math.sin(time * (g.level === 'critical' ? 4 : 2) + g.phase);
        g.mesh.material.opacity = 0.85 * p;
        g.halo.material.opacity = (P.dark ? 0.35 : 0.2) * p;
      }
    }
    if (cam || moving) labelVisibility();
    if (moving || animated || dirty || cam || controls.autoRotate) {
      if (P.dark) composer.render(dt); else renderer.render(scene, camera);
      labels.render(scene, camera);
      measureLabels();
      dirty = false;
      if (lastMove && hovered && (cam || moving)) placeTip(lastMove);
      kick();
    }
  }

  // ---- public ----

  function setOptions(o) {
    Object.assign(options, o);
    if (options.reducedMotion) for (const c of cells.values()) Object.assign(c.cur, c.t);
    labelVisibility();
    dirty = true; kick();
  }

  function dispose() {
    if (disposed) return;
    disposed = true;
    if (raf) cancelAnimationFrame(raf);
    ro.disconnect();
    document.removeEventListener('visibilitychange', onVisibility);
    canvas.removeEventListener('pointermove', onMove);
    canvas.removeEventListener('pointerleave', onLeave);
    canvas.removeEventListener('pointerdown', onDown);
    canvas.removeEventListener('pointerup', onUp);
    canvas.removeEventListener('dblclick', onDbl);
    stage.removeEventListener('pointermove', onStageMove);
    canvas.removeEventListener('webglcontextlost', onLost);
    controls.removeEventListener('start', onStart);
    controls.removeEventListener('change', onChange);
    controls.dispose();
    for (const c of cells.values()) c.dispose();
    for (const a of links.values()) a.dispose();
    for (const a of arcs.values()) a.dispose();
    for (const z of zones.values()) z.dispose();
    for (const g of guards.values()) { g.mesh.geometry.dispose(); g.mesh.material.dispose(); g.halo.geometry.dispose(); g.halo.material.dispose(); }
    cells.clear(); links.clear(); arcs.clear(); zones.clear(); guards.clear();
    floor.geometry.dispose(); floor.material.dispose();
    envTex.dispose(); pmrem.dispose();
    composer.dispose();
    for (const g of [HEX, HEX_EDGES, CAP, HEX_RING, RISK]) g.dispose();
    glowTex?.dispose();
    renderer.dispose();
    renderer.forceContextLoss();
    canvas.remove(); labels.domElement.remove(); tip.remove();
  }

  applyTheme();
  resize();
  return { update, setOptions, fit: () => fit(false), focus, neighbour, dispose };
}
