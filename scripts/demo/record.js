// Pass 1: records a scripted tour of the demo dashboard as timestamped 2x screencast frames (frames/), so the
// zooms in pass 2 stay sharp, plus the mouse path (the pointer is drawn smoothly in pass 2), and logs, for each step, when it
// starts, its caption and the screen area to zoom to (steps.json). Run scripts/demo/setup.sh first.
// Needs: npm i puppeteer-core, and Chrome at $CHROME or /usr/bin/google-chrome.
const puppeteer = require('puppeteer-core');
const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');

const OUT = process.argv[2] || '.';
const URL = process.env.OVERHIVE_DEMO_URL || 'http://127.0.0.1:47900/';
const W = 1440, H = 900;
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const agents = (phase) => spawn(path.join(__dirname, 'agents.sh'), [phase], { stdio: 'inherit' });

// Mouse moves and clicks, timed with the same clock as the frames; pass 2 draws the pointer from these.
const TRACK = `
  window.__mouse = [];
  for (const type of ['mousemove', 'mousedown'])
    document.addEventListener(type, (e) => window.__mouse.push({ at: Date.now(), x: e.clientX, y: e.clientY, down: type === 'mousedown' }), true);`;

(async () => {
  // The Hive tab is WebGL: render on the GPU (CHROME_GL=swiftshader for a software fallback, slower and choppier).
  const gl = process.env.CHROME_GL === 'swiftshader' ? ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] : ['--use-angle=gl-egl', '--enable-gpu', '--ignore-gpu-blocklist'];
  // A real 2x window (not an emulated viewport), so the screencast below delivers 2x frames. The window
  // height includes the headless browser's 87 px of chrome.
  const b = await puppeteer.launch({ executablePath: process.env.CHROME || '/usr/bin/google-chrome', headless: 'new', defaultViewport: null,
    args: ['--no-sandbox', '--hide-scrollbars', `--window-size=${W},${H + 87}`, '--force-device-scale-factor=2', ...gl] });
  const p = (await b.pages())[0] || await b.newPage();
  const vp = await p.evaluate(() => [innerWidth, innerHeight]);
  if (vp[0] !== W || vp[1] !== H) throw new Error(`viewport is ${vp.join('x')}, expected ${W}x${H}`);
  await p.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'dark' }]);
  await p.goto(URL, { waitUntil: 'domcontentloaded' });
  await p.waitForSelector('.hv-label', { timeout: 15000 });
  await p.evaluate(TRACK);
  await p.mouse.move(W / 2, H / 2);
  await wait(1200);

  const steps = [];
  let t0 = 0;
  const box = async (sel, crop) => {
    const b = await (await p.waitForSelector(sel, { timeout: 10000 })).boundingBox();
    return crop ? { x: b.x, y: b.y, width: Math.min(b.width, crop.width ?? b.width), height: Math.min(b.height, crop.height ?? b.height) } : b;
  };
  const step = async (caption, rect) => steps.push({ t: (Date.now() - t0) / 1000, caption, rect: rect && { x: rect.x, y: rect.y, w: rect.width, h: rect.height } });
  const click = async (sel, after) => {
    const b = await box(sel);
    await p.mouse.move(b.x + Math.min(b.width / 2, 60), b.y + b.height / 2, { steps: 22 });
    await wait(200); await p.mouse.down(); await p.mouse.up();
    await wait(after);
  };

  fs.rmSync(path.join(OUT, 'frames'), { recursive: true, force: true });
  fs.mkdirSync(path.join(OUT, 'frames'), { recursive: true });
  const frames = [];
  const cdp = await p.createCDPSession();
  // Screencast: Chrome sends a frame whenever the page repaints (about 30/s at 2x and JPEG quality 80), which
  // keeps the 3D Hive motion smooth; one screenshot at a time only managed about 8/s.
  let pending = Promise.resolve();
  cdp.on('Page.screencastFrame', (f) => {
    const file = `frames/${String(frames.length).padStart(5, '0')}.jpg`;
    frames.push({ t: f.metadata.timestamp - t0 / 1000, file });
    pending = pending.then(() => fs.promises.writeFile(path.join(OUT, file), Buffer.from(f.data, 'base64')));
    cdp.send('Page.screencastFrameAck', { sessionId: f.sessionId }).catch(() => {});
  });
  t0 = Date.now();
  await cdp.send('Page.startScreencast', { format: 'jpeg', quality: 80, maxWidth: W * 2, maxHeight: H * 2, everyNthFrame: 1 });
  const live = agents('live'); agents('inbox');

  const stage = await box('.hive-stage');
  const cx = stage.x + stage.width / 2, cy = stage.y + stage.height / 2;
  const drag = async (dx, dy, ms) => {   // orbit the Hive: press, move slowly, release
    await p.mouse.move(cx - dx / 2, cy - dy / 2, { steps: 12 }); await p.mouse.down();
    await p.mouse.move(cx + dx / 2, cy + dy / 2, { steps: Math.round(ms / 16) }); await p.mouse.up();
  };
  const label = (text) => `.hv-label ::-p-text(${text})`;

  await step('Every agent and sub-agent, from any AI tool, live in 3D', null);
  await wait(1600);
  await drag(360, -40, 2400);
  await wait(900);
  await step('Conflicts are flagged before anyone merges', await box('#conflict-banner', { width: 1000 }));
  await wait(2800);
  await step('Hover any agent for its status, model and tokens', stage);
  const fl = await box(label('Fix flaky order tests'));
  await p.mouse.move(fl.x + fl.width / 2, fl.y + fl.height + 42, { steps: 24 });
  await wait(2400);
  await step('Click it to see exactly what it is doing', null);
  await click(label('Add checkout API'), 2600);
  await click('.attn-badge', 500);
  await step('One queue for everything that needs you', await box('.attn-panel'));
  await wait(3000);
  await p.keyboard.press('Escape'); await wait(300);
  await step('Risky commands are recorded, with the agent that ran them', await box('#center', { height: 300 }));
  await click('#tabs ::-p-text(Risks)', 2600);
  await step('Which agent changed which file, in which worktree', await box('#center'));
  await click('#tabs ::-p-text(Worktrees)', 2800);
  await step('Every command, test and edit, with evidence labels', await box('#center', { height: 420 }));
  await click('#tabs ::-p-text(Activity)', 2600);
  await step('Token usage per agent, session and model', await box('#center', { height: 400 }));
  await click('#tabs ::-p-text(Usage)', 2600);
  await click('#left ::-p-text(Add checkout API)', 900);
  await p.evaluate(() => document.querySelector('textarea.composer-text')?.scrollIntoView({ block: 'center' }));
  await wait(300);
  const composer = await box('textarea.composer-text');
  await step('Message any agent from the dashboard…', { x: composer.x - 40, y: composer.y - 160, width: composer.width + 80, height: composer.height + 220 });
  const tb = await box('textarea.composer-text');
  await p.mouse.move(tb.x + 80, tb.y + 20, { steps: 20 });
  await p.mouse.down(); await p.mouse.up();
  await p.keyboard.type('Rebase on fix/flaky-tests first so you pick up the rounding fix.', { delay: 30 });
  await wait(300);
  await click('button.btn-primary::-p-text(Send)', 1200);
  const right = await box('#right');
  await step('…and the agent picks it up and answers', { x: right.x, y: right.y + right.height * 0.3, width: right.width, height: right.height * 0.6 });
  await wait(4200);
  await step('Free, open source, and fully local', null);
  await click('#tabs ::-p-text(Hive)', 600);
  await click('label ::-p-text(Auto-orbit)', 4400);

  await cdp.send('Page.stopScreencast'); await pending;
  live.kill();
  const mouse = (await p.evaluate(() => window.__mouse)).map((m) => ({ t: (m.at - t0) / 1000, x: m.x, y: m.y, down: m.down }));
  fs.writeFileSync(path.join(OUT, 'steps.json'), JSON.stringify({ width: W, height: H, duration: (Date.now() - t0) / 1000, steps, frames, mouse }));
  await b.close();
})().catch((e) => { console.error(e); process.exit(1); });
