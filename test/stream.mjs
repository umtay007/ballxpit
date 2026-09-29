// Streams from FakeHost to headless Chromium through a simulated internet link and reports what
// the guest gets. Prints one line per scenario.
//   node stream.mjs "rtt=60 down=20" "rtt=60 down=3" ...
import { spawn } from 'node:child_process';
import { chromium } from 'playwright-core';
import { startLink } from './netsim.mjs';

const root = new URL('..', import.meta.url).pathname;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const scenarios = process.argv.slice(2).length ? process.argv.slice(2) : ['rtt=60 down=20', 'rtt=60 down=3'];
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' });
let port = 47900;
const results = [];
for (const spec of scenarios) {
  const opts = Object.fromEntries(spec.split(' ').map((kv) => kv.split('=')).map(([k, v]) => [k, Number(v)]));
  const hostPort = port++, linkPort = port++;
  const host = spawn('dotnet', [`${root}out/fakehost/FakeHost.dll`, String(hostPort), `${root}client/index.html`, '60'],
    { stdio: ['ignore', 'pipe', 'inherit'], env: { ...process.env, FAKEHOST_SIZE: process.env.FAKEHOST_SIZE || '2560x1080', FAKEHOST_DETAIL: '1' } });
  const lines = [];
  let code = '';
  host.stdout.setEncoding('utf8');
  host.stdout.on('data', (d) => { for (const l of d.split('\n')) if (l) { lines.push(l); if (l.startsWith('CODE ')) code = l.slice(5).trim(); } });
  while (!code) await sleep(50);
  const link = startLink({ listenPort: linkPort, targetPort: hostPort, rttMs: opts.rtt, downMbit: opts.down });
  const page = await browser.newPage();
  await page.goto(`http://127.0.0.1:${linkPort}/#${code}`);
  await page.click('#playBtn');
  await sleep(opts.secs ? opts.secs * 1000 : 16000);
  const stats = [];
  for (let i = 0; i < 4; i++) { stats.push(await page.textContent('#stats')); await sleep(1000); }
  const fps = stats.map((s) => parseInt(s.split('·')[1], 10));
  const ping = stats.map((s) => parseInt(s, 10));
  const video = lines.filter((l) => l.startsWith('VIDEO')).pop() || '';
  const q = /quality=(\d+)/.exec(video)?.[1];
  const size = /size=(\S+)/.exec(video)?.[1];
  const win = /window=(\d+)/.exec(lines.filter((l) => l.startsWith("GUEST")).pop() || "")?.[1];
  const kb = Math.round(Number(/bytes=(\d+)/.exec(video)?.[1] || 0) / 1024);
  const line = `${spec.padEnd(18)} -> ${Math.round(fps.reduce((a, b) => a + b) / fps.length)} fps, ping ${Math.max(...ping)} ms, quality ${q}, ${size}, ${kb} KB/frame, window ${win}`;
  console.log(line);
  results.push(line);
  await page.close();
  link.close();
  host.kill();
}
await browser.close();
