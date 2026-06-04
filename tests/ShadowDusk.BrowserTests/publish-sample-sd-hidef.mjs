// Phase 33 (issue #7) setup for the KNI HiDef / WebGL2 validation path.
//
// HiDef is NOT a different .mgfx — KNI converts the same legacy GLSL blob to
// GLSL ES 3.00 at load time. So this path reuses EXACTLY the ShadowDusk-own
// publish + references the --corpus=sd path uses (.publish-sd/ + references-sd/);
// only the runtime PROFILE differs (the harness passes ?profile=hidef). This
// script therefore just ensures the shared SD artifacts exist:
//   (1) publish samples/ShaderFiddle.Web into ./.publish-sd, then OVERWRITE the
//       served wwwroot/shaders/OpenGL/*.mgfx with bytes compiled by ShadowDusk's
//       OWN CLI (the product output);
//   (2) render the desktop DesktopGL reference PNGs from those SAME ShadowDusk
//       bytes into ./references-sd (the Reach baseline the HiDef render compares
//       against — a divergence then isolates "KNI's ES-3.00 conversion of our
//       bytes" from the compiler).
//
// Then: node run-harness.mjs --corpus=sd-hidef
//   - BEFORE the MonoGameGlslRewriter #define fix: RED — Grayscale fails to load
//     in KNI HiDef/WebGL2 (`'gl_FragColor' : undeclared identifier`) →
//     RESULTS-SD-HIDEF-REPRO.md.
//   - AFTER the fix: GREEN — all 10 load + render → RESULTS-SD-HIDEF.md.
//
// If .publish-sd/ + references-sd/ already exist (from a prior `node
// publish-sample-sd.mjs`), pass --skip-publish to reuse them and save the slow
// Blazor-WASM publish.
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync } from 'node:fs';
import { compileCorpusSd } from './compile-corpus-sd.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const SIZE = 512;
const SKIP_PUBLISH = process.argv.includes('--skip-publish');

function run(cmd, args, opts = {}) {
  console.log(`\n$ ${cmd} ${args.join(' ')}`);
  const r = spawnSync(cmd, args, { stdio: 'inherit', cwd: repoRoot, shell: false, ...opts });
  if (r.status !== 0) {
    console.error(`command failed (${r.status}): ${cmd} ${args.join(' ')}`);
    process.exit(r.status ?? 1);
  }
}

const publishOut = path.join(__dirname, '.publish-sd');
const mgfxDir = path.join(publishOut, 'wwwroot', 'shaders', 'OpenGL');
const refDir = path.join(__dirname, 'references-sd');

if (SKIP_PUBLISH && existsSync(path.join(publishOut, 'wwwroot', 'index.html')) && existsSync(refDir)) {
  console.log('[publish-sample-sd-hidef] --skip-publish: reusing existing .publish-sd + references-sd.');
  console.log('  next: node run-harness.mjs --corpus=sd-hidef');
  process.exit(0);
}

// 0. Build the ShadowDusk CLI so compileCorpusSd can drive it.
run('dotnet', ['build', '-c', 'Debug', path.join('src', 'ShadowDusk.Cli', 'ShadowDusk.Cli.csproj')]);

// 1. Publish the sample (index.html w/ the ?test + ?profile hooks, the KNI WASM
//    runtime, cat.jpg) into .publish-sd.
const sampleCsproj = path.join('samples', 'ShaderFiddle.Web', 'ShaderFiddle.Web.csproj');
run('dotnet', ['publish', '-c', 'Release', sampleCsproj, '-o', publishOut]);

// 1b. Overwrite the served OpenGL .mgfx with ShadowDusk's OWN compiled output.
console.log('\n[publish-sample-sd-hidef] compiling corpus with ShadowDusk CLI into the served dir…');
const { ok, total, failures } = compileCorpusSd(mgfxDir);
if (ok !== total) {
  console.error(`[publish-sample-sd-hidef] ShadowDusk failed to compile: ${failures.join(', ')}`);
  process.exit(1);
}

// 2. Render Reach desktop references from those SAME ShadowDusk bytes.
const refProj = path.join('tests', 'ShadowDusk.BrowserTests', 'RefRenderer', 'RefRenderer.csproj');
run('dotnet', ['build', '-c', 'Release', refProj]);
const refDll = path.join(__dirname, 'RefRenderer', 'bin', 'Release', 'net8.0', 'RefRenderer.dll');
const catPath = path.join(repoRoot, 'samples', 'ShaderViewer', 'Content', 'cat.jpg');
if (!existsSync(refDll)) { console.error('RefRenderer.dll missing after build'); process.exit(1); }
run('dotnet', [refDll, mgfxDir, catPath, refDir, String(SIZE)]);

console.log('\n[publish-sample-sd-hidef] done: .publish-sd/wwwroot (ShadowDusk bytes) + references-sd/ ready.');
console.log('  next: node run-harness.mjs --corpus=sd-hidef');
