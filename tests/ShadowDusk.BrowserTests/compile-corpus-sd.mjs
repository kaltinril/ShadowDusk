// Compile the 10 OpenGL corpus shaders with ShadowDusk's OWN CLI into a target
// directory, so the Phase 24 harness can render ShadowDusk's actual product
// output (instead of the committed mgfxc goldens) in KNI WebGL. This is the
// ShadowDusk-own-output validation building block (see ROUNDEVEN-FIX.md): it is
// how the harness proves OUR emitted GLSL — not just the golden — loads + renders
// in real KNI WebGL, so a "our output != loadable in WebGL" bug (e.g. roundEven)
// can't hide behind the golden corpus again.
//
// Usage (standalone): node compile-corpus-sd.mjs [outDir]
//   [outDir] defaults to .publish/wwwroot/shaders/OpenGL (what the harness serves).
// Or import { compileCorpusSd, SHADERS } and call it from another script.
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync, mkdirSync } from 'node:fs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');

export const SHADERS = [
  'Grayscale', 'Invert', 'TintShader', 'Sepia', 'Saturate',
  'Pixelated', 'Scanlines', 'Fading', 'Dots', 'Dissolve',
  // Issue #107 WebGL render proof. SPIRV-Cross emits a one-shot `do {…} while(false)`
  // for the nested-if early-return helper; GLSL ES 1.00 (WebGL1 / KNI Reach) does not
  // guarantee do-while, so pre-fix this effect compiled + loaded on desktop yet FAILED
  // TO LOAD in WebGL. MonoGameGlslRewriter Rule 9 lowers it to a WebGL1-safe bounded
  // for-loop. Including it here renders ShadowDusk's OWN bytes in real KNI WebGL,
  // proving the lowered GLSL loads + renders (the open rung the desktop gate can't reach).
  'Issue107DoWhile',
];

// Source-relative path for any corpus shader NOT at tests/fixtures/shaders/<name>.fx.
// (The 10 original corpus shaders live at the top level; regression fixtures like the
// #107 repro live under examples/.)
const SOURCE_OVERRIDES = {
  Issue107DoWhile: path.join('examples', 'Issue107DoWhile.fx'),
};

// Phase 35 KNIFX-container WebGL proof: compiled with `--target-runtime kni-knifx` (KNIF
// signature + __KNIFX__ defined) instead of `/Profile:OpenGL`. Written under the SAME
// shaders/OpenGL/<name>.mgfx URL the sample's TestLoadCorpus fetches — KNI's Effect loader
// dispatches on the 4-byte container signature ("KNIF" vs "MGFX"), NOT the filename, so
// loading one of these through TestLoadCorpus proves the KNIFX container loads in REAL KNI
// WebGL. The multi-backend KnifxWriter advertises the whole GL family and gives GLES/WebGL a
// ShaderVersion(0,0) raw-GLSL body, so KNI's runtime converts to ES at load (the proven v10
// path). ExKnifxMacro renders solid RED iff __KNIFX__ was defined, so run-harness.mjs asserts
// the rendered quad is red — proving the container loads AND the __KNIFX__ branch fired.
// (RefRenderer is MonoGame and cannot load KNIF, so these are NOT in its list and have no PNG
// reference; the harness uses a reference-free solid-color assert.)
export const KNIFX_PROOFS = {
  ExKnifxMacro: path.join('examples', 'ExKnifxMacro.fx'),
};

/**
 * Compile every corpus shader with ShadowDusk's own CLI into outDir.
 * @returns {{ ok: number, total: number, failures: string[] }}
 */
export function compileCorpusSd(outDir) {
  mkdirSync(outDir, { recursive: true });

  // The CLI assembly is named ShadowDuskCLI (csproj <AssemblyName>, 0.1.1 rename),
  // not after the project file.
  const cliDll = path.join(
    repoRoot, 'src', 'ShadowDusk.Cli', 'bin', 'Debug', 'net8.0', 'ShadowDuskCLI.dll');
  if (!existsSync(cliDll)) {
    throw new Error(
      `ShadowDuskCLI.dll not found at ${cliDll}. Build it first: ` +
      `dotnet build src/ShadowDusk.Cli`);
  }

  // OpenGL corpus (incl. the #107 fixture) compiled with /Profile:OpenGL, plus the KNIFX
  // container proofs compiled with --target-runtime kni-knifx.
  const jobs = [
    ...SHADERS.map((name) => ({
      name, rel: SOURCE_OVERRIDES[name] ?? (name + '.fx'), cliArgs: ['/Profile:OpenGL'],
    })),
    ...Object.entries(KNIFX_PROOFS).map(([name, rel]) => ({
      name, rel, cliArgs: ['--target-runtime', 'kni-knifx'],
    })),
  ];

  let ok = 0;
  const failures = [];
  for (const job of jobs) {
    const src = path.join(repoRoot, 'tests', 'fixtures', 'shaders', job.rel);
    const dst = path.join(outDir, job.name + '.mgfx');
    const r = spawnSync('dotnet', [cliDll, src, dst, ...job.cliArgs],
      { stdio: 'inherit', cwd: repoRoot, shell: false });
    if (r.status === 0) { ok++; console.log(`  [OK]   ${job.name} -> ${dst}`); }
    else { failures.push(job.name); console.error(`  [FAIL] ${job.name} (exit ${r.status})`); }
  }
  console.log(`[compile-corpus-sd] ${ok}/${jobs.length} compiled into ${outDir}`);
  return { ok, total: jobs.length, failures };
}

// Standalone entry point.
if (fileURLToPath(import.meta.url) === path.resolve(process.argv[1] ?? '')) {
  const outDir = process.argv[2]
    ? path.resolve(process.argv[2])
    : path.join(__dirname, '.publish', 'wwwroot', 'shaders', 'OpenGL');
  const { ok, total } = compileCorpusSd(outDir);
  process.exit(ok === total ? 0 : 1);
}
