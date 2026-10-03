// Issue #349 - REAL-BROWSER gate for the HLSL -> SkSL converter (SkslConverter, issue #197).
//
// Headless Chromium boots the published ShaderFiddle.Web sample (the real .NET-browser runtime)
// and drives WasmShaderCompiler.ConvertToSksl through the [JSInvokable] TestSyncConvertSksl
// hook, with the browser's WASM DXC + SPIRV-Cross injected into the converter. Checks:
//   1. COLD: ConvertToSksl before InitializeAsync fails with the clear SD1903 error.
//   2. After InitializeAsync, Gum's Grayscale.fx converts (COLOR0 becomes ShadowDusk_Color by default),
//      and the SkSL text is BYTE-IDENTICAL to tests/fixtures/golden/sksl/Grayscale.sksl, the
//      same file the desktop test SkslConverterTests pins to the desktop converter's output.
//      So browser == golden == desktop.
//   3. An interpolant other than TEXCOORD0/COLOR0 is refused in the browser too: SD0611 naming it.
//
// No Skia in the browser: byte-identity to the desktop text is the bar (the desktop
// SkslSkiaEvidenceTests already prove Skia accepts that text).
//
// Usage:  cd tests/ShadowDusk.BrowserTests
//         node browser-sksl-gate.mjs [--skip-publish]
// --skip-publish reuses .publish-vkd3d/ (what browser-vkd3d-gate.mjs publishes), so CI runs
// the two gates with one publish. Needs the .NET SDK + wasm-tools workload, Node 18+,
// Playwright Chromium, and the restored DXC/SPIRV-Cross wasm (tools/restore.*).

import { chromium } from 'playwright';
import { startServer } from './static-server.mjs';
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const fxPath = path.join(repoRoot, 'tests', 'fixtures', 'shaders', 'third-party', 'Gum', 'MonoGameInCode-Grayscale.fx');
const goldenPath = path.join(repoRoot, 'tests', 'fixtures', 'golden', 'sksl', 'Grayscale.sksl');
const dxcWasm = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'dxc', 'dxcompiler.wasm');
const publishOut = path.join(__dirname, '.publish-vkd3d');
const publishRoot = path.join(publishOut, 'wwwroot');

function fail(reason) {
  console.error(`[sksl browser gate] FAIL - ${reason}`);
  process.exit(1);
}

if (!existsSync(dxcWasm))
  fail(`${dxcWasm} is not restored (run tools/restore.sh / restore.ps1).`);

// Same normalization the vkd3d gate uses for .NET-read sources: drop the BOM, LF endings.
const normalize = (t) => t.replace(/^\u{FEFF}/u, '').replace(/\r\n/g, '\n');
const source = normalize(readFileSync(fxPath, 'utf8'));
const golden = readFileSync(goldenPath, 'utf8');

if (process.argv.includes('--skip-publish') && existsSync(path.join(publishRoot, 'index.html'))) {
  console.log(`[sksl browser gate] --skip-publish: reusing ${publishRoot}`);
} else {
  const sampleCsproj = path.join('samples', 'ShaderFiddle.Web', 'ShaderFiddle.Web.csproj');
  console.log(`\n$ dotnet publish -c Release ${sampleCsproj} -o ${publishOut}`);
  const r = spawnSync('dotnet', ['publish', '-c', 'Release', sampleCsproj, '-o', publishOut],
    { stdio: 'inherit', cwd: repoRoot, shell: false });
  if (r.status !== 0) fail(`dotnet publish exited ${r.status}.`);
}

const srv = await startServer(publishRoot);
const browser = await chromium.launch({
  headless: true,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--ignore-gpu-blocklist', '--enable-unsafe-swiftshader'],
});

const failures = [];
const check = (name, ok, note) => {
  console.log(`  [${ok ? 'OK' : 'FAIL'}] ${name} - ${note}`);
  if (!ok) failures.push(`${name}: ${note}`);
};

try {
  const page = await browser.newPage({ viewport: { width: 900, height: 700 } });
  page.setDefaultTimeout(180000);
  page.on('pageerror', (e) => console.log(`  [pageerror] ${e.message}`));
  await page.goto(`${srv.url}/`, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(
    () => typeof window.theInstance !== 'undefined' && window.theInstance !== null,
    { timeout: 120000 });

  const convert = (varyings, src = source) => page.evaluate(
    async ({ src, varyings }) =>
      await window.theInstance.invokeMethodAsync('TestSyncConvertSksl', src, varyings, 'Grayscale.fx'),
    { src, varyings });

  // 1. COLD, before anything has loaded the DXC module.
  const cold = await convert('');
  check('cold ConvertToSksl', typeof cold === 'string' && cold.startsWith('ERR:') && cold.includes('SD1903'),
    `expected SD1903, got: ${String(cold).slice(0, 200)}`);

  // 2. Warm: initialize, then convert and compare against the desktop golden.
  const init = await page.evaluate(async () => await window.theInstance.invokeMethodAsync('TestInitializeCompiler'));
  check('InitializeAsync', init === 'OK', String(init).slice(0, 200));

  const warm = await convert('');
  if (typeof warm !== 'string' || !warm.startsWith('OK:')) {
    check('warm ConvertToSksl (COLOR0 by default)', false, `expected OK, got: ${String(warm).slice(0, 400)}`);
  } else {
    const sksl = warm.slice(3);
    check('browser SkSL byte-identical to the desktop golden', sksl === golden,
      sksl === golden ? `${sksl.length} chars, identical`
        : `DIFFERS from golden.\n--- browser ---\n${sksl}\n--- golden ---\n${golden}`);
  }

  // 3. Refusal path: an interpolant SkSL cannot supply (not TEXCOORD0, not COLOR0) is refused by name.
  const extraInterpolant = `float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0, float4 extra : TEXCOORD1) : SV_Target
{ return extra; }
technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }`;
  const refused = await convert('', extraInterpolant);
  check('refusal of a non-COLOR0 interpolant',
    typeof refused === 'string' && refused.startsWith('ERR:') && refused.includes('SD0611') && refused.includes('TEXCOORD1'),
    `expected SD0611 naming TEXCOORD1, got: ${String(refused).slice(0, 300)}`);
} finally {
  await browser.close();
  await srv.close();
}

if (failures.length > 0) {
  console.error(`[sksl browser gate] FAIL - ${failures.length} failure(s):`);
  for (const f of failures) console.error('  - ' + f);
  process.exit(1);
}
console.log('SKSL BROWSER GATE PASSED: cold SD1903, SkSL byte-identical to the desktop golden, TEXCOORD1 refusal by name.');
