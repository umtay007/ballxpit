// Slow-connection check: a guest on ~3 Mbit/s with 100 ms latency. The host must lower quality and
// frame rate on its own, and the guest's lag (ping over the same socket as the video) must stay bounded.
//   node throttle.mjs
import { spawn } from 'node:child_process';
import { chromium } from 'playwright-core';

const PORT = 47831;
const root = new URL('..', import.meta.url).pathname;
const host = spawn('dotnet', [`${root}out/fakehost/FakeHost.dll`, String(PORT), `${root}client/index.html`, '90'], { stdio: ['ignore', 'pipe', 'inherit'] });
const lines = [];
let code = '';
host.stdout.setEncoding('utf8');
host.stdout.on('data', (d) => { for (const l of d.split('\n')) { if (!l) continue; lines.push(l); if (l.startsWith('CODE ')) code = l.slice(5).trim(); } });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
while (!code) await sleep(100);

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' });
let ok = true;
try {
  const page = await browser.newPage();
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Network.enable');
  await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: 100, downloadThroughput: 3e6 / 8, uploadThroughput: 1e6 / 8 });
  await page.goto(`http://127.0.0.1:${PORT}/#${code}`);
  await page.click('#playBtn');
  const samples = [];
  for (let i = 0; i < 20; i++) {
    await sleep(1000);
    samples.push(await page.textContent('#stats'));
  }
  const pings = samples.map((s) => parseInt(s, 10)).filter((n) => !isNaN(n));
  const fps = samples.map((s) => parseInt(s.split('·')[1], 10)).filter((n) => !isNaN(n));
  const video = lines.filter((l) => l.startsWith('VIDEO')).slice(-3);
  console.log('guest stats (last 5 s):', samples.slice(-5).join(' | '));
  console.log('host:', video.join(' | '));
  const lastPing = Math.max(...pings.slice(-5));
  const q = Math.min(...video.map((l) => parseInt(/quality=(\d+)/.exec(l)?.[1] ?? '99', 10)));
  const check = (c, m) => { console.log(`${c ? 'PASS' : 'FAIL'}  ${m}`); if (!c) ok = false; };
  check(lastPing < 1200, `lag stays bounded on a slow link (ping ${lastPing} ms)`);
  check(Math.min(...fps.slice(-5)) >= 3, `picture keeps moving (${fps.slice(-5).join(', ')} fps)`);
  check(q < 60, `host lowered JPEG quality by itself (now ${q})`);
} finally {
  await browser.close();
  host.kill();
}
process.exit(ok ? 0 : 1);
