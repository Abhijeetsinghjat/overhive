// Pass 1: records a scripted tour of the demo dashboard as timestamped 2x screenshots (frames/), so the
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
  const b = await puppeteer.launch({ executablePath: process.env.CHROME || '/usr/bin/google-chrome', headless: 'new', args: ['--no-sandbox', '--hide-scrollbars'] });
  const p = await b.newPage();
  await p.setViewport({ width: W, height: H, deviceScaleFactor: 2 });
  await p.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'dark' }]);
  await p.goto(URL, { waitUntil: 'networkidle2' });
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
  let capturing = true;
  t0 = Date.now();
  const capture = (async () => {
    while (capturing) {
      const at = (Date.now() - t0) / 1000;
      const { data } = await cdp.send('Page.captureScreenshot', { format: 'jpeg', quality: 90, optimizeForSpeed: true });
      const file = `frames/${String(frames.length).padStart(5, '0')}.jpg`;
      frames.push({ t: (at + (Date.now() - t0) / 1000) / 2, file });
      fs.writeFileSync(path.join(OUT, file), Buffer.from(data, 'base64'));
    }
  })();
  const live = agents('live'); agents('inbox');

  await step('Every agent and sub-agent, from any AI tool, live', null);
  await wait(2600);
  await step('Conflicts are flagged before anyone merges', await box('#conflict-banner', { width: 1000 }));
  await wait(2600);
  await step('Sub-agents roll up to their parent', await box('#left', { height: 260 }));
  await click('#left ::-p-text(Write checkout tests)', 2600);
  await step('Which agent changed which file, in which worktree', await box('#center'));
  await click('#tabs ::-p-text(Worktrees)', 3000);
  await step('Every command, test and edit, with evidence labels', await box('#center', { height: 420 }));
  await click('#tabs ::-p-text(Activity)', 2800);
  await step('Commits link straight to your Git host', await box('#center', { height: 260 }));
  await click('#tabs ::-p-text(Commits)', 2600);
  await step('Token usage per agent, session and model', await box('#center', { height: 400 }));
  await click('#tabs ::-p-text(Usage)', 2800);
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
  await click('#tabs ::-p-text(Graph)', 2800);

  capturing = false; await capture;
  live.kill();
  const mouse = (await p.evaluate(() => window.__mouse)).map((m) => ({ t: (m.at - t0) / 1000, x: m.x, y: m.y, down: m.down }));
  fs.writeFileSync(path.join(OUT, 'steps.json'), JSON.stringify({ width: W, height: H, duration: (Date.now() - t0) / 1000, steps, frames, mouse }));
  await b.close();
})().catch((e) => { console.error(e); process.exit(1); });
