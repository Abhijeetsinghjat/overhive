// Pass 2: renders compose.html frame by frame over the pass-1 recording, then encodes
// overhive-demo.mp4 and overhive-demo.webp.  Usage: node compose.js <pass-1 dir> [fps]
// Needs: npm i puppeteer-core ffmpeg-static, and python3 with Pillow (pip install pillow) for the WebP.
const puppeteer = require('puppeteer-core');
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const ffmpeg = require('ffmpeg-static');

const DIR = path.resolve(process.argv[2] || '.');
const FPS = Number(process.argv[3] || 30);
const repo = path.resolve(__dirname, '../..');

(async () => {
  fs.copyFileSync(path.join(__dirname, 'compose.html'), path.join(DIR, 'compose.html'));
  fs.copyFileSync(path.join(repo, 'src/Amc.Daemon/wwwroot/logo.svg'), path.join(DIR, 'logo.svg'));
  const out = path.join(DIR, 'out'); fs.rmSync(out, { recursive: true, force: true }); fs.mkdirSync(out);

  const b = await puppeteer.launch({ executablePath: process.env.CHROME || '/usr/bin/google-chrome', headless: 'new', args: ['--no-sandbox', '--allow-file-access-from-files'] });
  const p = await b.newPage();
  await p.setViewport({ width: 1920, height: 1080 });
  await p.goto('file://' + path.join(DIR, 'compose.html'), { waitUntil: 'networkidle0' });
  await p.evaluate(() => document.fonts.ready);
  const data = JSON.parse(fs.readFileSync(path.join(DIR, 'steps.json'), 'utf8'));
  const total = await p.evaluate((d) => window.setup(d), data);
  const n = Math.round(total * FPS);
  for (let i = 0; i < n; i++) {
    await p.evaluate((T) => window.render(T), i / FPS);
    await p.screenshot({ path: path.join(out, `${String(i).padStart(5, '0')}.jpg`), type: 'jpeg', quality: 94 });
    if (i % 150 === 0) process.stdout.write(`frame ${i}/${n}\n`);
  }
  await b.close();

  const ff = (...a) => execFileSync(ffmpeg, ['-loglevel', 'error', '-y', ...a], { stdio: 'inherit', cwd: DIR });
  const frames = ['-framerate', String(FPS), '-i', path.join(out, '%05d.jpg')];
  // MP4: two-pass to just under 9.5 MB, the most quality that still fits GitHub's 10 MB video upload limit.
  const kbps = Math.floor((9.5 * 8 * 1000) / total * 0.97);
  const x264 = ['-c:v', 'libx264', '-preset', 'veryslow', '-b:v', `${kbps}k`, '-pix_fmt', 'yuv420p'];
  ff(...frames, ...x264, '-pass', '1', '-an', '-f', 'mp4', '/dev/null');
  ff(...frames, ...x264, '-pass', '2', '-movflags', '+faststart', path.join(DIR, 'overhive-demo.mp4'));
  // Animated WebP for the README (GitHub never autoplays video, but animated images play on their own),
  // encoded from the frames, not the MP4, at 1600 px so it stays sharp on high-density screens; kept under
  // 10 MB, above which GitHub serves files as a generic download instead of an image. webp.py (Pillow) is used
  // when available: ffmpeg's encoder leaves blocky smears in the dark 3D Hive areas (see webp.py).
  const webp = path.join(DIR, 'overhive-demo.webp');
  try {
    execFileSync('python3', ['-I', path.join(__dirname, 'webp.py'), out, webp, '10', '1600', '48'], { stdio: 'inherit' });
  } catch {
    console.warn('webp.py failed (pip install pillow); falling back to ffmpeg, which can smear dark 3D areas');
    ff(...frames, '-vf', 'fps=10,scale=1600:-1:flags=lanczos', '-c:v', 'libwebp_anim', '-quality', '56',
       '-compression_level', '3', '-loop', '0', webp);
  }
  const mb = fs.statSync(webp).size / 1e6;
  if (mb >= 10) console.warn(`overhive-demo.webp is ${mb.toFixed(1)} MB: over GitHub's 10 MB image limit, lower the quality or fps`);
  console.log('done', n, 'frames');
})().catch((e) => { console.error(e); process.exit(1); });
