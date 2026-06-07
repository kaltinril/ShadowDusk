// G2 — DIAGNOSTICS gate for the FAITHFUL `shadowdusk-dxc` shim (Phase 38).
//
// G0/G1 prove the SUCCESS path (byte-identical SPIR-V). THIS test proves the
// FAILURE path: when DXC rejects bad HLSL, the shim must THROW an Error whose
// message carries DXC's VERBATIM diagnostics (file:line:col: error: message) —
// NOT the opaque "[object WebAssembly.Exception]" that the pre-Phase-38 glue
// produced under -fwasm-exceptions. That readable text is what the C# WASM seam
// (JsShaderBackends) runs through DxcDiagnosticReformatter to get line/column,
// which is what lets a downstream in-browser editor squiggle the offending line.
//
// It also prints the raw message so we can confirm the format matches the
// reformatter's regex (^<file>:<line>:<col>: <severity>: <msg>$). If DXC emitted
// no filename, the C# regex would miss the line — this test surfaces that early.
//
// Run (AFTER copying the relinked module into the package wwwroot):
//   node .wasm-build/node-test-dxc-diagnostics.mjs

import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..');
const shimJs = join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'shadowdusk-dxc.js');
const corpusDir = join(here, 'corpus-spirv');

if (!existsSync(shimJs)) { console.error(`Shim not found: ${shimJs}`); process.exit(2); }

const shim = await import('file://' + shimJs.replace(/\\/g, '/'));
await shim.ensureReady();

// Base case: a real corpus shader (valid) + its exact desktop args.
const baseHlsl = readFileSync(join(corpusDir, 'Grayscale.hlsl'), 'utf8');
const args = JSON.parse(readFileSync(join(corpusDir, 'Grayscale.args.json'), 'utf8'));

// Positive control — the unbroken shader must still compile.
let okFailed = false;
try {
    const spirv = shim.compileToSpirv(baseHlsl, args);
    console.log(`[control] OK — valid shader compiled (${spirv.length} bytes SPIR-V)`);
} catch (e) {
    okFailed = true;
    console.error(`[control] UNEXPECTED THROW on a valid shader: ${e && e.message ? e.message : e}`);
}

// Inject a guaranteed compile error: an undeclared identifier inside the body.
// Insert it right after the entry function's opening brace so it carries a line.
const marker = '{';
const idx = baseHlsl.indexOf(marker, baseHlsl.indexOf('MainPS'));
const broken = idx >= 0
    ? baseHlsl.slice(0, idx + 1) + '\n    float4 bad = totally_undeclared_identifier_xyz;\n' + baseHlsl.slice(idx + 1)
    : baseHlsl + '\nthis is not valid hlsl;\n';

let diagFailed = false;
let threw = false;
let message = '';
try {
    shim.compileToSpirv(broken, args);
} catch (e) {
    threw = true;
    message = e && e.message ? e.message : String(e);
}

console.log('\n--- raw diagnostic message from the shim ---');
console.log(message || '(none)');
console.log('--- end ---\n');

if (!threw) {
    diagFailed = true;
    console.error('[diag] FAIL — broken shader did NOT throw (no diagnostics surfaced).');
} else {
    if (/\[object WebAssembly\.Exception\]/i.test(message)) {
        diagFailed = true;
        console.error('[diag] FAIL — opaque WebAssembly.Exception leaked (the Phase-38 bug is NOT fixed).');
    }
    const hasError = /error/i.test(message);
    const hasLineCol = /:\d+:\d+:/.test(message);   // clang-style file:line:col:
    if (hasError && hasLineCol) {
        console.log('[diag] OK — diagnostics carry "error" and a file:line:col location.');
    } else {
        diagFailed = true;
        console.error(`[diag] FAIL — message missing ${!hasError ? '"error" ' : ''}${!hasLineCol ? 'line:col ' : ''}` +
            '(the C# reformatter needs the file:line:col: shape to extract a line).');
    }
}

if (okFailed || diagFailed) {
    console.error('\nG2 diagnostics gate FAILED.');
    process.exit(1);
}
console.log('\nG2 diagnostics gate PASSED — bad HLSL surfaces verbatim line/column diagnostics.');
