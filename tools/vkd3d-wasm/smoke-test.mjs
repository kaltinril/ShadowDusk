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
//   4. The caller's compile options reach vkd3d (issue #295): a ps_4_0 shader
//      with SM1-3 semantics on struct fields compiles to SV_Position / SV_Target
//      with BACKWARD_COMPATIBILITY = MAP_SEMANTIC_NAMES, and is refused (E5013)
//      with no options. A wrapper that dropped, defaulted or hard-coded the
//      options fails one of the two.
//   5. An option list the wrapper cannot honour exactly is refused, not truncated.
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
// SM4+ rejects a user semantic on a pixel-shader output (vkd3d 2.1) unless it is
// given BACKWARD_COMPATIBILITY/MAP_SEMANTIC_NAMES; this case passes no options, so
// it declares SV_Target directly. Case 4 below is the one that passes the option.
const SMOKE_HLSL_SM5 = 'float4 main() : SV_Target { return float4(1,0,0,1); }\n';
const BROKEN_HLSL = 'float4 main() : COLOR { return float4(1,0,0,1; }\n'; // missing ')'
// SM1-3 semantics on struct FIELDS (the tests/fixtures/shaders/Sm3SemanticStructs.fx
// shape): only the compile option makes this an SM4 pixel shader.
const SM3_STRUCT_HLSL =
    'struct I { float4 p : POSITION0; float4 c : COLOR0; };\n' +
    'struct O { float4 c : COLOR0; };\n' +
    'O main(I i) { O o; o.c = i.c; return o; }\n';

// vkd3d_shader_compile_option_name / value (vkd3d_shader.h), as the managed
// Vkd3dCompileContract.ResolveCompileOptions passes them for DXBC_TPF.
const OPTION_BACKWARD_COMPATIBILITY = 0x00000008;
const BACKCOMPAT_MAP_SEMANTIC_NAMES = 0x00000001;
const MAP_SEMANTIC_NAMES = [OPTION_BACKWARD_COMPATIBILITY, BACKCOMPAT_MAP_SEMANTIC_NAMES];

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

for (const name of ['_sdw_vkd3d_compile_options', '_sdw_vkd3d_free_code', '_sdw_vkd3d_free_messages', '_malloc', '_free']) {
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

// int sdw_vkd3d_compile_options(const unsigned char* source, int source_len,
//                       const char* entry_point, const char* profile,
//                       const char* source_name, int target_type,
//                       const unsigned int* options, int option_count,
//                       unsigned char** out_code, int* out_size, char** out_messages)
// `options` is the flat (name, value) pair list; `optionCount` defaults to its pair
// count and is overridable only to exercise the wrapper's argument checks.
function compile(hlsl, profile, targetType, options = [], optionCount = options.length / 2) {
    const srcBytes = utf8.encode(hlsl); // raw UTF-8, NOT null-terminated
    const srcPtr = mallocBytes(srcBytes);
    const entryPtr = mallocCString('main');
    const profilePtr = mallocCString(profile);
    const namePtr = mallocCString('smoke.hlsl');
    const optsPtr = options.length > 0 ? mod._malloc(options.length * 4) : 0;
    const outCodePP = mod._malloc(4);
    const outSizeP = mod._malloc(4);
    const outMsgPP = mod._malloc(4);
    try {
        options.forEach((word, i) => mod.setValue(optsPtr + 4 * i, word, 'i32'));
        mod.setValue(outCodePP, 0, 'i32');
        mod.setValue(outSizeP, 0, 'i32');
        mod.setValue(outMsgPP, 0, 'i32');

        const rc = mod._sdw_vkd3d_compile_options(
            srcPtr, srcBytes.length, entryPtr, profilePtr, namePtr, targetType,
            optsPtr, optionCount,
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
        for (const p of [srcPtr, entryPtr, profilePtr, namePtr, optsPtr, outCodePP, outSizeP, outMsgPP]) {
            if (p) mod._free(p);
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
    const { rc, code, messages } = compile(SMOKE_HLSL_SM5, 'ps_5_0', TARGET_DXBC_TPF);
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

// 4. The caller's options reach vkd3d: MAP_SEMANTIC_NAMES turns the SM1-3 struct
//    semantics into SV_Position / SV_Target; without it vkd3d refuses the shader.
{
    const latin1 = (code) => Array.from(code, (b) => String.fromCharCode(b)).join('');
    const mapped = compile(SM3_STRUCT_HLSL, 'ps_4_0', TARGET_DXBC_TPF, MAP_SEMANTIC_NAMES);
    check('SM3 struct semantics + MAP_SEMANTIC_NAMES rc == 0', mapped.rc === 0,
        `rc=${mapped.rc}${mapped.messages ? `, messages: ${mapped.messages.trim()}` : ''}`);
    const text = latin1(mapped.code);
    check('SM3 struct semantics + MAP_SEMANTIC_NAMES -> SV_Position input, SV_Target output',
        text.includes('SV_Position') && text.includes('SV_Target'),
        `${mapped.code.length} bytes`);

    const unmapped = compile(SM3_STRUCT_HLSL, 'ps_4_0', TARGET_DXBC_TPF, []);
    check('SM3 struct semantics with NO options is refused (E5013)',
        unmapped.rc < 0 && unmapped.code.length === 0 && unmapped.messages.includes('E5013'),
        `rc=${unmapped.rc}, first line: ${unmapped.messages.split('\n')[0]}`);
}

// 5. An option list the wrapper cannot honour exactly is refused outright:
//    more pairs than it has room for, a negative count, a count with no list.
{
    const tooMany = Array.from({ length: 17 }, () => MAP_SEMANTIC_NAMES).flat();
    for (const [label, options, count] of [
        ['17 options', tooMany, 17],
        ['a negative option count', MAP_SEMANTIC_NAMES, -1],
        ['a count with a NULL list', [], 1],
    ]) {
        const { rc, code } = compile(SMOKE_HLSL_SM5, 'ps_5_0', TARGET_DXBC_TPF, options, count);
        check(`${label} is refused`, rc < 0 && code.length === 0, `rc=${rc}`);
    }
}

if (failures > 0) {
    console.error(`SMOKE FAILED: ${failures} assertion(s) failed`);
    process.exit(1);
}
console.log('SMOKE OK: all assertions passed');
