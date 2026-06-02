// Phase 24 setup: (1) publish samples/ShaderFiddle.Web into ./.publish, and
// (2) render the desktop DesktopGL reference PNGs from the SAME corpus .mgfx the
// browser loads, at the SAME fixed size, into ./references. Run before the
// harness. Self-contained so Phase 30 CI can call it unattended.
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync } from 'node:fs';

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

const publishOut = path.join(__dirname, '.publish');
const sampleCsproj = path.join('samples', 'ShaderFiddle.Web', 'ShaderFiddle.Web.csproj');
run('dotnet', ['publish', '-c', 'Release', sampleCsproj, '-o', publishOut]);

// Render references from the published corpus bytes (identical to what the
// browser fetches), keeping browser and reference perfectly in sync.
const refProj = path.join('tests', 'ShadowDusk.BrowserTests', 'RefRenderer', 'RefRenderer.csproj');
run('dotnet', ['build', '-c', 'Release', refProj]);
const refDll = path.join(__dirname, 'RefRenderer', 'bin', 'Release', 'net8.0', 'RefRenderer.dll');
const mgfxDir = path.join(publishOut, 'wwwroot', 'shaders', 'OpenGL');
const catPath = path.join(repoRoot, 'samples', 'ShaderViewer', 'Content', 'cat.jpg');
const refDir = path.join(__dirname, 'references');
if (!existsSync(refDll)) { console.error('RefRenderer.dll missing after build'); process.exit(1); }
run('dotnet', [refDll, mgfxDir, catPath, refDir, String(SIZE)]);

console.log('\n[publish-sample] done: .publish/wwwroot + references/ ready.');
