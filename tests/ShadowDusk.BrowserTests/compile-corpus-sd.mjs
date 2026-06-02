// Investigation helper (NOT part of the committed harness): compile the 10
// OpenGL corpus shaders with ShadowDusk's OWN CLI into a target directory, so the
// Phase 24 harness can render ShadowDusk's actual product output (instead of the
// committed mgfxc goldens) in KNI WebGL. Used to A/B the precision-qualifier fix.
//
// Usage: node compile-corpus-sd.mjs <outDir>
//   <outDir> defaults to .publish/wwwroot/shaders/OpenGL (what the harness serves).
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync, mkdirSync } from 'node:fs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');

const SHADERS = [
  'Grayscale', 'Invert', 'TintShader', 'Sepia', 'Saturate',
  'Pixelated', 'Scanlines', 'Fading', 'Dots', 'Dissolve',
];

const outDir = process.argv[2]
  ? path.resolve(process.argv[2])
  : path.join(__dirname, '.publish', 'wwwroot', 'shaders', 'OpenGL');
mkdirSync(outDir, { recursive: true });

const cliDll = path.join(repoRoot, 'src', 'ShadowDusk.Cli', 'bin', 'Debug', 'net8.0', 'ShadowDusk.Cli.dll');
if (!existsSync(cliDll)) {
  console.error(`ShadowDusk.Cli.dll not found at ${cliDll}. Build it first: dotnet build src/ShadowDusk.Cli`);
  process.exit(1);
}

let ok = 0;
for (const name of SHADERS) {
  const src = path.join(repoRoot, 'tests', 'fixtures', 'shaders', name + '.fx');
  const dst = path.join(outDir, name + '.mgfx');
  const r = spawnSync('dotnet', [cliDll, src, dst, '/Profile:OpenGL'],
    { stdio: 'inherit', cwd: repoRoot, shell: false });
  if (r.status === 0) { ok++; console.log(`  [OK]   ${name} -> ${dst}`); }
  else console.error(`  [FAIL] ${name} (exit ${r.status})`);
}
console.log(`[compile-corpus-sd] ${ok}/${SHADERS.length} compiled into ${outDir}`);
process.exit(ok === SHADERS.length ? 0 : 1);
