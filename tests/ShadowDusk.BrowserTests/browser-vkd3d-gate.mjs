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
// -> loud SKIP, exit 0 (run tools/restore.* first). Everything else that goes wrong
// (publish missing the module, compile failure, hash mismatch, the .wasm never being
// fetched) -> FAIL, exit 1.
//
// Usage:  cd tests/ShadowDusk.BrowserTests
//         node browser-vkd3d-gate.mjs [--skip-publish]
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
  skip(
    'src/ShadowDusk.Wasm/wwwroot/vkd3d/vkd3d-shader.{js,wasm} is not restored. ' +
    'Run tools/restore.ps1 / tools/restore.sh (release tag native-vkd3d-wasm-1.17) ' +
    'and re-run. See src/ShadowDusk.Wasm/wwwroot/vkd3d/RESTORE.md.');
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
// Hard evidence that the REAL module was fetched over HTTP by the browser (and a
// tripwire against any accidental future fallback path): record the network responses
// for vkd3d-shader.{js,wasm}.
const moduleFetches = [];

try {
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
