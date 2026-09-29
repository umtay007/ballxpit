// End-to-end test of the guest page against the networking core running in FakeHost.
//   node e2e.mjs            (expects ../out/fakehost/FakeHost.dll to be built)
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { chromium } from 'playwright-core';

const PORT = 47811;
const root = new URL('..', import.meta.url).pathname;
const executablePath = process.env.CHROMIUM_PATH || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome';

let failures = 0;
function check(ok, what) {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${what}`);
  if (!ok) failures++;
}

// ---- start the fake host and follow its output ----
const host = spawn('dotnet', [`${root}out/fakehost/FakeHost.dll`, String(PORT), `${root}client/index.html`, '120'], { stdio: ['ignore', 'pipe', 'inherit'] });
const lines = [];
let code = '';
host.stdout.setEncoding('utf8');
let partial = '';
host.stdout.on('data', (chunk) => {
  partial += chunk;
  const parts = partial.split('\n');
  partial = parts.pop();
  for (const line of parts) {
    lines.push(line);
    if (line.startsWith('CODE ')) code = line.slice(5).trim();
    if (process.env.VERBOSE) console.log('   host:', line);
  }
});
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
// Waits for a host output line matching pred, looking only at lines from index `from` on.
async function waitFor(pred, ms, what, from = 0) {
  const until = Date.now() + ms;
  while (Date.now() < until) {
    for (let i = from; i < lines.length; i++) if (pred(lines[i])) return lines[i];
    await sleep(50);
  }
  throw new Error('Timed out waiting for ' + what);
}
const since = () => lines.length;
const linesAfter = (i) => lines.slice(i);

await waitFor((l) => l.startsWith('PORT'), 15000, 'fake host start');

const browser = await chromium.launch({ executablePath, args: ['--autoplay-policy=no-user-gesture-required'] });
try {
  // ---- guest 1 joins as P2 ----
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 720 } });
  const page = await ctx.newPage();
  const audioPackets = [];
  page.on('websocket', (ws) => ws.on('framereceived', (f) => {
    if (typeof f.payload !== 'string' && f.payload[0] === 2 && audioPackets.length < 5) audioPackets.push(Buffer.from(f.payload));
  }));
  page.on('pageerror', (e) => check(false, 'page error: ' + e.message));
  await page.goto(`http://127.0.0.1:${PORT}/#${code}`);
  check(await page.locator('#codeRow').isHidden(), 'code from the link is used (no code field)');
  await page.fill('#nameInput', 'Tester');
  await page.click('#playBtn');
  await waitFor((l) => l.includes('Tester joined') && l.includes('as P2'), 5000, 'join as P2');
  check(true, 'host saw Tester join as P2');
  await page.waitForFunction(() => document.querySelector('#role').textContent === 'You are P2', null, { timeout: 5000 });
  check(true, 'page says "You are P2"');

  // ---- video ----
  await page.waitForFunction(() => document.getElementById('waiting').hidden && document.getElementById('screen').width === 960, null, { timeout: 8000 });
  const pixels = await page.evaluate(() => {
    const c = document.getElementById('screen');
    const g = c.getContext('2d');
    const top = g.getImageData(480, 20, 1, 1).data;      // red band at the top of the host screen
    const bottom = g.getImageData(480, 520, 1, 1).data;
    return { w: c.width, h: c.height, top: [...top], bottom: [...bottom] };
  });
  check(pixels.w === 960 && pixels.h === 540, `1920x1080 frame arrives shrunk to 960x540 (${pixels.w}x${pixels.h})`);
  check(pixels.top[0] > 180 && pixels.top[1] < 70, `top of the picture is the host's top (red band) rgb=${pixels.top.slice(0, 3)}`);
  check(pixels.bottom[0] < 200 || pixels.bottom[1] > 150, `bottom is not red rgb=${pixels.bottom.slice(0, 3)}`);
  await sleep(2500);
  const stats = await page.textContent('#stats');
  const fps = parseInt(stats.split('·')[1], 10);
  check(fps >= 15, `stream runs at a playable rate: "${stats}"`);

  // ---- P2 health from the host's status ----
  await page.waitForFunction(() => !document.getElementById('hp').hidden, null, { timeout: 5000 });
  const hpText = await page.textContent('#hp');
  check(/75\/100/.test(hpText), `P2 health shows on the page ("${hpText}")`);
  check(await page.locator('#downed').isHidden(), 'no knocked-out overlay while P2 is up');

  // ---- keyboard movement ----
  let mark = since();
  await page.keyboard.down('d');
  await waitFor((l) => l.startsWith('INPUT') && l.includes('mx=1.00'), 2000, 'move right', mark);
  check(true, 'holding D reaches the host as move right');
  await page.keyboard.down('w');
  await waitFor((l) => l.startsWith('INPUT') && l.includes('mx=0.71 my=0.71'), 2000, 'diagonal', mark);
  check(true, 'D+W is a normalised diagonal');
  await page.keyboard.up('d');
  await page.keyboard.up('w');
  await waitFor((l) => l.startsWith('INPUT') && l.includes('mx=0.00 my=0.00'), 2000, 'stop', mark);
  check(true, 'releasing the keys stops P2');

  // ---- mouse aim and shooting ----
  mark = since();
  const box = await page.locator('#screen').boundingBox();
  await page.mouse.move(box.x + box.width * 0.25, box.y + box.height * 0.75);
  await sleep(200);
  await page.mouse.down();
  const aimLine = await waitFor((l) => l.startsWith('INPUT') && l.includes('am=1') && l.includes('sh=True'), 2000, 'mouse aim + shoot', mark);
  const ax = parseFloat(/ax=([\d.]+)/.exec(aimLine)[1]);
  const ay = parseFloat(/ay=([\d.]+)/.exec(aimLine)[1]);
  check(Math.abs(ax - 0.25) < 0.02 && Math.abs(ay - 0.75) < 0.02, `pointer maps to the host screen (ax=${ax}, ay=${ay})`);
  await page.mouse.up();
  await waitFor((l) => l.startsWith('INPUT') && l.includes('sh=False'), 2000, 'stop shooting', mark);
  check(true, 'releasing the button stops shooting');

  mark = since();
  await page.keyboard.press('e');
  await waitFor((l) => l.startsWith('TOGGLE'), 3000, 'auto-shoot toggle', mark);
  check(true, 'E toggles auto-shoot on the host');

  mark = since();
  await page.keyboard.down('j');
  await waitFor((l) => l.startsWith('INPUT') && l.includes('am=2'), 2000, 'keyboard turning', mark);
  await page.keyboard.up('j');
  check(true, 'J turns the aim (direction mode)');

  // ---- P2's own level-up picks ----
  await page.waitForFunction(() => !document.getElementById('lvl').hidden, null, { timeout: 5000 });
  const cards = await page.$$eval('#lvlCards .lvl-card', (b) => b.map((x) => x.textContent));
  check(cards.length === 3 && /Bleed/.test(cards[0]) && /New ball/.test(cards[0]) && /Lv 2/.test(cards[1]), `three level-up choices show (${cards.join(' | ')})`);
  const kit = await page.textContent('#kit');
  check(/Frost/.test(kit) && /Lv1/.test(kit), `P2's balls show in the corner ("${kit}")`);
  check(/1 more/.test(await page.textContent('#lvlNote')), 'the tray says another pick is waiting');
  mark = since();
  await page.keyboard.press('1');
  await waitFor((l) => l === 'PICK o=1 i=0', 3000, 'pick by key', mark);
  check(true, 'pressing 1 sends the first choice to the host');
  await page.waitForFunction(() => /Bleed/.test(document.getElementById('kit').textContent), null, { timeout: 3000 });
  check(true, "the new ball shows up in P2's list");
  const cards2 = await page.$$eval('#lvlCards .lvl-card', (b) => b.map((x) => x.textContent));
  check(/Bleed/.test(cards2[0]) && /Lv 2/.test(cards2[0]) && (await page.$$('#lvlCards .lvl-card:disabled')).length === 0, `the next pick is offered right away (${cards2.join(' | ')})`);
  mark = since();
  await page.click('#lvlCards .lvl-card >> nth=2');
  await waitFor((l) => l === 'PICK o=2 i=2', 3000, 'pick by click', mark);
  check(true, 'clicking a card picks it');
  await page.waitForFunction(() => /Fuser/.test(document.getElementById('lvlTitle').textContent), null, { timeout: 3000 });
  const kit2 = await page.textContent('#kit');
  check(/Magnet/.test(kit2) && /Bleed/.test(kit2), `the new passive shows up ("${kit2}")`);

  // ---- P2's own fuser ----
  const fuserCards = await page.$$eval('#lvlCards .lvl-card', (b) => b.map((x) => x.textContent));
  check(fuserCards.length === 3 && /Fission/.test(fuserCards[0]) && /upgrade levels/.test(fuserCards[0]) && /Fusion/.test(fuserCards[1]) && /Evolution/.test(fuserCards[2]),
    `a fuser offers Fission, Fusion and Evolution (${fuserCards.join(' | ')})`);
  mark = since();
  await page.keyboard.press('1');
  await waitFor((l) => l === 'PICK o=3 i=0', 3000, 'fuser pick', mark);
  await page.waitForFunction(() => document.getElementById('lvl').hidden, null, { timeout: 3000 });
  check(true, 'picking Fission sends it to the host; the tray closes when no picks are left');
  await page.waitForFunction(() => /Fission/.test(document.getElementById('toast').textContent) && !document.getElementById('toast').hidden, null, { timeout: 3000 });
  check(true, `what Fission gave shows up ("${await page.textContent('#toast')}")`);
  const kit3 = await page.textContent('#kit');
  check(/Frost\s*Lv2/.test(kit3.replace(/\s+/g, ' ')) || /FrostLv2/.test(kit3), `Fission's levels show in P2's list ("${kit3}")`);
  mark = since();
  await page.keyboard.press('2');
  await sleep(300);
  check(!linesAfter(mark).some((l) => l.startsWith('PICK')), 'number keys do nothing without a level-up waiting');

  // ---- audio ----
  await sleep(500);
  check(audioPackets.length > 0, `audio packets arrive (${audioPackets.length} captured)`);
  if (audioPackets.length) {
    const result = await page.evaluate((bytes) => {
      const buf = new Uint8Array(bytes).buffer;
      const p = window.__coopAudio.decode(buf);
      const zc = (a) => { let n = 0; for (let i = 1; i < a.length; i++) if ((a[i - 1] < 0) !== (a[i] < 0)) n++; return n; };
      const peak = (a) => a.reduce((m, v) => Math.max(m, Math.abs(v)), 0);
      return { rate: p.rate, frames: p.frames, zl: zc(p.out[0]), zr: zc(p.out[1]), pl: peak(p.out[0]), pr: peak(p.out[1]) };
    }, [...audioPackets[audioPackets.length - 1]]);
    // 20 ms of 440 Hz ~ 17.6 zero crossings, 660 Hz ~ 26.4.
    check(result.rate === 48000 && result.frames === 960, `audio packet is 20 ms at 48 kHz (${result.frames} @ ${result.rate})`);
    check(Math.abs(result.zl - 17.6) <= 2 && Math.abs(result.zr - 26.4) <= 2, `ADPCM decodes the test tones (zero crossings L=${result.zl}, R=${result.zr})`);
    check(Math.abs(result.pl - 0.3) < 0.05 && Math.abs(result.pr - 0.3) < 0.05, `tone level survives (peaks ${result.pl.toFixed(3)}, ${result.pr.toFixed(3)})`);
  }

  // ---- a second guest watches ----
  const ctx2 = await browser.newContext();
  const page2 = await ctx2.newPage();
  await page2.goto(`http://127.0.0.1:${PORT}/#${code}`);
  await page2.fill('#nameInput', 'Watcher');
  await page2.click('#playBtn');
  await page2.waitForFunction(() => document.querySelector('#role').textContent === 'Watching', null, { timeout: 5000 });
  check(true, 'second guest becomes a spectator while P2 is taken');
  await page2.waitForFunction(() => document.getElementById('waiting').hidden, null, { timeout: 8000 });
  check(true, 'spectator gets the picture too');
  await page2.waitForFunction(() => /Magnet/.test(document.getElementById('kit').textContent), null, { timeout: 3000 });
  check(true, "a guest who joins later sees P2's balls and passives");

  // ---- wrong code ----
  const page3 = await ctx2.newPage();
  await page3.goto(`http://127.0.0.1:${PORT}/#WRONG1`);
  await page3.click('#playBtn');
  await page3.waitForFunction(() => /code is wrong/.test(document.getElementById('joinError').textContent), null, { timeout: 5000 });
  check(true, 'a wrong code is refused with a clear message');
  await page3.close();

  // ---- P2 leaves; the spectator can take over ----
  await page.close();
  await page2.waitForFunction(() => !document.getElementById('claimBtn').hidden, null, { timeout: 5000 });
  check(true, 'spectator is offered "Play as P2" when the slot frees up');
  await page2.click('#claimBtn');
  await page2.waitForFunction(() => document.querySelector('#role').textContent === 'You are P2', null, { timeout: 5000 });
  mark = since();
  await page2.keyboard.down('ArrowLeft');
  await waitFor((l) => l.startsWith('INPUT') && l.includes('mx=-1.00'), 2000, 'new P2 moves', mark);
  await page2.keyboard.up('ArrowLeft');
  check(true, 'the new P2 controls the player');

  // ---- reload keeps the P2 slot (same tab id replaces the old connection) ----
  await page2.reload();
  await page2.click('#playBtn');
  await page2.waitForFunction(() => document.querySelector('#role').textContent === 'You are P2', null, { timeout: 5000 });
  check(true, 'reloading the tab gets P2 back immediately');

  console.log('\nhost summary:');
  for (const l of lines.filter((l) => l.startsWith('VIDEO') || l.startsWith('GUEST')).slice(-4)) console.log('  ' + l);
} catch (e) {
  check(false, e.message);
} finally {
  await browser.close();
  host.kill();
}
console.log(failures ? `\n${failures} check(s) failed` : '\nall checks passed');
process.exit(failures ? 1 : 0);
