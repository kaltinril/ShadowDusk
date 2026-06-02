// G1 — byte-identity gate for the FAITHFUL `shadowdusk-dxc` SHIM (Phase 23 M2).
//
// node-test-dxc-wasm.mjs (G0) drives the raw emscripten module directly. THIS test
// (G1) drives the PRODUCT SHIM — src/ShadowDusk.Wasm/wwwroot/shadowdusk-dxc.js —
// through its real [JSImport] contract surface:
//     await ensureReady();              // lazy load + instantiate, idempotent
//     const spirv = compileToSpirv(hlsl, args);   // synchronous, args VERBATIM
// and asserts that for every corpus shader the shim's SPIR-V EQUALS the desktop DXC
// SPIR-V byte-for-byte (.wasm-build/corpus-spirv/{name}.spv). Passing G1 proves the
// shim wraps the module correctly (lazy ensureReady, verbatim args, byte copy-out,
// magic-word check) — not just that the module is good (G0). The shim imports
// ./dxc/dxcompiler.js relative to its own URL; we copy the built dxcompiler.{js,wasm}
// into wwwroot/dxc/ exactly as M1 will package it, so this runs against the shipping
// layout. ensureReady() is called twice to exercise idempotency.
//
// Run:  node .wasm-build/node-test-dxc-shim.mjs

import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..');
const shimJs = join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'shadowdusk-dxc.js');
const shimDxcJs = join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'dxc', 'dxcompiler.js');
const corpusDir = join(here, 'corpus-spirv');

if (!existsSync(shimJs)) {
    console.error(`Faithful shim not found: ${shimJs}`);
    process.exit(2);
}
if (!existsSync(shimDxcJs)) {
    console.error(`Faithful DXC module not co-located with the shim: ${shimDxcJs}`);
    console.error('Copy it:  cp .wasm-build/dxc-wasm-out/dxcompiler.{js,wasm} src/ShadowDusk.Wasm/wwwroot/dxc/');
    process.exit(2);
}
if (!existsSync(corpusDir)) {
    console.error(`Corpus ground truth not found: ${corpusDir}`);
    process.exit(2);
}

// Import the SHIM exactly as the browser host's [JSImport] module would.
const shim = await import('file://' + shimJs.replace(/\\/g, '/'));
if (typeof shim.ensureReady !== 'function' || typeof shim.compileToSpirv !== 'function') {
    console.error('Shim does not export ensureReady() + compileToSpirv().');
    process.exit(2);
}

// Exercise idempotency: two ensureReady() calls must both resolve, load once.
await shim.ensureReady();
await shim.ensureReady();

const stems = readdirSync(corpusDir)
    .filter(f => f.endsWith('.spv'))
    .map(f => f.slice(0, -4))
    .sort();

if (stems.length === 0) { console.error('No .spv ground-truth files in ' + corpusDir); process.exit(2); }

let failed = 0;
for (const stem of stems) {
    const hlsl = readFileSync(join(corpusDir, stem + '.hlsl'), 'utf8');
    const args = JSON.parse(readFileSync(join(corpusDir, stem + '.args.json'), 'utf8'));
    const expected = new Uint8Array(readFileSync(join(corpusDir, stem + '.spv')));

    let actual;
    try {
        actual = shim.compileToSpirv(hlsl, args);
    } catch (e) {
        console.error(`[${stem}] THREW: ${e && e.message ? e.message : e}`);
        failed++;
        continue;
    }

    if (bytesEqual(actual, expected)) {
        console.log(`[${stem}] OK — ${actual.length} bytes, byte-identical to desktop DXC (via SHIM)`);
    } else {
        failed++;
        console.error(`[${stem}] MISMATCH — shim=${actual.length} bytes, desktop=${expected.length} bytes; ` +
            `first diff at ${firstDiff(actual, expected)}`);
    }
}

function bytesEqual(a, b) {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
}
function firstDiff(a, b) {
    const n = Math.min(a.length, b.length);
    for (let i = 0; i < n; i++) if (a[i] !== b[i]) return `byte ${i} (0x${a[i].toString(16)} vs 0x${b[i].toString(16)})`;
    return a.length === b.length ? 'none' : `length ${a.length} vs ${b.length}`;
}

if (failed > 0) {
    console.error(`\n${failed}/${stems.length} corpus shader(s) NOT byte-identical via the SHIM. G1 gate FAILED.`);
    process.exit(1);
}
console.log(`\nALL ${stems.length} CORPUS SHADERS BYTE-IDENTICAL VIA THE FAITHFUL SHIM — G1 gate PASSED.`);
