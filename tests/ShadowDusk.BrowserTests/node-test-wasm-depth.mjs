// Issue #271 WASM stack-depth gate: deeply nested but valid shaders must compile in the
// browser modules exactly as on the desktop, and a module that does trap must recover.
//
// Emscripten links a 64 KB stack unless told otherwise, where the desktop natives get 1 MB
// (Windows) or 8 MB (Linux/macOS). At 64 KB the DXC module hung on a 100-term add chain and
// trapped or miscompiled deeper nesting, SPIRV-Cross corrupted itself from about a dozen
// nested ifs, and vkd3d trapped at 75 else-ifs (.wasm-build/WASM-STACK-DEPTH.md). The modules
// now link an 8 MB stack placed below static data.
//
//   1. Depth cases. Vkd3dCorpusProbe --depth captures the desktop ground truth (DXC SPIR-V,
//      desktop SPIRV-Cross GLSL, desktop vkd3d DXBC) for nested shapes the 64 KB modules
//      failed on; each is replayed through the PRODUCT shims (src/ShadowDusk.Wasm/wwwroot)
//      in its own node process with a timeout (a 64 KB module could spin forever) and must
//      be byte-identical.
//   2. Trap recovery. Far deeper source still exhausts the JS engine's own stack (wasm
//      frames live there too), which no STACK_SIZE can raise. The shim must then throw its
//      '<module> trapped:' error (WasmShaderCompiler maps it to SD1907), refuse the next call
//      until ensureReady(), and after ensureReady() compile correctly again.
//
// The vkd3d module is hosted on a release tag and re-pinned in tools/restore.*; while the
// restored module is still the pre-#271 build (64 KB stack), its depth arm is reported
// loudly as NOT RUN rather than failed, and runs automatically once the rebuilt module is
// pinned.
//
// SKIP-WITH-NOTICE (never a fabricated pass): modules not restored, or the desktop natives
// not restored (probe exit 3). --require-module turns a missing module into a failure.
//
// Usage:  cd tests/ShadowDusk.BrowserTests && node node-test-wasm-depth.mjs [--require-module]

import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const www = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot');
const outDir = path.join(__dirname, '.depth-gate');
const CASE_TIMEOUT_MS = 240000;

// SHA-256 of the vkd3d-shader.wasm hosted on native-vkd3d-wasm-2.1, linked before issue #271
// with emscripten's 64 KB default stack (it traps on the depth cases).
const VKD3D_WASM_PRE_271 = '3e8c85104ab9a793220615e2ff22c3dc882d6dd1348cc20e16e7c9cfb7251a00';

const eq = (a, b) => a.length === b.length && a.every((x, i) => x === b[i]);
const shim = (name) => import(pathToFileURL(path.join(www, name)).href);
const describe = (e) => String(e?.message ?? e).replace(/\s+/g, ' ').slice(0, 200);

// The same nested bodies as DepthProbe.Body (Vkd3dCorpusProbe/DepthProbe.cs).
function body(kind, n) {
  const range = Array.from({ length: n }, (_, i) => i);
  switch (kind) {
    case 'add': return `float x = ${Array(n).fill('uv.x').join(' + ')}; return float4(x,0,0,1);`;
    case 'elseif': return 'float x = 0.0; ' + range.map((i) => `if (uv.x > ${i}.0) x = ${i}.0;`).join(' else ') + ' return float4(x,0,0,1);';
    default: throw new Error(kind);
  }
}
// Swap the MainPS body of a captured, preprocessed source for a deeper one.
function deepen(source, kind, n) {
  const start = source.indexOf('float2 uv = input.TextureCoordinates; ');
  const end = source.indexOf(' return float4(x,0,0,1); }', start);
  if (start < 0 || end < 0) throw new Error('depth-gate: cannot locate the MainPS body to deepen');
  return source.slice(0, start) + 'float2 uv = input.TextureCoordinates; ' + body(kind, n) + source.slice(end + ' return float4(x,0,0,1);'.length);
}

// ---------------------------------------------------------------------------
// Worker: one stage of one case, in its own process (so a hang is a timeout, not a stuck gate).
// ---------------------------------------------------------------------------
if (process.argv[2] === '--case') {
  const [, , , stage, id] = process.argv;
  const manifest = JSON.parse(readFileSync(path.join(outDir, 'manifest.json'), 'utf8'));
  const c = manifest.find((m) => m.id === id);
  let verdict;
  try {
    if (stage === 'dxc') {
      const m = await shim('shadowdusk-dxc.js'); await m.ensureReady();
      const out = m.compileToSpirv(readFileSync(path.join(outDir, `${id}.dxc.hlsl`), 'utf8'), c.dxcArgs);
      verdict = eq(out, new Uint8Array(readFileSync(path.join(outDir, `${id}.spv`)))) ? 'OK' : `DIFF (${out.length} B vs desktop)`;
    } else if (stage === 'spirv-cross') {
      const m = await shim('shadowdusk-spirv-cross.js');
      const glsl = m.transpileToGlsl(new Uint8Array(readFileSync(path.join(outDir, `${id}.spv`))), false, true, 140, false, false, true);
      verdict = glsl === readFileSync(path.join(outDir, `${id}.glsl`), 'utf8') ? 'OK' : `DIFF (${glsl.length} chars vs desktop)`;
    } else {
      const m = await shim('shadowdusk-vkd3d.js'); await m.ensureReady();
      // c.options: the vkd3d compile options the desktop passed for this compile (issue #295).
      // compile() returns { code, messages } (issue #335); the depth arm compares the code.
      const out = m.compile(new Uint8Array(readFileSync(path.join(outDir, `${id}.vkd3d.hlsl`))), c.entryPoint, c.profile, c.sourceName, c.targetType, c.options).code;
      verdict = eq(out, new Uint8Array(readFileSync(path.join(outDir, `${id}.dxbc`)))) ? 'OK' : `DIFF (${out.length} B vs desktop)`;
    }
  } catch (e) {
    verdict = 'THREW ' + describe(e);
  }
  console.log('RESULT ' + verdict);
  process.exit(0);
}

// ---------------------------------------------------------------------------
// Main.
// ---------------------------------------------------------------------------
const requireModule = process.argv.includes('--require-module');
function skip(reason) {
  if (requireModule) { console.error(`[wasm-depth gate] FAIL (--require-module): ${reason}`); process.exit(1); }
  console.log('\n' + '='.repeat(78) + `\n[wasm-depth gate] SKIPPED: NOT RUN, NOT A PASS.\n[wasm-depth gate] ${reason}\n` + '='.repeat(78) + '\n');
  process.exit(0);
}

for (const f of ['dxc/dxcompiler.js', 'dxc/dxcompiler.wasm', 'spirv-cross/spirv-cross.wasm', 'vkd3d/vkd3d-shader.js', 'vkd3d/vkd3d-shader.wasm']) {
  if (!existsSync(path.join(www, f))) skip(`src/ShadowDusk.Wasm/wwwroot/${f} is not restored (run tools/restore.ps1 / tools/restore.sh).`);
}

console.log('[wasm-depth gate] capturing desktop ground truth (Vkd3dCorpusProbe --depth)...');
const probe = spawnSync('dotnet', ['run', '--project', path.join(__dirname, 'Vkd3dCorpusProbe'), '--', '--depth', repoRoot, outDir],
  { stdio: 'inherit', cwd: repoRoot, shell: false });
if (probe.status === 3) skip('desktop natives not restored (probe exit 3 / SD0211); run tools/restore.*.');
if (probe.status !== 0) { console.error(`[wasm-depth gate] FAIL: the depth probe exited ${probe.status}.`); process.exit(1); }

const manifest = JSON.parse(readFileSync(path.join(outDir, 'manifest.json'), 'utf8'));
const vkd3dSha = createHash('sha256').update(readFileSync(path.join(www, 'vkd3d', 'vkd3d-shader.wasm'))).digest('hex');
const vkd3dPre271 = vkd3dSha === VKD3D_WASM_PRE_271;
const failures = [];
let pass = 0;

function runCase(stage, id) {
  const r = spawnSync(process.execPath, [fileURLToPath(import.meta.url), '--case', stage, id],
    { encoding: 'utf8', timeout: CASE_TIMEOUT_MS, maxBuffer: 1 << 26 });
  const line = (r.stdout || '').split('\n').find((l) => l.startsWith('RESULT '));
  if (line) return line.slice(7);
  return r.error ? `HUNG (no result within ${CASE_TIMEOUT_MS / 1000} s)` : `DIED (exit ${r.status}) ${describe(r.stderr)}`;
}

console.log(`\n[wasm-depth gate] 1. depth cases (${manifest.length} shapes x DXC, SPIRV-Cross, vkd3d)`);
for (const c of manifest) {
  for (const stage of ['dxc', 'spirv-cross', 'vkd3d']) {
    const label = `${c.id} ${stage}`;
    if (stage === 'vkd3d' && vkd3dPre271) {
      console.log(`  [NOT RUN] ${label}: the restored vkd3d module is the pre-#271 64 KB-stack build`);
      continue;
    }
    const verdict = runCase(stage, c.id);
    if (verdict === 'OK') { pass++; console.log(`  [OK]   ${label}: byte-identical to the desktop`); }
    else { failures.push(`${label}: ${verdict}`); console.error(`  [FAIL] ${label}: ${verdict}`); }
  }
}

// ---------------------------------------------------------------------------
// 2. Trap recovery through the product shims, in this process.
// ---------------------------------------------------------------------------
console.log('\n[wasm-depth gate] 2. trap recovery (trap -> refused until ensureReady -> correct compile)');
async function trapRecovery(label, trap, again, isRight, reload) {
  let trapped = '';
  try { trap(); } catch (e) { trapped = describe(e); }
  if (!/ trapped: /.test(trapped)) {
    // The trigger did not trap on this engine (its stack limit differs): that is not a pass
    // of the recovery path, so say so loudly instead of counting it.
    console.log(`  [NOT RUN] ${label}: the trigger did not trap here (${trapped || 'it compiled'})`);
    return;
  }
  let refused = '';
  try { again(); } catch (e) { refused = describe(e); }
  await reload();
  let ok = false, detail = '';
  try { ok = isRight(again()); } catch (e) { detail = describe(e); }
  if (/not (ready|loaded)/.test(refused) && ok) {
    pass++;
    console.log(`  [OK]   ${label}: ${trapped.slice(0, 90)}; next call refused; reloaded output byte-identical`);
  } else {
    failures.push(`${label}: trapped='${trapped.slice(0, 90)}', refused='${refused.slice(0, 90)}', reloaded ok=${ok} ${detail}`);
    console.error(`  [FAIL] ${label}: refused='${refused.slice(0, 90)}', reloaded ok=${ok} ${detail}`);
  }
}

{
  const ref = manifest.find((m) => m.kind === 'add');
  const refHlsl = readFileSync(path.join(outDir, `${ref.id}.dxc.hlsl`), 'utf8');
  const refSpv = new Uint8Array(readFileSync(path.join(outDir, `${ref.id}.spv`)));
  const refGlsl = readFileSync(path.join(outDir, `${ref.id}.glsl`), 'utf8');

  const dxc = await shim('shadowdusk-dxc.js'); await dxc.ensureReady();
  await trapRecovery('DXC (add x5000)',
    () => dxc.compileToSpirv(deepen(refHlsl, 'add', 5000), ref.dxcArgs),
    () => dxc.compileToSpirv(refHlsl, ref.dxcArgs), (out) => eq(out, refSpv), () => dxc.ensureReady());

  // SPIR-V deep enough to exhaust the engine stack in SPIRV-Cross: an 800-branch else-if
  // chain, compiled by the (now 8 MB) DXC module itself.
  const elseif = manifest.find((m) => m.kind === 'elseif');
  let deepSpv = null;
  try {
    deepSpv = dxc.compileToSpirv(deepen(readFileSync(path.join(outDir, `${elseif.id}.dxc.hlsl`), 'utf8'), 'elseif', 800), elseif.dxcArgs);
  } catch (e) {
    console.log(`  [NOT RUN] SPIRV-Cross trap: DXC could not produce the 800-branch SPIR-V (${describe(e)})`);
  }
  if (deepSpv) {
    const spvc = await shim('shadowdusk-spirv-cross.js');
    await trapRecovery('SPIRV-Cross (else-if x800)',
      () => spvc.transpileToGlsl(deepSpv, false, true, 140, false, false, true),
      () => spvc.transpileToGlsl(refSpv, false, true, 140, false, false, true), (g) => g === refGlsl, () => spvc.ensureReady());
  }

  const vk = await shim('shadowdusk-vkd3d.js'); await vk.ensureReady();
  const calls = manifest.find((m) => m.kind === 'calls');
  let deepCalls = 'float f0(float x) { return x * 1.5; }\n';
  for (let i = 1; i <= 12800; i++) deepCalls += `float f${i}(float x) { return f${i - 1}(x) + 1.0; }\n`;
  deepCalls += 'float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return float4(f12800(uv.x),0,0,1); }\n';
  const refVk = manifest.find((m) => m.kind === 'add');
  const refVkSrc = new Uint8Array(readFileSync(path.join(outDir, `${refVk.id}.vkd3d.hlsl`)));
  const refDxbc = new Uint8Array(readFileSync(path.join(outDir, `${refVk.id}.dxbc`)));
  await trapRecovery('vkd3d (call chain x12800)',
    () => vk.compile(new TextEncoder().encode(deepCalls), 'MainPS', calls.profile, 'deep.fx', calls.targetType, calls.options).code,
    () => vk.compile(refVkSrc, refVk.entryPoint, refVk.profile, refVk.sourceName, refVk.targetType, refVk.options).code, (out) => eq(out, refDxbc), () => vk.ensureReady());
}

console.log('');
if (vkd3dPre271) {
  console.log('[wasm-depth gate] NOTICE: the vkd3d depth arm did NOT run: the restored module ' +
    `(sha256 ${vkd3dSha.slice(0, 12)}...) is the pre-#271 64 KB-stack build. It runs once the rebuilt ` +
    'module (vkd3d-wasm-build.yml, 8 MB stack) is hosted and re-pinned in tools/restore.*.');
}
if (failures.length > 0) {
  console.error(`[wasm-depth gate] FAIL: ${pass} passed, ${failures.length} failed:`);
  for (const f of failures) console.error('  - ' + f);
  process.exit(1);
}
console.log(`[wasm-depth gate] PASSED: ${pass} checks (depth cases byte-identical to the desktop, trap recovery).`);
process.exit(0);
