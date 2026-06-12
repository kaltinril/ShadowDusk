// Phase 4.1 byte-identity gate (the bar) — vkd3d->WASM == desktop vkd3d.
//
// Mirrors the Phase 23 G1 mechanism (.wasm-build/node-test-dxc-shim.mjs): drive the
// PRODUCT shim (src/ShadowDusk.Wasm/wwwroot/shadowdusk-vkd3d.js) through its real
// contract surface under node — await ensureReady() (twice, to exercise idempotency),
// then compile(sourceUtf8, entryPoint, profile, sourceName, targetType) — and assert
// every output byte-identical to the DESKTOP vkd3d backend's output over the DX (SM5
// DXBC_TPF) and FNA (SM1-3 D3D_BYTECODE) byte-identity corpus.
//
// The desktop ground truth is captured fresh each run by Vkd3dCorpusProbe (dotnet),
// which records every vkd3d compile the REAL pipeline issues (preprocessed source
// bytes, entry point, profile, target type, output blob) through the same
// dxbcCompilerFactory seam the WASM host uses — so the comparison sits at the exact
// seam that differs between hosts.
//
// SKIP-WITH-NOTICE (never a fabricated pass):
//   - vkd3d-shader.{js,wasm} not restored (hosted build pending,
//     tag native-vkd3d-wasm-1.17)            -> loud SKIP, exit 0.
//   - desktop vkd3d native not restored (probe exit 3 / SD0211) -> loud SKIP, exit 0.
// Any compile failure or byte mismatch -> FAIL, exit 1.
//
// Usage:  cd tests/ShadowDusk.BrowserTests && node node-test-vkd3d-wasm.mjs
//         (or: npm run vkd3d-gate)

import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');

const shimPath = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'shadowdusk-vkd3d.js');
const moduleJs = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'vkd3d', 'vkd3d-shader.js');
const moduleWasm = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'vkd3d', 'vkd3d-shader.wasm');
const probeDir = path.join(__dirname, 'Vkd3dCorpusProbe');
const outDir = path.join(__dirname, '.vkd3d-gate');

function skip(reason) {
  console.log('');
  console.log('='.repeat(78));
  console.log('[vkd3d-wasm gate] SKIPPED — NOT RUN, NOT A PASS.');
  console.log(`[vkd3d-wasm gate] ${reason}`);
  console.log('='.repeat(78));
  console.log('');
  process.exit(0);
}

// ---------------------------------------------------------------------------
// 1. Artifact gate: the vkd3d->WASM module must be restored.
// ---------------------------------------------------------------------------
if (!existsSync(moduleJs) || !existsSync(moduleWasm)) {
  skip(
    'src/ShadowDusk.Wasm/wwwroot/vkd3d/vkd3d-shader.{js,wasm} is not restored. ' +
    'The hosted build (release tag native-vkd3d-wasm-1.17) is pending; once it exists, ' +
    'run tools/restore.ps1 / tools/restore.sh (or place a local build in ' +
    '.wasm-build/vkd3d-wasm-out/) and re-run this gate. See ' +
    'src/ShadowDusk.Wasm/wwwroot/vkd3d/RESTORE.md.');
}

// ---------------------------------------------------------------------------
// 2. Capture the desktop ground truth (Vkd3dCorpusProbe).
// ---------------------------------------------------------------------------
mkdirSync(outDir, { recursive: true });
console.log('[vkd3d-wasm gate] capturing desktop vkd3d ground truth (Vkd3dCorpusProbe)…');
const probe = spawnSync(
  'dotnet', ['run', '--project', probeDir, '--', repoRoot, outDir],
  { stdio: 'inherit', cwd: repoRoot, shell: false });

if (probe.status === 3) {
  skip('desktop vkd3d-shader native not restored (probe exit 3 / SD0211) — run tools/restore.*.');
}
if (probe.status !== 0) {
  console.error(`[vkd3d-wasm gate] FAIL — Vkd3dCorpusProbe exited ${probe.status}.`);
  process.exit(1);
}

const manifest = JSON.parse(readFileSync(path.join(outDir, 'manifest.json'), 'utf8'));
if (!Array.isArray(manifest) || manifest.length === 0) {
  console.error('[vkd3d-wasm gate] FAIL — probe produced an empty manifest.');
  process.exit(1);
}

// ---------------------------------------------------------------------------
// 3. Replay every captured compile through the PRODUCT shim and byte-compare.
// ---------------------------------------------------------------------------
const shim = await import(pathToFileURL(shimPath).href);

// Idempotency: the contract allows (and the C# backend performs) repeated awaits.
await shim.ensureReady();
await shim.ensureReady();

let pass = 0;
const failures = [];

for (const entry of manifest) {
  const label = `${entry.target}/${entry.fixture} ${entry.stage} ${entry.entryPoint} (${entry.profile} -> tt${entry.targetType})`;
  try {
    const source = new Uint8Array(readFileSync(path.join(outDir, entry.sourceFile)));
    const expected = new Uint8Array(readFileSync(path.join(outDir, entry.blobFile)));

    const actual = shim.compile(source, entry.entryPoint, entry.profile, entry.sourceName, entry.targetType);

    if (actual.length !== expected.length || !actual.every((b, i) => b === expected[i])) {
      const firstDiff = actual.findIndex((b, i) => b !== expected[i]);
      failures.push(`${label}: MISMATCH — desktop ${expected.length} B, wasm ${actual.length} B, ` +
        `first differing byte index ${firstDiff < 0 ? expected.length : firstDiff}`);
      console.error(`  [DIFF] ${label}`);
      continue;
    }

    pass++;
    console.log(`  [OK]   ${label} — ${actual.length} bytes, byte-identical to desktop vkd3d (via SHIM)`);
  } catch (e) {
    failures.push(`${label}: THREW — ${e?.message ?? e}`);
    console.error(`  [FAIL] ${label} — ${e?.message ?? e}`);
  }
}

// ---------------------------------------------------------------------------
// 4. Error path THROUGH THE PRODUCT SHIM: a broken shader must throw an Error
//    whose message is vkd3d's VERBATIM diagnostic (source name + line:col), the
//    text .NET parses for constraint 5. Without this, a regression in the shim's
//    failure branch (e.g. dropping/garbling the messages) reddens nothing — the
//    corpus above only exercises the success path.
// ---------------------------------------------------------------------------
{
  const brokenName = 'broken.fx';
  // Same deliberately broken shader as tools/vkd3d-wasm/smoke-test.mjs (missing ')').
  const brokenSource = new TextEncoder().encode(
    'float4 main() : COLOR { return float4(1,0,0,1; }\n');
  const label = `error-path/${brokenName} (ps_2_0 -> tt4, via SHIM)`;
  try {
    shim.compile(brokenSource, 'main', 'ps_2_0', brokenName, 4);
    failures.push(`${label}: a broken shader COMPILED instead of throwing`);
    console.error(`  [FAIL] ${label} — compiled instead of throwing`);
  } catch (e) {
    const msg = String(e?.message ?? e);
    // Verbatim vkd3d diagnostics carry "<sourceName>:<line>:<col>:" locations.
    if (new RegExp(`${brokenName.replace('.', '\\.')}:\\d+:\\d+`).test(msg)) {
      pass++;
      console.log(`  [OK]   ${label} — threw with verbatim diagnostics: ${msg.split('\n')[0]}`);
    } else {
      failures.push(`${label}: thrown message lacks the verbatim ` +
        `'${brokenName}:<line>:<col>' diagnostic — got: ${msg.slice(0, 300)}`);
      console.error(`  [FAIL] ${label} — message lacks file:line:col — got: ${msg.slice(0, 300)}`);
    }
  }
}
const totalCases = manifest.length + 1; // corpus + the error-path case

console.log('');
if (failures.length > 0) {
  console.error(`[vkd3d-wasm gate] FAIL — ${pass}/${totalCases} byte-identical; ${failures.length} failures:`);
  for (const f of failures) console.error('  - ' + f);
  process.exit(1);
}

console.log(`ALL ${manifest.length} CORPUS COMPILES BYTE-IDENTICAL VIA THE FAITHFUL SHIM ` +
  '(+ the shim error path surfaces verbatim diagnostics) — ' +
  'WASM vkd3d == desktop vkd3d. Phase 4.1 byte-identity gate PASSED.');
process.exit(0);
