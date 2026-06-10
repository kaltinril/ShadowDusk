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
];

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
    const src = path.join(repoRoot, 'tests', 'fixtures', 'shaders', name + '.fx');
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
