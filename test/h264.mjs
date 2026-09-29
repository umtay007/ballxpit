// Checks the H.264 stream in a real Chrome (the Chromium that Playwright ships can't decode H.264).
//   CHROME_PATH=/path/to/google-chrome node h264.mjs      (expects ../out/fakehost/FakeHost.dll)
// FakeHost downloads Cisco's OpenH264 next to itself the first time.
import { spawn } from 'node:child_process';
import { chromium } from 'playwright-core';

const executablePath = process.env.CHROME_PATH;
if (!executablePath) {
  console.log('SKIP  set CHROME_PATH to a Google Chrome to test H.264');
  process.exit(0);
}
const PORT = 47831;
const root = new URL('..', import.meta.url).pathname;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let failures = 0;
const check = (ok, what) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${what}`); if (!ok) failures++; };

const host = spawn('dotnet', [`${root}out/fakehost/FakeHost.dll`, String(PORT), `${root}client/index.html`, '90'], { stdio: ['ignore', 'pipe', 'inherit'] });
const lines = [];
let code = '';
host.stdout.setEncoding('utf8');
host.stdout.on('data', (d) => { for (const l of d.split('\n')) if (l) { lines.push(l); if (l.startsWith('CODE ')) code = l.slice(5).trim(); if (process.env.VERBOSE) console.log('   host:', l); } });
while (!code) await sleep(50);
const waitLine = async (pred, ms, what) => {
  const until = Date.now() + ms;
  while (Date.now() < until) { const l = lines.find(pred); if (l) return l; await sleep(100); }
  throw new Error('timed out waiting for ' + what);
};

const browser = await chromium.launch({ executablePath });
try {
  await waitLine((l) => l.includes('Loaded the H.264 encoder'), 30000, 'OpenH264 to load');
  check(true, 'the host loads Cisco OpenH264 (downloaded and checksum-verified on first use)');

  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  page.on('pageerror', (e) => check(false, 'page error: ' + e.message));
  const sent = [];
  page.on('websocket', (ws) => ws.on('framesent', (f) => { if (typeof f.payload === 'string') sent.push(f.payload); }));
  await page.goto(`http://127.0.0.1:${PORT}/#${code}`);
  await page.click('#playBtn');
  await waitLine((l) => l.includes('joined') && l.includes('browser decodes H.264'), 8000, 'H.264 join');
  check(true, 'Chrome announces H.264 support and the host picks it');
  await page.waitForFunction(() => document.getElementById('waiting').hidden && document.getElementById('screen').width === 960, null, { timeout: 10000 });
  await sleep(3000);
  const stats = await page.textContent('#stats');
  const fps = parseInt(stats.split('·')[1], 10);
  check(/H\.264/.test(stats) && fps >= 20, `frames are decoded with WebCodecs at a playable rate ("${stats}")`);
  const px = await page.evaluate(() => {
    const g = document.getElementById('screen').getContext('2d');
    return { top: [...g.getImageData(480, 20, 1, 1).data], bottom: [...g.getImageData(480, 520, 1, 1).data] };
  });
  check(px.top[0] > 170 && px.top[1] < 80, `picture is the right way up (top rgb=${px.top.slice(0, 3)}, bottom rgb=${px.bottom.slice(0, 3)})`);
  const video = lines.filter((l) => l.startsWith('VIDEO')).pop() || '';
  check(/codec=H264/.test(video), `the host sends H.264 (${video.replace(/^VIDEO /, '')})`);

  // A reload starts a new decoder; the host must send it a keyframe.
  await page.reload();
  await page.click('#playBtn');
  await page.waitForFunction(() => document.getElementById('waiting').hidden, null, { timeout: 10000 });
  await sleep(1500);
  const stats2 = await page.textContent('#stats');
  check(/H\.264/.test(stats2) && parseInt(stats2.split('·')[1], 10) >= 15, `after a reload the picture comes back with a fresh keyframe ("${stats2}")`);
  await page.close();

  // A browser whose decoder keeps failing asks for JPEG pictures and still gets a picture.
  const broken = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const brokenSent = [];
  broken.on('websocket', (ws) => ws.on('framesent', (f) => { if (typeof f.payload === 'string') brokenSent.push(f.payload); }));
  await broken.addInitScript(() => {
    const Real = window.VideoDecoder;
    window.VideoDecoder = class extends Real {
      constructor(init) { super({ output: (f) => f.close(), error: init.error }); this._err = init.error; }
      decode() { setTimeout(() => this._err(new DOMException('simulated failure')), 0); }
    };
  });
  await broken.goto(`http://127.0.0.1:${PORT}/#${code}`);
  await broken.click('#watchBtn');
  await broken.waitForFunction(() => /JPEG/.test(document.getElementById('stats').textContent) && document.getElementById('waiting').hidden, null, { timeout: 15000 });
  check(brokenSent.some((m) => m.includes('"noh264"')), 'after repeated decoder errors the page asks for JPEG pictures and shows them');
  await broken.close();
} catch (e) {
  check(false, e.message);
} finally {
  await browser.close();
  host.kill();
}
console.log(failures ? `\n${failures} check(s) failed` : '\nall checks passed');
process.exit(failures ? 1 : 0);
