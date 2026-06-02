// Byte-identity gate for the FAITHFUL DXC->WASM frontend (Phase 23 M0 DoD).
//
// Loads the SAME ES module the browser loads (the emscripten DXC build:
// .wasm-build/dxc-wasm-out/dxcompiler.js) and asserts that, for every corpus shader,
// its compileToSpirv(hlsl, args) output EQUALS the desktop DXC SPIR-V byte-for-byte.
//
// The corpus ground truth (.wasm-build/corpus-spirv/{name}.{hlsl,args.json,spv}) is
// produced by the dxc-corpus-probe, which drives the REAL desktop ShadowDusk pipeline
// (FxPreParser + Preprocessor + DxcShaderCompiler @ Vortice.Dxc 3.3.4) and captures
// the EXACT (preprocessed HLSL, DXC arg list, SPIR-V bytes) triple the desktop CLI
// feeds DXC. compileToSpirv receives byte-for-byte the same hlsl+args, so byte-equal
// SPIR-V proves the WASM build is the same compiler -> the transitive render-proof.
//
// Run:  node .wasm-build/node-test-dxc-wasm.mjs
//
// The compileToSpirv export comes from EMSCRIPTEN_BINDINGS in dxc-wasm-glue.cpp; the
// module is MODULARIZE'd with EXPORT_NAME=createDxcModule, so we instantiate it and
// read module.compileToSpirv (an embind function: (string, string[]) -> Uint8Array).

import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const moduleJs = join(here, 'dxc-wasm-out', 'dxcompiler.js');
const corpusDir = join(here, 'corpus-spirv');

if (!existsSync(moduleJs)) {
    console.error(`DXC WASM module not found: ${moduleJs}`);
    console.error('Build it first:  pwsh -File .wasm-build/Invoke-DxcWasmBuild.ps1');
    process.exit(2);
}
if (!existsSync(corpusDir)) {
    console.error(`Corpus ground truth not found: ${corpusDir}`);
    console.error('Capture it first:  dotnet run --project .wasm-build/dxc-corpus-probe -- <repoRoot> ' + corpusDir);
    process.exit(2);
}

const factory = (await import('file://' + moduleJs.replace(/\\/g, '/'))).default;
const Module = await factory();
const compileToSpirv = Module.compileToSpirv;
if (typeof compileToSpirv !== 'function') {
    console.error('Module does not export compileToSpirv (embind binding missing).');
    process.exit(2);
}

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
        actual = compileToSpirv(hlsl, args);
        // embind may return a Uint8Array view; normalize to a plain Uint8Array.
        actual = actual instanceof Uint8Array ? actual : new Uint8Array(actual);
    } catch (e) {
        console.error(`[${stem}] THREW: ${e && e.message ? e.message : e}`);
        failed++;
        continue;
    }

    if (bytesEqual(actual, expected)) {
        console.log(`[${stem}] OK — ${actual.length} bytes, byte-identical to desktop DXC`);
    } else {
        failed++;
        console.error(`[${stem}] MISMATCH — wasm=${actual.length} bytes, desktop=${expected.length} bytes; ` +
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
    console.error(`\n${failed}/${stems.length} corpus shader(s) NOT byte-identical. M0 gate FAILED.`);
    process.exit(1);
}
console.log(`\nALL ${stems.length} CORPUS SHADERS BYTE-IDENTICAL — DXC->WASM == desktop DXC. M0 gate PASSED.`);
