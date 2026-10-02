// Issue #257 (Phase 67): REAL-BROWSER gate for full Slang input in the browser.
//
// The browser cannot spawn slangc, so ShadowDusk.Slang.Wasm runs the SAME pinned slangc
// (v2026.14.1) compiled to WebAssembly inside the page and hands its HLSL to the unchanged
// in-browser pipeline (ShadowDusk.Wasm: DXC -> SPIR-V -> SPIRV-Cross -> GLSL, vkd3d -> DXBC).
// Headless Chromium boots the published ShaderFiddle.Web sample (the real .NET-browser
// runtime, real [JSImport] interop, real HTTP fetch of every module) and checks two things:
//
//  1. BYTE IDENTITY. Every OpenGL/* and DirectX_Vkd3d/* entry of the committed
//     tests/fixtures/golden/byte-identity/slang-manifest.json is compiled in the page
//     (TestCompileSlang) and its SHA-256 must equal the manifest's. The manifest is what
//     SlangCrossHostByteIdentityTests asserts on Windows, Linux and macOS, and its OpenGL and
//     DirectX bytes are the ones validation/SlangFullCorpus loads into real MonoGame, so the
//     browser becomes one more host proven equal to the same bytes.
//
//  2. RENDER. Every tests/fixtures/shaders/slang shader is compiled in the page for OpenGL
//     two independent ways, through real slangc ('slangc') and through ShadowDusk.Compiler's
//     own HLSL-compatible-subset frontend ('subset', a pure text transform), loaded into the
//     live KNI WebGL Effect, drawn, and read back. Both must load, and the two canvases must
//     agree within tolerance; the uniform-free procedural ones must also draw a non-trivial
//     image (so two blank frames cannot agree their way to a pass). The full-Slang sample
//     (interface + generics, slangc only) must load and draw too.
//
// Usage:  cd tests/ShadowDusk.BrowserTests
//         node browser-slang-gate.mjs [--skip-publish] [--require-module]
// Requires: .NET SDK + wasm-tools workload, Node 18+, Playwright Chromium, the restored
// ShadowDusk.Wasm modules (tools/restore.*), and the slangc module built into
// src/ShadowDusk.Slang.Wasm/wwwroot/slangc/ (.wasm-build/slang-wasm/build-slangc-wasm.ps1).
// Without the slangc module the gate SKIPS loudly (exit 0) locally and FAILS with
// --require-module (what CI passes, after building it).

import { chromium } from 'playwright';
import { startServer } from './static-server.mjs';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');

const moduleDir = path.join(repoRoot, 'src', 'ShadowDusk.Slang.Wasm', 'wwwroot', 'slangc');
const manifestPath = path.join(repoRoot, 'tests', 'fixtures', 'golden', 'byte-identity', 'slang-manifest.json');
const shippedDir = path.join(repoRoot, 'tests', 'fixtures', 'shaders', 'slang');
const probeDir = path.join(repoRoot, 'plan', 'PHASE-65-appendix', 'slang-probe', 'shaders');
const exampleSlang = path.join(repoRoot, 'samples', 'ShaderFiddle.Web', 'wwwroot', 'shaders', 'slang', 'generic-blend.slang');
const publishOut = path.join(__dirname, '.publish-slang');
const publishRoot = path.join(publishOut, 'wwwroot');
const resultsFile = path.join(__dirname, 'RESULTS-SLANG-BROWSER.md');

const SKIP_PUBLISH = process.argv.includes('--skip-publish');
const REQUIRE_MODULE = process.argv.includes('--require-module');
const SIZE = 256;
// Same budget run-harness.mjs gives WebGL-vs-WebGL precision drift.
const TOLERANCE = 2;
// Manifest target key -> the PlatformTarget name TestCompileSlang parses.
const TARGETS = { OpenGL: 'OpenGL', DirectX_Vkd3d: 'DirectX' };

function fail(reason) {
  console.error(`[slang browser gate] FAIL: ${reason}`);
  process.exit(1);
}

const moduleFiles = ['shadowdusk-slangc.js', 'shadowdusk-slangc.wasm'].map((f) => path.join(moduleDir, f));
if (!moduleFiles.every(existsSync)) {
  const reason = 'src/ShadowDusk.Slang.Wasm/wwwroot/slangc/shadowdusk-slangc.{js,wasm} is not built. ' +
    'Run pwsh .wasm-build/slang-wasm/build-slangc-wasm.ps1 first.';
  if (REQUIRE_MODULE) fail(`--require-module: ${reason}`);
  console.log(`\n${'='.repeat(78)}\n[slang browser gate] SKIPPED, NOT RUN, NOT A PASS.\n[slang browser gate] ${reason}\n${'='.repeat(78)}\n`);
  if (process.env.GITHUB_ACTIONS === 'true') console.log(`::warning::Slang browser gate SKIPPED (not a pass): ${reason}`);
  process.exit(0);
}

// ---------------------------------------------------------------------------------- corpus
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
function sourcePathFor(key) {
  if (key.startsWith('slang/')) return path.join(shippedDir, key.slice('slang/'.length));
  if (key.startsWith('phase65-probe/')) return path.join(probeDir, key.slice('phase65-probe/'.length));
  fail(`unrecognized manifest corpus key '${key}'`);
}
const byteEntries = Object.entries(manifest)
  .filter(([key]) => Object.keys(TARGETS).some((t) => key.startsWith(t + '/')))
  .map(([key, expected]) => {
    const slash = key.indexOf('/');
    const targetKey = key.slice(0, slash);
    const corpusKey = key.slice(slash + 1);
    return { key, target: TARGETS[targetKey], file: sourcePathFor(corpusKey), expected };
  });
if (byteEntries.length !== 42) fail(`expected 21 shaders x 2 targets = 42 manifest entries, found ${byteEntries.length}`);
const renderNames = readdirSync(shippedDir).filter((f) => f.endsWith('.slang')).sort();
if (renderNames.length < 10) fail(`only ${renderNames.length} shaders under ${shippedDir}`);

const readSource = (p) => readFileSync(p, 'utf8').replace(/\r\n/g, '\n');
const isProcedural = (text) => {
  const entries = text.match(/\[shader\(\s*"(vertex|fragment)"\s*\)\]/g) ?? [];
  return entries.length === 1 && entries[0].includes('fragment') && !text.includes('cbuffer') && !text.includes('Texture2D');
};

// --------------------------------------------------------------------------------- publish
if (SKIP_PUBLISH && existsSync(path.join(publishRoot, 'index.html'))) {
  console.log(`[slang browser gate] --skip-publish: reusing ${publishRoot}`);
} else {
  const csproj = path.join('samples', 'ShaderFiddle.Web', 'ShaderFiddle.Web.csproj');
  console.log(`\n$ dotnet publish -c Release ${csproj} -o ${publishOut}`);
  const r = spawnSync('dotnet', ['publish', '-c', 'Release', csproj, '-o', publishOut], { stdio: 'inherit', cwd: repoRoot });
  if (r.status !== 0) fail(`dotnet publish exited ${r.status}.`);
}
const servedWasm = path.join(publishRoot, '_content', 'ShadowDusk.Slang.Wasm', 'slangc', 'shadowdusk-slangc.wasm');
if (!existsSync(servedWasm)) {
  fail(`the slangc module is built but MISSING from the publish output (${servedWasm}): the static-web-asset flow regressed.`);
}

// --------------------------------------------------------------------------------- browser
const srv = await startServer(publishRoot);
console.log(`[slang browser gate] serving ${srv.url}`);
const browser = await chromium.launch({
  headless: true,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--ignore-gpu-blocklist', '--enable-unsafe-swiftshader'],
});
const failures = [];
const rows = { bytes: [], render: [] };
const fetched = [];
try {
  const page = await browser.newPage({ viewport: { width: 900, height: 700 } });
  page.setDefaultTimeout(300000);
  page.on('response', (resp) => { if (/shadowdusk-slangc\.(js|wasm)$/.test(resp.url())) fetched.push(`${resp.status()} ${resp.url()}`); });
  await page.goto(`${srv.url}/?test=${SIZE}`, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => typeof window.theInstance !== 'undefined' && window.theInstance !== null, { timeout: 180000 });

  // 1. Byte identity against the committed manifest.
  console.log(`[slang browser gate] byte identity: ${byteEntries.length} artifacts (21 shaders x OpenGL + DirectX_Vkd3d)`);
  for (const e of byteEntries) {
    const src = readSource(e.file);
    const t0 = Date.now();
    const out = await page.evaluate(
      async ({ s, t, n }) => await window.theInstance.invokeMethodAsync('TestCompileSlang', s, t, n),
      { s: src, t: e.target, n: path.basename(e.file) });
    const ms = Date.now() - t0;
    if (!out.startsWith('OK:')) {
      failures.push(`${e.key}: in-browser compile failed: ${out}`);
      rows.bytes.push({ key: e.key, verdict: 'FAIL', note: out.slice(0, 160), ms });
      console.log(`  [FAIL] ${e.key}: ${out.slice(0, 200)}`);
      continue;
    }
    const sha = createHash('sha256').update(Buffer.from(out.slice(3), 'base64')).digest('hex');
    const ok = sha === e.expected;
    if (!ok) failures.push(`${e.key}: browser=${sha} manifest=${e.expected}`);
    rows.bytes.push({ key: e.key, verdict: ok ? 'PASS' : 'FAIL', note: ok ? '' : `browser ${sha}`, ms });
    console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${e.key} (${ms} ms)`);
  }

  // 2. Render: slangc route vs subset route, real KNI WebGL Effect, read back.
  await page.waitForFunction(() => typeof window.__sd_readback === 'function', { timeout: 60000 });
  const readback = async () => {
    const rb = await page.evaluate(() => window.__sd_readback());
    return rb ? Buffer.from(rb.data, 'base64') : null;
  };
  const applyAndRead = async (src, route) => {
    // The game boots on the first animation frames; retry "game not ready" briefly.
    for (let i = 0; i < 60; i++) {
      const err = await page.evaluate(
        async ({ s, r }) => await window.theInstance.invokeMethodAsync('TestCompileSlangAndApply', s, r), { s: src, r: route });
      if (err === 'game not ready') { await page.waitForTimeout(500); continue; }
      if (err !== null) return { err };
      await page.waitForTimeout(600);
      return { img: await readback() };
    }
    return { err: 'game never became ready' };
  };
  const distinctColors = (img) => {
    const set = new Set();
    for (let i = 0; i < img.length && set.size < 64; i += 4) set.add(img.readUInt32LE(i));
    return set.size;
  };

  const cases = renderNames.map((n) => ({ name: n, src: readSource(path.join(shippedDir, n)), compare: true }));
  cases.push({ name: 'generic-blend.slang (sample; slangc only)', src: readSource(exampleSlang), compare: false });
  console.log(`[slang browser gate] render: ${cases.length} shaders in KNI WebGL`);
  for (const c of cases) {
    const row = { name: c.name, verdict: 'FAIL', note: '' };
    const a = await applyAndRead(c.src, 'slangc');
    if (a.err || !a.img) {
      row.note = `slangc route: ${a.err ?? 'readback returned null'}`;
    } else if (!c.compare) {
      const colors = distinctColors(a.img);
      row.verdict = colors >= 2 ? 'PASS' : 'FAIL';
      row.note = `loaded and drew; ${colors}${colors >= 64 ? '+' : ''} distinct colours`;
    } else {
      const b = await applyAndRead(c.src, 'subset');
      if (b.err || !b.img) {
        row.note = `subset route: ${b.err ?? 'readback returned null'}`;
      } else {
        let maxd = 0;
        for (let i = 0; i < a.img.length; i++) maxd = Math.max(maxd, Math.abs(a.img[i] - b.img[i]));
        const colors = distinctColors(a.img);
        const needsImage = isProcedural(c.src);
        const ok = maxd <= TOLERANCE && (!needsImage || colors >= 2);
        row.verdict = ok ? (maxd === 0 ? 'PASS(exact)' : 'PASS(tol)') : 'FAIL';
        row.note = `maxd ${maxd} vs subset route; ${colors}${colors >= 64 ? '+' : ''} distinct colours${needsImage ? ' (procedural)' : ''}`;
      }
    }
    if (!row.verdict.startsWith('PASS')) failures.push(`render ${c.name}: ${row.note}`);
    rows.render.push(row);
    console.log(`  [${row.verdict}] ${c.name}: ${row.note}`);
  }
} finally {
  await browser.close();
  await srv.close();
}

if (!fetched.some((f) => f.endsWith('.wasm') && f.startsWith('200'))) {
  failures.push('the slangc .wasm was never fetched over HTTP: the in-browser route did not run');
}

const lines = [
  '# Slang in the browser: byte identity + render (issue #257)',
  '',
  `_Generated by \`browser-slang-gate.mjs\`, headless Chromium (ANGLE/SwiftShader), KNI WebGL ${SIZE}x${SIZE}. ` +
    'slangc v2026.14.1 compiled to WebAssembly, run in the page; its HLSL goes through the faithful DXC/SPIRV-Cross/vkd3d WASM modules._',
  '',
  `**Verdict: ${failures.length === 0 ? 'GREEN' : 'RED'}**`,
  '',
  '## Byte identity vs `slang-manifest.json`',
  '',
  '| Manifest key | Verdict | ms | Note |',
  '|---|---|---|---|',
  ...rows.bytes.map((r) => `| \`${r.key}\` | ${r.verdict} | ${r.ms} | ${r.note} |`),
  '',
  '## Render: slangc route vs subset route (KNI WebGL)',
  '',
  '| Shader | Verdict | Note |',
  '|---|---|---|',
  ...rows.render.map((r) => `| \`${r.name}\` | ${r.verdict} | ${r.note} |`),
  '',
  '## Module fetches',
  '',
  ...fetched.map((f) => `- ${f}`),
  '',
];
await fs.writeFile(resultsFile, lines.join('\n'));

if (failures.length > 0) {
  console.error(`\n[slang browser gate] RED, ${failures.length} failure(s):`);
  for (const f of failures) console.error(`  - ${f}`);
  process.exit(1);
}
console.log(`\n[slang browser gate] GREEN: ${rows.bytes.length}/${rows.bytes.length} artifacts byte-identical to the manifest, ${rows.render.length}/${rows.render.length} renders. (${resultsFile})`);
