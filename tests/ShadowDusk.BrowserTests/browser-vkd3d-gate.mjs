// Phase 4.1 G2 — REAL-BROWSER byte-identity gate for the DirectX / FNA EXPORT targets.
//
// A browser cannot RENDER DXBC or D3D9 bytecode (there is no Direct3D in a browser),
// so the honest G2 analogue for the DX/FNA export targets is byte-identity in a real
// browser: headless Chromium boots the published KNI/Blazor sample (the actual
// .NET-browser runtime), and for every DirectX_Vkd3d/* and FNA/* entry of the
// committed cross-host byte-identity manifest
// (tests/fixtures/golden/byte-identity/manifest.json) the page compiles the fixture
// through the REAL product path — WasmShaderCompiler.CompileAsync via the
// [JSInvokable] TestCompileExport hook (real [JSImport] interop, real HTTP fetch of
// vkd3d-shader.{js,wasm} from the served static web assets) — and this harness
// asserts the artifact's SHA-256 equals the manifest hash.
//
// WHY THE MANIFEST (and not a fresh Vkd3dCorpusProbe capture): the manifest is the
// FULL-ARTIFACT hash (.mgfx / .fxb — it exercises the managed writers on the browser
// runtime too, strictly stronger than the per-stage vkd3d seam the node G1 gate
// already proved 98/98), it is the exact device Phase 37 uses to transfer the
// Windows rung-4 render proofs to Linux/macOS byte-for-byte (CI asserts it on all
// three desktop OSes via CrossHostByteIdentityTests), and it needs no desktop vkd3d
// native in this job. Matching it makes the browser simply the FOURTH host proven
// equal to the same render-proven bytes:
//
//   browser bytes == manifest bytes == desktop bytes == the bytes that loaded and
//   rendered equivalently in real MonoGame WindowsDX (Phase 18) and real FNA
//   (Phases 39/40, gate 17/17) — render-equivalence closes by transitivity.
//
// SKIP-WITH-NOTICE (never a fabricated pass): vkd3d-shader.{js,wasm} not restored
// -> loud SKIP, exit 0 (run tools/restore.* first) — LOCAL runs only. With
// --require-module (what wasm.yml passes) the missing-module branch FAILS instead:
// the pins are hosted, so a module missing in CI is an infrastructure failure that
// must go red, never a green self-skip. Everything else that goes wrong (publish
// missing the module, compile failure, hash mismatch, the .wasm never being fetched)
// -> FAIL, exit 1.
//
// Usage:  cd tests/ShadowDusk.BrowserTests
//         node browser-vkd3d-gate.mjs [--skip-publish] [--require-module]
//         (or: npm run vkd3d-browser-gate)
// Requires: .NET SDK + wasm-tools workload (for the publish), Node 18+, Playwright
// Chromium (npx playwright install chromium). No GPU / xvfb needed — pure compile.

import { chromium } from 'playwright';
import { startServer } from './static-server.mjs';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');

const moduleJs   = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'vkd3d', 'vkd3d-shader.js');
const moduleWasm = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'vkd3d', 'vkd3d-shader.wasm');
const manifestPath = path.join(repoRoot, 'tests', 'fixtures', 'golden', 'byte-identity', 'manifest.json');
const fixturesDir  = path.join(repoRoot, 'tests', 'fixtures', 'shaders');
const publishOut   = path.join(__dirname, '.publish-vkd3d');
const publishRoot  = path.join(publishOut, 'wwwroot');
const resultsFile  = path.join(__dirname, 'RESULTS-VKD3D-BROWSER.md');

const SKIP_PUBLISH = process.argv.includes('--skip-publish');
// CI mode: the hosted pins exist (native-vkd3d-wasm-1.17), so a missing module is an
// infrastructure failure — fail red instead of the local-convenience loud skip.
const REQUIRE_MODULE = process.argv.includes('--require-module');

// Manifest target key -> the PlatformTarget name TestCompileExport parses.
const TARGETS = { DirectX_Vkd3d: 'DirectX', FNA: 'Fna' };

function skip(reason) {
  console.log('');
  console.log('='.repeat(78));
  console.log('[vkd3d browser gate] SKIPPED — NOT RUN, NOT A PASS.');
  console.log(`[vkd3d browser gate] ${reason}`);
  console.log('='.repeat(78));
  console.log('');
  if (process.env.GITHUB_ACTIONS === 'true')
    console.log(`::warning::Phase 4.1 G2 browser gate SKIPPED (not a pass): ${reason}`);
  process.exit(0);
}

function fail(reason) {
  console.error(`[vkd3d browser gate] FAIL — ${reason}`);
  process.exit(1);
}

// ---------------------------------------------------------------------------
// 1. Artifact gate: the vkd3d->WASM module must be restored (repo side).
// ---------------------------------------------------------------------------
if (!existsSync(moduleJs) || !existsSync(moduleWasm)) {
  const reason =
    'src/ShadowDusk.Wasm/wwwroot/vkd3d/vkd3d-shader.{js,wasm} is not restored. ' +
    'Run tools/restore.ps1 / tools/restore.sh (release tag native-vkd3d-wasm-1.17) ' +
    'and re-run. See src/ShadowDusk.Wasm/wwwroot/vkd3d/RESTORE.md.';
  if (REQUIRE_MODULE) {
    // The hosted pins exist, so in CI (--require-module) a missing module means the
    // restore itself failed — an infrastructure failure that must NOT skip green.
    fail(`--require-module: ${reason}`);
  }
  skip(reason);
}

// ---------------------------------------------------------------------------
// 2. Load the committed ground truth and sanity-check the corpus.
// ---------------------------------------------------------------------------
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
const entries = Object.entries(manifest)
  .filter(([key]) => Object.keys(TARGETS).some((t) => key.startsWith(t + '/')))
  .map(([key, sha256]) => {
    const slash = key.indexOf('/');
    const targetKey = key.slice(0, slash);
    const fixture = key.slice(slash + 1);
    return { key, targetKey, target: TARGETS[targetKey], fixture, expected: sha256 };
  });

const dxCount  = entries.filter((e) => e.targetKey === 'DirectX_Vkd3d').length;
const fnaCount = entries.filter((e) => e.targetKey === 'FNA').length;
if (dxCount === 0 || fnaCount === 0) {
  fail(`manifest has no ${dxCount === 0 ? 'DirectX_Vkd3d' : 'FNA'} entries — ` +
       'an empty corpus cannot prove anything (manifest moved/renamed?).');
}
console.log(`[vkd3d browser gate] corpus from committed manifest: ` +
  `${dxCount} DirectX_Vkd3d (.mgfx) + ${fnaCount} FNA (.fxb) = ${entries.length} artifacts ` +
  '(the FULL DX+FNA byte-identity corpus — no subset).');

for (const e of entries) {
  if (!existsSync(path.join(fixturesDir, e.fixture)))
    fail(`fixture '${e.fixture}' (manifest key ${e.key}) not found under tests/fixtures/shaders/.`);
}

// ---------------------------------------------------------------------------
// 3. Publish the sample (the real consumer app) unless reusing an existing one.
// ---------------------------------------------------------------------------
if (SKIP_PUBLISH && existsSync(path.join(publishRoot, 'index.html'))) {
  console.log(`[vkd3d browser gate] --skip-publish: reusing ${publishRoot}`);
} else {
  const sampleCsproj = path.join('samples', 'ShaderFiddle.Web', 'ShaderFiddle.Web.csproj');
  console.log(`\n$ dotnet publish -c Release ${sampleCsproj} -o ${publishOut}`);
  const r = spawnSync('dotnet', ['publish', '-c', 'Release', sampleCsproj, '-o', publishOut],
    { stdio: 'inherit', cwd: repoRoot, shell: false });
  if (r.status !== 0)
    fail(`dotnet publish exited ${r.status}.`);
}

// The repo-side module IS restored (checked above), so its absence from the publish
// output is a real packaging regression (static-web-asset flow broke) — FAIL, never skip.
const servedWasm = path.join(publishRoot, '_content', 'ShadowDusk.Wasm', 'vkd3d', 'vkd3d-shader.wasm');
if (!existsSync(servedWasm)) {
  fail('vkd3d-shader.wasm is restored in src/ShadowDusk.Wasm/wwwroot/vkd3d/ but MISSING ' +
       `from the publish output (${servedWasm}) — the Razor-SDK static-web-asset flow ` +
       'regressed; the packed NuGet would be broken the same way.');
}

// ---------------------------------------------------------------------------
// 4. Real browser: boot the sample, compile every corpus entry, hash, compare.
// ---------------------------------------------------------------------------
const srv = await startServer(publishRoot);
console.log(`[vkd3d browser gate] serving ${srv.url}`);

const browser = await chromium.launch({
  headless: true,
  // Same deterministic software-GL flags as run-harness.mjs: the sample boots its KNI
  // WebGL game regardless of what we call, and CI runners have no GPU.
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--ignore-gpu-blocklist', '--enable-unsafe-swiftshader'],
});

const rows = [];
const failures = [];
// Phase 42 (issue #28) sync-API results: the cold SD1903 checks, the InitializeAsync
// verdict, and the synchronous-Compile byte-identity pass count.
const syncApi = { cold: [], initialize: null, pass: 0, total: 0 };
// Phase 27 review-input scenarios: SD1902 module-absent e2e + vkd3d-path module
// isolation (the SD1902 attribution fix). Each entry: { name, ok, note }.
const phase27 = [];
// Hard evidence that the REAL module was fetched over HTTP by the browser (and a
// tripwire against any accidental future fallback path): record the network responses
// for vkd3d-shader.{js,wasm}.
const moduleFetches = [];

// Boot one fresh sample page (its own .NET runtime/session) with an optional set of
// Playwright route-abort patterns — the device for the Phase 27 module-absent /
// module-isolation scenarios. Returns the page once the Blazor runtime is up.
async function bootScenarioPage(blockPatterns) {
  const page = await browser.newPage({ viewport: { width: 900, height: 700 } });
  page.setDefaultTimeout(180000);
  for (const pattern of blockPatterns)
    await page.route(pattern, (route) => route.abort('failed'));
  await page.goto(`${srv.url}/`, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(
    () => typeof window.theInstance !== 'undefined' && window.theInstance !== null,
    { timeout: 120000 });
  return page;
}

function recordPhase27(name, ok, note) {
  phase27.push({ name, ok, note });
  if (ok) {
    console.log(`  [OK]   ${name} — ${note}`);
  } else {
    failures.push(`Phase27 ${name}: ${note}`);
    console.error(`  [FAIL] ${name} — ${note}`);
  }
}

try {
  const grayscaleSource = readFileSync(path.join(fixturesDir, 'Grayscale.fx'), 'utf8')
    .replace(/^\u{FEFF}/u, '')
    .replace(/\r\n/g, '\n');

  // ─────────────────────────────────────────────────────────────────────────
  // Phase 27 scenario 1 — SD1902 END-TO-END (review input): the path every
  // consumer hits if the packed vkd3d module ever goes missing. A fresh session
  // where vkd3d/vkd3d-shader.{js,wasm} cannot be fetched (route-aborted — the
  // honest browser analogue of "not restored / not hosted"):
  //   (a) COLD sync Compile() → SD1903 (the module is simply not loaded yet);
  //   (b) async CompileAsync → SD1902 with the helpful restore pointer.
  // ─────────────────────────────────────────────────────────────────────────
  console.log('[vkd3d browser gate] Phase 27 scenario 1 — vkd3d module ABSENT (SD1902 e2e)…');
  {
    const page = await bootScenarioPage([/.*\/vkd3d\/vkd3d-shader\.(js|wasm)(\?.*)?$/]);
    try {
      const cold = await page.evaluate(
        async ({ src, name }) =>
          await window.theInstance.invokeMethodAsync('TestSyncCompileExport', src, 'DirectX', name),
        { src: grayscaleSource, name: 'Grayscale.fx' });
      const coldOk = typeof cold === 'string' && cold.startsWith('ERR:') && cold.includes('SD1903');
      recordPhase27('module-absent cold sync Compile (DirectX)', coldOk,
        coldOk ? 'SD1903 (module not loaded yet — clear, no abort)'
               : `expected SD1903, got: ${String(cold).slice(0, 300)}`);

      const res = await page.evaluate(
        async ({ src, name }) =>
          await window.theInstance.invokeMethodAsync('TestCompileExport', src, 'DirectX', name),
        { src: grayscaleSource, name: 'Grayscale.fx' });
      const isErr = typeof res === 'string' && res.startsWith('ERR:');
      const hasCode = isErr && res.includes('SD1902');
      const hasPointer = isErr && (res.includes('RESTORE.md') || res.includes('tools/restore'));
      recordPhase27('module-absent async CompileAsync (DirectX)', hasCode && hasPointer,
        hasCode && hasPointer
          ? `SD1902 with the restore pointer: ${String(res).slice(4, 160)}…`
          : `expected ERR with SD1902 + the restore pointer, got: ${String(res).slice(0, 400)}`);
    } finally {
      await page.close();
    }
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Phase 27 scenario 2 — SD1902 ATTRIBUTION (review input fix): the vkd3d path
  // must register/load ONLY its own module, so a DXC/SPIRV-Cross asset being
  // unavailable can no longer fail (or mis-headline) a DirectX/FNA compile. A
  // fresh session where the DXC + SPIRV-Cross shims can NOT be fetched but vkd3d
  // can: a DirectX export compile must SUCCEED, byte-identical to the manifest.
  // ─────────────────────────────────────────────────────────────────────────
  console.log('[vkd3d browser gate] Phase 27 scenario 2 — DXC/SPIRV-Cross ABSENT, vkd3d path must be unaffected…');
  {
    const page = await bootScenarioPage([
      /.*\/shadowdusk-dxc\.js(\?.*)?$/,
      /.*\/shadowdusk-spirv-cross\.js(\?.*)?$/,
      /.*\/dxc\/.*$/,
    ]);
    try {
      const probe = entries.find((e) => e.targetKey === 'DirectX_Vkd3d' && e.fixture === 'Grayscale.fx')
        ?? entries.find((e) => e.targetKey === 'DirectX_Vkd3d');
      const res = await page.evaluate(
        async ({ src, name }) =>
          await window.theInstance.invokeMethodAsync('TestCompileExport', src, 'DirectX', name),
        { src: grayscaleSource, name: probe.fixture });

      if (typeof res !== 'string' || !res.startsWith('OK:')) {
        recordPhase27('vkd3d-path isolation (DirectX with DXC/SPIRV-Cross blocked)', false,
          `expected a SUCCESSFUL compile (the vkd3d path must not touch the other modules), got: ${String(res).slice(0, 400)}`);
      } else if (probe.fixture === 'Grayscale.fx') {
        const actual = createHash('sha256').update(Buffer.from(res.slice(3), 'base64')).digest('hex');
        const ok = actual === probe.expected;
        recordPhase27('vkd3d-path isolation (DirectX with DXC/SPIRV-Cross blocked)', ok,
          ok ? 'compiled successfully AND SHA-256 == committed manifest'
             : `compiled, but HASH MISMATCH — manifest=${probe.expected} got=${actual}`);
      } else {
        recordPhase27('vkd3d-path isolation (DirectX with DXC/SPIRV-Cross blocked)', true,
          'compiled successfully (manifest had no Grayscale.fx DX entry; hash not compared)');
      }
    } finally {
      await page.close();
    }
  }

  const page = await browser.newPage({ viewport: { width: 900, height: 700 } });
  page.setDefaultTimeout(180000);
  page.on('pageerror', (e) => console.log(`  [pageerror] ${e.message}`));
  page.on('response', (resp) => {
    const url = resp.url();
    if (/vkd3d-shader\.(js|wasm)$/.test(url))
      moduleFetches.push({ url, status: resp.status() });
  });

  await page.goto(`${srv.url}/`, { waitUntil: 'domcontentloaded' });
  console.log('[vkd3d browser gate] waiting for the Blazor/.NET browser runtime…');
  await page.waitForFunction(
    () => typeof window.theInstance !== 'undefined' && window.theInstance !== null,
    { timeout: 120000 });

  // ─────────────────────────────────────────────────────────────────────────
  // Phase 42 (issue #28) — the InitializeAsync + synchronous Compile() API,
  // proven in the real browser runtime. MUST run in this order:
  //
  //   (a) COLD: sync Compile() before ANY initialization or compile in this
  //       session must fail with the clear, SD-coded SD1903 "await
  //       InitializeAsync() first" error — never an opaque runtime abort.
  //       (Runs FIRST because any compile would warm the modules.)
  //   (b) InitializeAsync(): the one-time warm-up (awaited twice — idempotency),
  //       loading ALL the WASM modules (DXC + SPIRV-Cross + vkd3d).
  //   (c) WARM: sync Compile() over the FULL DX+FNA manifest corpus, every
  //       artifact SHA-256 == the committed cross-host manifest — the synchronous
  //       path produces the exact desktop-render-proven bytes (and therefore the
  //       exact CompileAsync bytes, asserted again by the async pass below).
  // ─────────────────────────────────────────────────────────────────────────
  // Grayscale.fx: in the render-proven corpus of ALL THREE targets, so on every path
  // the source parses cleanly and the failure can only come from the module-readiness
  // gate — the SD1903 under test (Minimal.fx would trip the FNA SM≤3 profile policy
  // first, SD0300, masking the check).
  const coldSource = readFileSync(path.join(fixturesDir, 'Grayscale.fx'), 'utf8')
    .replace(/^\u{FEFF}/u, '')
    .replace(/\r\n/g, '\n');

  console.log('[vkd3d browser gate] Phase 42 (a) — COLD sync Compile() must fail SD1903…');
  for (const target of ['DirectX', 'OpenGL', 'Fna']) {
    const res = await page.evaluate(
      async ({ src, target, name }) =>
        await window.theInstance.invokeMethodAsync('TestSyncCompileExport', src, target, name),
      { src: coldSource, target, name: 'Grayscale.fx' });

    const ok = typeof res === 'string' && res.startsWith('ERR:') && res.includes('SD1903');
    syncApi.cold.push({ target, ok, res: String(res).slice(0, 200) });
    if (!ok) {
      failures.push(`Phase42 cold sync Compile (${target}): expected the clear SD1903 ` +
        `not-initialized error, got: ${String(res).slice(0, 400)}`);
      console.error(`  [FAIL] cold sync Compile (${target}) — expected SD1903, got: ${String(res).slice(0, 200)}`);
    } else {
      console.log(`  [OK]   cold sync Compile (${target}) → SD1903 (clear, diagnosable, no abort)`);
    }
  }

  console.log('[vkd3d browser gate] Phase 42 (b) — await InitializeAsync() (loads DXC + SPIRV-Cross + vkd3d)…');
  {
    const res = await page.evaluate(
      async () => await window.theInstance.invokeMethodAsync('TestInitializeCompiler'));
    syncApi.initialize = String(res).slice(0, 400);
    if (res !== 'OK') {
      failures.push(`Phase42 InitializeAsync failed: ${String(res).slice(0, 400)}`);
      console.error(`  [FAIL] InitializeAsync — ${String(res).slice(0, 200)}`);
    } else {
      console.log('  [OK]   InitializeAsync completed (awaited twice — idempotent)');
    }
  }

  console.log('[vkd3d browser gate] Phase 42 (c) — WARM sync Compile() over the full corpus…');
  for (const e of entries) {
    const label = `${e.targetKey}/${e.fixture} (sync)`;
    syncApi.total++;
    try {
      const source = readFileSync(path.join(fixturesDir, e.fixture), 'utf8')
        .replace(/^\u{FEFF}/u, '')
        .replace(/\r\n/g, '\n');

      const res = await page.evaluate(
        async ({ src, target, name }) =>
          await window.theInstance.invokeMethodAsync('TestSyncCompileExport', src, target, name),
        { src: source, target: e.target, name: e.fixture });

      if (typeof res !== 'string' || !res.startsWith('OK:')) {
        failures.push(`${label}: sync compile failed: ${String(res).slice(0, 400)}`);
        console.error(`  [FAIL] ${label} — ${String(res).slice(0, 200)}`);
        continue;
      }

      const artifact = Buffer.from(res.slice(3), 'base64');
      const actual = createHash('sha256').update(artifact).digest('hex');
      if (actual !== e.expected) {
        failures.push(`${label}: HASH MISMATCH — manifest=${e.expected} sync-browser=${actual}`);
        console.error(`  [DIFF] ${label} — manifest=${e.expected} sync-browser=${actual}`);
      } else {
        syncApi.pass++;
        console.log(`  [OK]   ${label} — ${artifact.length} bytes, SHA-256 == committed manifest`);
      }
    } catch (err) {
      failures.push(`${label}: harness error: ${err?.message ?? err}`);
      console.error(`  [FAIL] ${label} — harness error: ${err?.message ?? err}`);
    }
  }

  for (const e of entries) {
    const label = `${e.targetKey}/${e.fixture}`;
    const row = { ...e, bytes: null, actual: null, verdict: 'FAIL', note: '' };
    try {
      // Normalize the source exactly like CrossHostByteIdentityTests / Vkd3dCorpusProbe
      // read it (.NET File.ReadAllTextAsync + CRLF->LF): strip the UTF-8 BOM — .NET's
      // UTF-8 decoder drops it, Node's keeps it as U+FEFF, and 9 corpus fixtures carry
      // one (a BOM-prefixed source fails the HLSL parse) — and LF-normalize EOLs so git
      // checkout flavor cannot leak into the experiment.
      const source = readFileSync(path.join(fixturesDir, e.fixture), 'utf8')
        .replace(/^\u{FEFF}/u, '')
        .replace(/\r\n/g, '\n');

      const res = await page.evaluate(
        async ({ src, target, name }) =>
          await window.theInstance.invokeMethodAsync('TestCompileExport', src, target, name),
        { src: source, target: e.target, name: e.fixture });

      if (typeof res !== 'string' || !res.startsWith('OK:')) {
        row.note = `in-browser compile failed: ${String(res).slice(0, 400)}`;
        failures.push(`${label}: ${row.note}`);
        console.error(`  [FAIL] ${label} — ${row.note}`);
        rows.push(row);
        continue;
      }

      const artifact = Buffer.from(res.slice(3), 'base64');
      row.bytes = artifact.length;
      row.actual = createHash('sha256').update(artifact).digest('hex');

      if (row.actual !== e.expected) {
        row.note = `HASH MISMATCH — manifest(win-x64 desktop)=${e.expected} browser=${row.actual}`;
        failures.push(`${label}: ${row.note}`);
        console.error(`  [DIFF] ${label} — ${row.note}`);
      } else {
        row.verdict = 'PASS';
        console.log(`  [OK]   ${label} — ${artifact.length} bytes, SHA-256 == committed manifest (in-browser ${e.target})`);
      }
    } catch (err) {
      row.note = `harness error: ${err?.message ?? err}`;
      failures.push(`${label}: ${row.note}`);
      console.error(`  [FAIL] ${label} — ${row.note}`);
    }
    rows.push(row);
  }
} finally {
  await browser.close();
  await srv.close();
}

// The compiles can only have succeeded through the served module — but assert the
// fetch was observed anyway, so a future accidental fallback can never pass silently.
const wasmFetch = moduleFetches.find((f) => f.url.endsWith('vkd3d-shader.wasm') && f.status === 200);
if (failures.length === 0 && !wasmFetch) {
  failures.push('IMPOSSIBLE STATE: all compiles passed but no HTTP fetch of ' +
    'vkd3d-shader.wasm was observed — the browser did not run the faithful module.');
}

// ---------------------------------------------------------------------------
// 5. Results + verdict.
// ---------------------------------------------------------------------------
const pass = rows.filter((r) => r.verdict === 'PASS').length;
await writeResults(rows, pass, failures, wasmFetch);

console.log('');
if (failures.length > 0) {
  console.error(`[vkd3d browser gate] FAIL — ${pass}/${rows.length} byte-identical; ${failures.length} failure(s):`);
  for (const f of failures) console.error('  - ' + f);
  process.exit(1);
}

console.log(`ALL ${rows.length} DX+FNA ARTIFACTS COMPILED IN A REAL BROWSER ARE BYTE-IDENTICAL ` +
  '(SHA-256) TO THE COMMITTED CROSS-HOST MANIFEST — browser bytes == desktop ' +
  'render-proven bytes. Phase 4.1 G2 gate PASSED.');
console.log(`PHASE 42 (issue #28) PASSED: cold sync Compile() → SD1903 on all ` +
  `${syncApi.cold.length} targets; InitializeAsync OK (idempotent); warm SYNCHRONOUS ` +
  `Compile() byte-identical to the manifest ${syncApi.pass}/${syncApi.total}.`);
console.log(`PHASE 27 PASSED: module-absent e2e → SD1903 (cold sync) + SD1902 with the ` +
  `restore pointer (async), and the vkd3d path compiled UNAFFECTED with DXC/SPIRV-Cross ` +
  `assets blocked (${phase27.filter((s) => s.ok).length}/${phase27.length} scenario checks).`);
process.exit(0);

async function writeResults(rows, pass, failures, wasmFetch) {
  const lines = [];
  lines.push('# Phase 4.1 G2 — Real-browser DirectX/FNA export byte-identity proof');
  lines.push('');
  lines.push(`_Generated by \`browser-vkd3d-gate.mjs\` — headless Chromium (Playwright), the published ` +
    '`samples/ShaderFiddle.Web` KNI/Blazor sample (the actual .NET-browser runtime), real ' +
    '`WasmShaderCompiler.CompileAsync` via the `TestCompileExport` JS-interop hook, real HTTP fetch of ' +
    '`_content/ShadowDusk.Wasm/vkd3d/vkd3d-shader.{js,wasm}` from the served static web assets._');
  lines.push('');
  lines.push(`## Verdict: ${failures.length === 0 ? `**PASS — ${pass}/${rows.length} byte-identical**` : `**FAIL — ${pass}/${rows.length} byte-identical, ${failures.length} failure(s)**`}`);
  lines.push('');
  lines.push('A browser cannot render DXBC or D3D9 bytecode (no Direct3D in a browser), so the honest');
  lines.push('browser-side bar for the DirectX/FNA **export** targets is byte-identity, and');
  lines.push('render-equivalence closes by **transitivity** — the Phase 37 cross-host-manifest argument');
  lines.push('with the browser as the fourth host:');
  lines.push('');
  lines.push('1. **Browser bytes == manifest bytes** (this run: every artifact compiled in-browser by the');
  lines.push('   real product pipeline hashes equal to `tests/fixtures/golden/byte-identity/manifest.json`).');
  lines.push('2. **Manifest bytes == desktop bytes on every OS** (`CrossHostByteIdentityTests`, CI-asserted');
  lines.push('   on windows/ubuntu/macos — Phase 37 verification tail).');
  lines.push('3. **Desktop bytes are rung-4 render-proven**: the DX corpus loads + renders equivalently to');
  lines.push('   `mgfxc` in real MonoGame WindowsDX (Phase 18), and the FNA corpus loads + renders');
  lines.push('   pixel-equivalent (Δ ≤ 1/255) to `fxc /T fx_2_0` in real FNA (Phases 39/40, gate 17/17).');
  lines.push('');
  lines.push('Therefore the bytes a browser user exports ARE the render-proven bytes. **Not claimed:**');
  lines.push('rendering inside the browser — that is impossible for these targets by construction, not a');
  lines.push('gap this gate papers over.');
  lines.push('');
  lines.push('## Phase 42 (issue #28) — InitializeAsync + synchronous Compile()');
  lines.push('');
  lines.push('The synchronous-compile API, proven in the same real browser session (the lazy-load');
  lines.push('order matters: the cold checks ran before ANY compile or initialization):');
  lines.push('');
  for (const c of syncApi.cold) {
    lines.push(`- COLD sync \`Compile()\` (${c.target}, before InitializeAsync): ` +
      (c.ok ? '**SD1903** — the clear "await InitializeAsync() first" error, no runtime abort. PASS'
            : `**FAIL** — got \`${c.res.replace(/\|/g, '\\|')}\``));
  }
  lines.push(`- \`InitializeAsync()\` (awaited twice — idempotency): ${syncApi.initialize === 'OK' ? '**OK**' : `**FAIL** — \`${syncApi.initialize}\``}`);
  lines.push(`- WARM **synchronous** \`Compile()\` over the full DX+FNA corpus: **${syncApi.pass}/${syncApi.total}** SHA-256 == committed manifest (sync bytes == async bytes == desktop render-proven bytes).`);
  lines.push('');
  lines.push('## Phase 27 — SD1902 end-to-end + attribution scenarios');
  lines.push('');
  lines.push('Fresh per-scenario browser sessions with route-aborted static web assets (the honest');
  lines.push('browser analogue of "module not restored/hosted"):');
  lines.push('');
  for (const s of phase27) {
    lines.push(`- ${s.name}: ${s.ok ? '**PASS**' : '**FAIL**'} — ${s.note.replace(/\|/g, '\\|')}`);
  }
  lines.push('');
  lines.push('## Coverage');
  lines.push('');
  const dx = rows.filter((r) => r.targetKey === 'DirectX_Vkd3d');
  const fna = rows.filter((r) => r.targetKey === 'FNA');
  lines.push(`- **DirectX (SM4/5 DXBC → MGFX v10 \`.mgfx\`):** ${dx.filter((r) => r.verdict === 'PASS').length}/${dx.length} fixtures (the full DX byte-identity corpus — core MGFX + SM≤3 render-proven sets).`);
  lines.push(`- **FNA (SM1–3 D3D9 → fx_2_0 \`.fxb\`):** ${fna.filter((r) => r.verdict === 'PASS').length}/${fna.length} fixtures (the full FNA byte-identity corpus).`);
  lines.push('- No subset, no silent caps: every `DirectX_Vkd3d/*` and `FNA/*` manifest entry ran.');
  lines.push(`- Faithful-module evidence: \`vkd3d-shader.wasm\` fetched over HTTP by the page — ${wasmFetch ? `**yes** (\`${wasmFetch.url}\`, HTTP ${wasmFetch.status})` : '**NO — failure recorded above**'}.`);
  lines.push('');
  lines.push('| Manifest key | Target | Artifact bytes | SHA-256 == manifest | Verdict |');
  lines.push('|---|---|---|---|---|');
  for (const r of rows) {
    const hashCell = r.actual === null ? '—'
      : (r.actual === r.expected ? `yes (\`${r.actual.slice(0, 12)}…\`)` : `**NO** — browser \`${r.actual}\` vs manifest \`${r.expected}\``);
    lines.push(`| ${r.key} | ${r.target} | ${r.bytes ?? '—'} | ${hashCell} | ${r.verdict}${r.note ? ` — ${r.note.replace(/\|/g, '\\|')}` : ''} |`);
  }
  lines.push('');
  lines.push('## How to re-run');
  lines.push('');
  lines.push('```bash');
  lines.push('./tools/restore.sh                        # vkd3d-shader.{js,wasm} (tag native-vkd3d-wasm-1.17)');
  lines.push('cd tests/ShadowDusk.BrowserTests');
  lines.push('npm install && npx playwright install chromium');
  lines.push('node browser-vkd3d-gate.mjs               # publishes the sample into .publish-vkd3d/');
  lines.push('```');
  lines.push('');
  await fs.writeFile(resultsFile, lines.join('\n') + '\n');
  console.log(`[vkd3d browser gate] wrote ${path.basename(resultsFile)}`);
}
