// Phase 23 M3 setup — the FAITHFUL in-browser-compile render proof (Gate G2).
//
// Companion to publish-sample-sd.mjs (mode-1, ShadowDusk's own precompiled bytes).
// This script prepares the published wwwroot so the harness's mode-2 path compiles
// EACH corpus shader ENTIRELY IN-BROWSER through the FAITHFUL pipeline:
//     WasmShaderCompiler.CompileAsync(.fx)
//        -> JsDxcShaderCompiler  -> shadowdusk-dxc  (FAITHFUL DXC->WASM)  -> SPIR-V
//        -> JsSpirvToGlslTranspiler -> shadowdusk-spirv-cross (SPIRV-Cross WASM) -> GLSL
//        -> SpirvReflector + MGFX writer (pure managed) -> .mgfx
//        -> new Effect(gd, bytes) in KNI WebGL -> render
// and pixel-compares the WebGL canvas against references-sd/ (the desktop DesktopGL
// render of ShadowDusk's OWN .mgfx). Because the faithful DXC->WASM SPIR-V is
// byte-identical to desktop DXC (G0/G1), the in-browser .mgfx must equal the desktop
// .mgfx, so the renders must match within the §6.1 tolerance.
//
// Steps:
//   (1) publish samples/ShaderFiddle.Web into ./.publish-faithful;
//   (2) OVERWRITE the published wwwroot/shadowdusk-dxc.js (Slang sample shim) with
//       the FAITHFUL product shim (src/ShadowDusk.Wasm/wwwroot/shadowdusk-dxc.js) and
//       drop the faithful dxcompiler.{js,wasm} into wwwroot/dxc/ — this is what makes
//       the "shadowdusk-dxc" [JSImport] module resolve to the FAITHFUL frontend for
//       the proof run. The Slang shim + wwwroot/slang/ are left intact (sample-only).
//   (3) ALSO overwrite the served OpenGL .mgfx with ShadowDusk's own bytes so mode-1
//       and the references-sd/ comparison stay same-source (mirrors publish-sample-sd).
//
// references-sd/ is rendered by publish-sample-sd.mjs (committed). If it is stale,
// re-run `node publish-sample-sd.mjs` first; this script reuses those references.
import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { compileCorpusSd } from './compile-corpus-sd.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const SIZE = 512;

function run(cmd, args, opts = {}) {
  console.log(`\n$ ${cmd} ${args.join(' ')}`);
  const r = spawnSync(cmd, args, { stdio: 'inherit', cwd: repoRoot, shell: false, ...opts });
  if (r.status !== 0) {
    console.error(`command failed (${r.status}): ${cmd} ${args.join(' ')}`);
    process.exit(r.status ?? 1);
  }
}

// 0. Build the ShadowDusk CLI so compileCorpusSd can drive it (for mode-1 + refs).
run('dotnet', ['build', '-c', 'Debug', path.join('src', 'ShadowDusk.Cli', 'ShadowDusk.Cli.csproj')]);

// 1. Publish the sample (index.html, the KNI WASM runtime, cat.jpg, golden corpus,
//    the Slang shim + slang/, and shadowdusk-spirv-cross.js + spirv-cross/).
const publishOut = path.join(__dirname, '.publish-faithful');
const wwwroot = path.join(publishOut, 'wwwroot');
const sampleCsproj = path.join('samples', 'ShaderFiddle.Web', 'ShaderFiddle.Web.csproj');
run('dotnet', ['publish', '-c', 'Release', sampleCsproj, '-o', publishOut]);

// 2. Swap the "shadowdusk-dxc" module to the FAITHFUL product shim + binaries.
//    The product home is src/ShadowDusk.Wasm/wwwroot/ (where M1 will package these
//    as static web assets). We copy from there so the proof runs the SHIPPING files.
const productWwwroot = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot');
const faithShim = path.join(productWwwroot, 'shadowdusk-dxc.js');
const faithDxcJs = path.join(productWwwroot, 'dxc', 'dxcompiler.js');
const faithDxcWasm = path.join(productWwwroot, 'dxc', 'dxcompiler.wasm');
for (const f of [faithShim, faithDxcJs, faithDxcWasm]) {
  if (!existsSync(f)) {
    console.error(`Faithful frontend file missing: ${f}`);
    console.error('Build/copy first: cp .wasm-build/dxc-wasm-out/dxcompiler.{js,wasm} src/ShadowDusk.Wasm/wwwroot/dxc/');
    process.exit(1);
  }
}
console.log('\n[publish-sample-faithful] swapping shadowdusk-dxc -> FAITHFUL DXC->WASM shim…');
copyFileSync(faithShim, path.join(wwwroot, 'shadowdusk-dxc.js'));            // overwrite Slang shim
const dxcDir = path.join(wwwroot, 'dxc');
mkdirSync(dxcDir, { recursive: true });
copyFileSync(faithDxcJs, path.join(dxcDir, 'dxcompiler.js'));
copyFileSync(faithDxcWasm, path.join(dxcDir, 'dxcompiler.wasm'));
console.log('  served wwwroot/shadowdusk-dxc.js  = FAITHFUL shim');
console.log('  served wwwroot/dxc/dxcompiler.{js,wasm} = faithful DXC->WASM module (17.4 MB)');

// 3. Overwrite served OpenGL .mgfx with ShadowDusk's OWN bytes (mode-1 baseline +
//    keeps the references-sd/ comparison same-source).
const mgfxDir = path.join(wwwroot, 'shaders', 'OpenGL');
console.log('\n[publish-sample-faithful] compiling corpus with ShadowDusk CLI into the served dir…');
const { ok, total, failures } = compileCorpusSd(mgfxDir);
if (ok !== total) {
  console.error(`[publish-sample-faithful] ShadowDusk failed to compile: ${failures.join(', ')}`);
  process.exit(1);
}

// 4. Ensure references-sd/ exists (rendered by publish-sample-sd.mjs from the SAME
//    ShadowDusk bytes). If missing, render them now so the proof is self-contained.
const refDir = path.join(__dirname, 'references-sd');
if (!existsSync(path.join(refDir, 'Grayscale.png'))) {
  console.log('\n[publish-sample-faithful] references-sd/ missing — rendering from ShadowDusk bytes…');
  const refProj = path.join('tests', 'ShadowDusk.BrowserTests', 'RefRenderer', 'RefRenderer.csproj');
  run('dotnet', ['build', '-c', 'Release', refProj]);
  const refDll = path.join(__dirname, 'RefRenderer', 'bin', 'Release', 'net8.0', 'RefRenderer.dll');
  const catPath = path.join(repoRoot, 'samples', 'ShaderViewer', 'Content', 'cat.jpg');
  if (!existsSync(refDll)) { console.error('RefRenderer.dll missing after build'); process.exit(1); }
  run('dotnet', [refDll, mgfxDir, catPath, refDir, String(SIZE)]);
} else {
  console.log('\n[publish-sample-faithful] reusing committed references-sd/ (ShadowDusk-byte renders).');
}

console.log('\n[publish-sample-faithful] done: .publish-faithful/wwwroot (faithful shim + ShadowDusk bytes) ready.');
console.log('  next: node run-harness.mjs --corpus=faithful');
