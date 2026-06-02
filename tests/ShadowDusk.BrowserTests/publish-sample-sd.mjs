// Phase 24 setup for the ShadowDusk-OWN-output validation path (companion to
// publish-sample.mjs, which uses the committed mgfxc goldens).
//
// (1) publish samples/ShaderFiddle.Web into ./.publish-sd, then OVERWRITE the
//     served wwwroot/shaders/OpenGL/*.mgfx with bytes compiled by ShadowDusk's
//     OWN CLI (the product output);
// (2) render the desktop DesktopGL reference PNGs from those SAME ShadowDusk
//     bytes, at the SAME fixed size, into ./references-sd.
//
// Then `node run-harness.mjs --corpus=sd` loads ShadowDusk's own .mgfx in real
// KNI WebGL and compares against the desktop render of the same bytes — proving
// OUR emitted GLSL (not just the golden) loads + renders in WebGL. See
// ROUNDEVEN-FIX.md: this is what would have caught the roundEven WebGL1 bug.
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync } from 'node:fs';
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

// 0. Build the ShadowDusk CLI so compileCorpusSd can drive it.
run('dotnet', ['build', '-c', 'Debug', path.join('src', 'ShadowDusk.Cli', 'ShadowDusk.Cli.csproj')]);

// 1. Publish the sample (brings index.html, the KNI WASM runtime, cat.jpg, and a
//    full golden corpus) into .publish-sd.
const publishOut = path.join(__dirname, '.publish-sd');
const sampleCsproj = path.join('samples', 'ShaderFiddle.Web', 'ShaderFiddle.Web.csproj');
run('dotnet', ['publish', '-c', 'Release', sampleCsproj, '-o', publishOut]);

// 1b. Overwrite the served OpenGL .mgfx with ShadowDusk's OWN compiled output.
const mgfxDir = path.join(publishOut, 'wwwroot', 'shaders', 'OpenGL');
console.log('\n[publish-sample-sd] compiling corpus with ShadowDusk CLI into the served dir…');
const { ok, total, failures } = compileCorpusSd(mgfxDir);
if (ok !== total) {
  console.error(`[publish-sample-sd] ShadowDusk failed to compile: ${failures.join(', ')}`);
  process.exit(1);
}

// 2. Render references from those SAME ShadowDusk bytes (NOT the goldens), so the
//    WebGL-vs-DesktopGL comparison stays same-bytes for ShadowDusk's own output.
const refProj = path.join('tests', 'ShadowDusk.BrowserTests', 'RefRenderer', 'RefRenderer.csproj');
run('dotnet', ['build', '-c', 'Release', refProj]);
const refDll = path.join(__dirname, 'RefRenderer', 'bin', 'Release', 'net8.0', 'RefRenderer.dll');
const catPath = path.join(repoRoot, 'samples', 'ShaderViewer', 'Content', 'cat.jpg');
const refDir = path.join(__dirname, 'references-sd');
if (!existsSync(refDll)) { console.error('RefRenderer.dll missing after build'); process.exit(1); }
run('dotnet', [refDll, mgfxDir, catPath, refDir, String(SIZE)]);

console.log('\n[publish-sample-sd] done: .publish-sd/wwwroot (ShadowDusk bytes) + references-sd/ ready.');
console.log('  next: node run-harness.mjs --corpus=sd');
