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

// NOTE — a KNIFX-container WebGL proof (compile ExKnifxMacro with --target-runtime
// kni-knifx, load via the signature-sniffing KNI Effect path, assert the __KNIFX__ red
// branch) was prototyped here and EMPIRICALLY confirmed a KNOWN, code-documented gap:
// KNI WebGL rejects ShadowDusk's OpenGL-backend KNIFX ("Effect profile 'DirectX_11' is
// not compatible with the graphics backend 'WebGL'") because the GL ShaderCode carries
// only the desktop GLSL-1.10 entry, not converted GLES/WebGL ES entries (see
// KnifxWriter.cs + plan/PHASE-35). The seamless DEFAULT MGFX v10 DOES load + render in
// KNI WebGL (the corpus above + #107), so KNI-web consumers are covered by the default.
// The KNIFX-web refinement is tracked in the validation matrix, not gated here.

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

  let ok = 0;
  const failures = [];
  for (const name of SHADERS) {
    const rel = SOURCE_OVERRIDES[name] ?? (name + '.fx');
    const src = path.join(repoRoot, 'tests', 'fixtures', 'shaders', rel);
    const dst = path.join(outDir, name + '.mgfx');
    const r = spawnSync('dotnet', [cliDll, src, dst, '/Profile:OpenGL'],
      { stdio: 'inherit', cwd: repoRoot, shell: false });
    if (r.status === 0) { ok++; console.log(`  [OK]   ${name} -> ${dst}`); }
    else { failures.push(name); console.error(`  [FAIL] ${name} (exit ${r.status})`); }
  }
  console.log(`[compile-corpus-sd] ${ok}/${SHADERS.length} compiled into ${outDir}`);
  return { ok, total: SHADERS.length, failures };
}

// Standalone entry point.
if (fileURLToPath(import.meta.url) === path.resolve(process.argv[1] ?? '')) {
  const outDir = process.argv[2]
    ? path.resolve(process.argv[2])
    : path.join(__dirname, '.publish', 'wwwroot', 'shaders', 'OpenGL');
  const { ok, total } = compileCorpusSd(outDir);
  process.exit(ok === total ? 0 : 1);
}
