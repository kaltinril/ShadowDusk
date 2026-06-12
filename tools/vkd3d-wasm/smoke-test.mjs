// smoke-test.mjs — node gate for the vkd3d-shader → WASM build (Phase 4.1).
//
// Mirrors the smoke test of .github/workflows/build-vkd3d-natives.yml (the Phase
// 37 C native precedent), driven through the sdw_* wrapper ABI instead of the
// vkd3d-compiler CLI:
//
//   1. ps_2_0 → VKD3D_SHADER_TARGET_D3D_BYTECODE (4): asserts the SM2.0 pixel
//      shader version token 0xFFFF0200 (the FNA fx_2_0 path).
//   2. ps_5_0 → VKD3D_SHADER_TARGET_DXBC_TPF (5): asserts the "DXBC" container
//      magic (the MonoGame DX11 path).
//   3. A deliberately broken shader must FAIL with a negative rc and verbatim
//      diagnostics in out_messages (constraint 5: fail loudly).
//
// Usage: node smoke-test.mjs <path-to-vkd3d-shader.js>
// The module must be an emscripten MODULARIZE + EXPORT_ES6 build exporting a
// default factory (createVkd3dModule) with the three sdw_* functions plus
// malloc/free (see sdw_vkd3d_wrapper.c for the ABI contract).

import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';

const TARGET_D3D_BYTECODE = 4; // VKD3D_SHADER_TARGET_D3D_BYTECODE (SM1-3)
const TARGET_DXBC_TPF = 5;     // VKD3D_SHADER_TARGET_DXBC_TPF (SM4/5)

// Same smoke shader as build-vkd3d-natives.yml.
const SMOKE_HLSL = 'float4 main() : COLOR { return float4(1,0,0,1); }\n';
const BROKEN_HLSL = 'float4 main() : COLOR { return float4(1,0,0,1; }\n'; // missing ')'

const modulePath = process.argv[2];
if (!modulePath) {
    console.error('usage: node smoke-test.mjs <path-to-vkd3d-shader.js>');
    process.exit(2);
}

const factory = (await import(pathToFileURL(resolve(modulePath)).href)).default;
if (typeof factory !== 'function') {
    console.error('FAIL: module did not export a default emscripten factory');
    process.exit(1);
}
const mod = await factory();

for (const name of ['_sdw_vkd3d_compile', '_sdw_vkd3d_free_code', '_sdw_vkd3d_free_messages', '_malloc', '_free']) {
    if (typeof mod[name] !== 'function') {
        console.error(`FAIL: module is missing required export ${name}`);
        process.exit(1);
    }
}

const utf8 = new TextEncoder();

function mallocBytes(bytes) {
    const ptr = mod._malloc(bytes.length === 0 ? 1 : bytes.length);
    if (!ptr) throw new Error('malloc failed');
    mod.HEAPU8.set(bytes, ptr);
    return ptr;
}

function mallocCString(s) {
    return mallocBytes(utf8.encode(s + '\0'));
}

// int sdw_vkd3d_compile(const unsigned char* source, int source_len,
//                       const char* entry_point, const char* profile,
//                       const char* source_name, int target_type,
//                       unsigned char** out_code, int* out_size, char** out_messages)
function compile(hlsl, profile, targetType) {
    const srcBytes = utf8.encode(hlsl); // raw UTF-8, NOT null-terminated
    const srcPtr = mallocBytes(srcBytes);
    const entryPtr = mallocCString('main');
    const profilePtr = mallocCString(profile);
    const namePtr = mallocCString('smoke.hlsl');
    const outCodePP = mod._malloc(4);
    const outSizeP = mod._malloc(4);
    const outMsgPP = mod._malloc(4);
    try {
        mod.setValue(outCodePP, 0, 'i32');
        mod.setValue(outSizeP, 0, 'i32');
        mod.setValue(outMsgPP, 0, 'i32');

        const rc = mod._sdw_vkd3d_compile(
            srcPtr, srcBytes.length, entryPtr, profilePtr, namePtr, targetType,
            outCodePP, outSizeP, outMsgPP);

        const codePtr = mod.getValue(outCodePP, 'i32');
        const size = mod.getValue(outSizeP, 'i32');
        const msgPtr = mod.getValue(outMsgPP, 'i32');

        const messages = msgPtr ? mod.UTF8ToString(msgPtr) : '';
        const code = codePtr ? mod.HEAPU8.slice(codePtr, codePtr + size) : new Uint8Array(0);

        if (codePtr) mod._sdw_vkd3d_free_code(codePtr);
        if (msgPtr) mod._sdw_vkd3d_free_messages(msgPtr);

        return { rc, code, messages };
    } finally {
        for (const p of [srcPtr, entryPtr, profilePtr, namePtr, outCodePP, outSizeP, outMsgPP]) {
            mod._free(p);
        }
    }
}

let failures = 0;
function check(label, ok, detail) {
    if (ok) {
        console.log(`OK   ${label}${detail ? ` (${detail})` : ''}`);
    } else {
        console.error(`FAIL ${label}${detail ? ` (${detail})` : ''}`);
        failures++;
    }
}

// 1. ps_2_0 -> d3dbc: SM2.0 PS version token 0xFFFF0200 (little-endian: 00 02 FF FF).
{
    const { rc, code, messages } = compile(SMOKE_HLSL, 'ps_2_0', TARGET_D3D_BYTECODE);
    check('ps_2_0 -> d3dbc rc == 0', rc === 0, `rc=${rc}${messages ? `, messages: ${messages.trim()}` : ''}`);
    check('ps_2_0 -> d3dbc non-empty', code.length > 0, `${code.length} bytes`);
    const token = code.length >= 4
        ? (code[0] | (code[1] << 8) | (code[2] << 16) | (code[3] << 24)) >>> 0
        : 0;
    check('ps_2_0 -> d3dbc version token 0xFFFF0200', token === 0xFFFF0200,
        `0x${token.toString(16).toUpperCase().padStart(8, '0')}`);
}

// 2. ps_5_0 -> dxbc-tpf: "DXBC" container magic.
{
    const { rc, code, messages } = compile(SMOKE_HLSL, 'ps_5_0', TARGET_DXBC_TPF);
    check('ps_5_0 -> dxbc-tpf rc == 0', rc === 0, `rc=${rc}${messages ? `, messages: ${messages.trim()}` : ''}`);
    check('ps_5_0 -> dxbc-tpf non-empty', code.length > 0, `${code.length} bytes`);
    const magic = code.length >= 4 ? String.fromCharCode(code[0], code[1], code[2], code[3]) : '';
    check('ps_5_0 -> dxbc-tpf magic "DXBC"', magic === 'DXBC', JSON.stringify(magic));
}

// 3. Broken shader: must fail with rc < 0 and verbatim diagnostics.
{
    const { rc, code, messages } = compile(BROKEN_HLSL, 'ps_2_0', TARGET_D3D_BYTECODE);
    check('broken shader rc < 0', rc < 0, `rc=${rc}`);
    check('broken shader emits no code', code.length === 0, `${code.length} bytes`);
    check('broken shader surfaces diagnostics', messages.length > 0,
        messages ? `first line: ${messages.split('\n')[0]}` : 'no messages');
}

if (failures > 0) {
    console.error(`SMOKE FAILED: ${failures} assertion(s) failed`);
    process.exit(1);
}
console.log('SMOKE OK: all assertions passed');
