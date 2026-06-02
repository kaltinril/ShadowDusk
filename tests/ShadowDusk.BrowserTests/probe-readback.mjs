// Phase 24 DE-RISK PROBE (do this first): prove the headless browser can
// produce a NON-BLACK pixel readback of one shader before building the full
// harness. Launches headless Chromium with deterministic software GL, loads the
// patched sample at ?test=512, drives a deterministic mode-1 load via the
// [JSInvokable] TestLoadCorpus, then reads the WebGL drawing buffer via
// window.__sd_readback (preserveDrawingBuffer forced on by index.html).
import { chromium } from 'playwright';
import { startServer } from './static-server.mjs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SIZE = 512;
const SHADER = process.argv[2] || 'Grayscale';

const srv = await startServer(path.join(__dirname, '.publish', 'wwwroot'));
console.log(`[probe] serving ${srv.url}`);

const browser = await chromium.launch({
  headless: true,
  args: [
    '--use-gl=angle',
    '--use-angle=swiftshader',
    '--ignore-gpu-blocklist',
    '--enable-unsafe-swiftshader',
  ],
});

let exitCode = 1;
try {
  const page = await browser.newPage({ viewport: { width: 900, height: 700 } });
  page.on('console', (m) => console.log(`  [page:${m.type()}] ${m.text()}`));
  page.on('pageerror', (e) => console.log(`  [pageerror] ${e.message}`));

  await page.goto(`${srv.url}/?test=${SIZE}`, { waitUntil: 'domcontentloaded' });

  // Wait until the KNI game has booted (TestLoadCorpus returns non-"game not ready").
  console.log('[probe] waiting for KNI game to boot…');
  await page.waitForFunction(
    () => typeof window.theInstance !== 'undefined' && window.theInstance !== null,
    { timeout: 120000 }
  );
  // Give the device a few frames to finish LoadContent.
  await page.waitForTimeout(1500);

  console.log(`[probe] loading shader "${SHADER}" via TestLoadCorpus…`);
  const loadErr = await page.evaluate(
    async (name) => await window.theInstance.invokeMethodAsync('TestLoadCorpus', name),
    SHADER
  );
  console.log(`[probe] TestLoadCorpus("${SHADER}") -> ${loadErr === null ? 'OK (null)' : JSON.stringify(loadErr)}`);

  // Let it render several frames.
  await page.waitForTimeout(800);

  const rb = await page.evaluate(() => window.__sd_readback());
  if (!rb) {
    console.log('[probe] FAIL: __sd_readback returned null (no GL context captured).');
  } else {
    const buf = Buffer.from(rb.data, 'base64');
    let nonBlack = 0;
    for (let i = 0; i < buf.length; i += 4) {
      if (buf[i] > 5 || buf[i + 1] > 5 || buf[i + 2] > 5) nonBlack++;
    }
    const total = buf.length / 4;
    console.log(`[probe] readback ${rb.w}x${rb.h}, nonBlack=${nonBlack}/${total} (${(100 * nonBlack / total).toFixed(1)}%)`);
    if (nonBlack > total * 0.05) {
      console.log('[probe] SUCCESS: captured a non-black render. Canvas capture is viable.');
      exitCode = 0;
    } else {
      console.log('[probe] FAIL: readback is essentially black.');
    }
  }
} finally {
  await browser.close();
  await srv.close();
}
process.exit(exitCode);
