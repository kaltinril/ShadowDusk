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
//   (2) Phase 23 M1: the sample now consumes the ShadowDusk.Wasm PACKAGE, which
//       self-registers its faithful "shadowdusk-dxc" module from the package's static
//       web assets at _content/ShadowDusk.Wasm/ (NOT the sample's own wwwroot root).
//       The package wwwroot already ships the FAITHFUL shim + dxcompiler.{js,wasm}, so
//       after publish those are already at wwwroot/_content/ShadowDusk.Wasm/. This step
//       re-asserts them from src/ShadowDusk.Wasm/wwwroot/ (idempotent) so the proof
//       definitively runs the SHIPPING faithful files even if the restored .wasm
//       differed. The Slang shim at wwwroot/shadowdusk-dxc.js is left intact but is
//       never registered (sample-only).
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

// 2. Re-assert the FAITHFUL product shim + binaries into the package's served static
//    web assets at _content/ShadowDusk.Wasm/ — the exact path ShadowDusk.Wasm
//    self-registers against (WasmModuleRegistration: ../_content/ShadowDusk.Wasm/<f>).
//    The product home is src/ShadowDusk.Wasm/wwwroot/. We also drop the SPIRV-Cross
//    module (the package now ships it too).
const productWwwroot = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot');
const faithShim = path.join(productWwwroot, 'shadowdusk-dxc.js');
const faithDxcJs = path.join(productWwwroot, 'dxc', 'dxcompiler.js');
const faithDxcWasm = path.join(productWwwroot, 'dxc', 'dxcompiler.wasm');
const spvShim = path.join(productWwwroot, 'shadowdusk-spirv-cross.js');
const spvJs = path.join(productWwwroot, 'spirv-cross', 'spirv-cross.js');
const spvWasm = path.join(productWwwroot, 'spirv-cross', 'spirv-cross.wasm');
for (const f of [faithShim, faithDxcJs, faithDxcWasm, spvShim, spvJs, spvWasm]) {
  if (!existsSync(f)) {
    console.error(`Faithful frontend file missing: ${f}`);
    console.error('Build/copy first: run tools/restore.ps1 (copies dxcompiler.wasm into src/ShadowDusk.Wasm/wwwroot/dxc/)');
    process.exit(1);
  }
}
const contentRoot = path.join(wwwroot, '_content', 'ShadowDusk.Wasm');
console.log('\n[publish-sample-faithful] re-asserting FAITHFUL frontend into _content/ShadowDusk.Wasm/…');
mkdirSync(path.join(contentRoot, 'dxc'), { recursive: true });
mkdirSync(path.join(contentRoot, 'spirv-cross'), { recursive: true });
copyFileSync(faithShim, path.join(contentRoot, 'shadowdusk-dxc.js'));
copyFileSync(faithDxcJs, path.join(contentRoot, 'dxc', 'dxcompiler.js'));
copyFileSync(faithDxcWasm, path.join(contentRoot, 'dxc', 'dxcompiler.wasm'));
copyFileSync(spvShim, path.join(contentRoot, 'shadowdusk-spirv-cross.js'));
copyFileSync(spvJs, path.join(contentRoot, 'spirv-cross', 'spirv-cross.js'));
copyFileSync(spvWasm, path.join(contentRoot, 'spirv-cross', 'spirv-cross.wasm'));
console.log('  served _content/ShadowDusk.Wasm/shadowdusk-dxc.js       = FAITHFUL shim');
console.log('  served _content/ShadowDusk.Wasm/dxc/dxcompiler.{js,wasm} = faithful DXC->WASM (17.4 MB)');
console.log('  served _content/ShadowDusk.Wasm/shadowdusk-spirv-cross.js + spirv-cross/ = SPIRV-Cross WASM');

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
