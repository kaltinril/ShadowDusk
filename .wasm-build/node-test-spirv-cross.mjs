// Node parity test for the in-browser SPIR-V -> GLSL backend.
//
// Loads the SAME ES module the browser loads
// (samples/ShaderFiddle.Web/wwwroot/shadowdusk-spirv-cross.js -> the emscripten
// WASM build of SPIRV-Cross) and asserts that, for fixtures produced by the REAL
// desktop pipeline (spirv-probe), its transpileToGlsl output matches the desktop
// SpirvCrossGlslTranspiler output BYTE-FOR-BYTE, with the exact option values the
// [JSImport] contract passes (flipVertexY=true, fixupDepthConvention=true,
// glslVersion=140, glslEs=false, vulkanSemantics=false).
//
// Run:  node .wasm-build/node-test-spirv-cross.mjs

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..');
const moduleUrl = 'file://' + join(repo, 'samples', 'ShaderFiddle.Web', 'wwwroot', 'shadowdusk-spirv-cross.js').replace(/\\/g, '/');
const fixturesDir = join(here, 'fixtures');

const { transpileToGlsl } = await import(moduleUrl);

const cases = ['simple', 'textured'];
let failed = 0;

for (const name of cases) {
    const spirv = new Uint8Array(readFileSync(join(fixturesDir, `${name}.spv`)));
    const expected = readFileSync(join(fixturesDir, `${name}.desktop-glsl.txt`), 'utf8');

    let actual;
    try {
        // EXACT option values from JsShaderBackends.cs / the [JSImport] call.
        actual = transpileToGlsl(spirv, true, true, 140, false, false);
    } catch (e) {
        console.error(`[${name}] THREW: ${e.message}`);
        failed++;
        continue;
    }

    if (actual === expected) {
        console.log(`[${name}] OK — ${actual.length} chars, byte-identical to desktop`);
    } else {
        failed++;
        console.error(`[${name}] MISMATCH`);
        console.error(`  expected (${expected.length} chars):\n${indent(expected)}`);
        console.error(`  actual   (${actual.length} chars):\n${indent(actual)}`);
        console.error('  first diff at: ' + firstDiff(expected, actual));
    }
}

// Negative test: garbage SPIR-V must throw (surfaces as SD1901).
try {
    transpileToGlsl(new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]), true, true, 140, false, false);
    console.error('[negative] FAIL — expected a throw on invalid SPIR-V');
    failed++;
} catch (e) {
    console.log(`[negative] OK — invalid SPIR-V threw: ${truncate(e.message, 80)}`);
}

function indent(s) { return s.split('\n').map(l => '    | ' + l).join('\n'); }
function truncate(s, n) { return s.length > n ? s.slice(0, n) + '…' : s; }
function firstDiff(a, b) {
    const n = Math.min(a.length, b.length);
    for (let i = 0; i < n; i++) if (a[i] !== b[i]) return `index ${i} (${JSON.stringify(a[i])} vs ${JSON.stringify(b[i])})`;
    return a.length === b.length ? 'none' : `length ${a.length} vs ${b.length}`;
}

if (failed > 0) {
    console.error(`\n${failed} check(s) failed.`);
    process.exit(1);
}
console.log('\nALL CHECKS PASSED — WASM SPIRV-Cross is byte-identical to desktop.');
