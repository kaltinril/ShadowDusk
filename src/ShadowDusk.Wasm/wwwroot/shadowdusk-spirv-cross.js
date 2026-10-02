// ShadowDusk — REAL in-browser SPIR-V -> GLSL backend for the
// `shadowdusk-spirv-cross` [JSImport] module contract (see
// src/ShadowDusk.Wasm/JsShaderBackends.cs:
//   [JSImport("transpileToGlsl", "shadowdusk-spirv-cross")]
//   static partial string TranspileToGlsl(byte[] spirv, bool flipVertexY,
//       bool fixupDepthConvention, int glslVersion, bool glslEs, bool vulkanSemantics);
//
// Backed by SPIRV-Cross's C API (spirv_cross_c) compiled to WebAssembly with
// emscripten (see ../../.wasm-build/build-spirv-cross-wasm.ps1). This module runs
// the SAME spvc_* call sequence the desktop SpirvCrossGlslTranspiler runs
// (src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs), with the SAME options, so the
// browser-emitted GLSL is byte-for-byte identical to the desktop output. On any
// failure it throws, surfaced to .NET as a JSException -> ShaderError SD1901.
//
// Browser-verification steps are documented in
// samples/ShaderFiddle.Web/wwwroot/spirv-cross/README.md.

import createSpirvCrossModule from './spirv-cross/spirv-cross.js';

// --------------------------------------------------------------------------
// SPIRV-Cross C API constants (must match src/ShadowDusk.GLSL/Interop/SpvcNative.cs).
const SPVC_BACKEND_GLSL = 1;          // spvc_backend
const SPVC_CAPTURE_MODE_TAKE_OWNERSHIP = 1;
const SPVC_SUCCESS = 0;               // spvc_result

// spvc_compiler_option (COMMON_BIT=0x1000000, GLSL_BIT=0x2000000)
const OPT_FLIP_VERTEX_Y          = 0x1000004;
const OPT_FIXUP_DEPTH_CONVENTION = 0x1000003;
const OPT_GLSL_VERSION           = 0x2000008;
const OPT_GLSL_ES                = 0x2000009;
const OPT_GLSL_VULKAN_SEMANTICS  = 0x200000A;
const OPT_RELAX_NAN_CHECKS       = 0x100004E;

// --------------------------------------------------------------------------
// Eagerly instantiate the WASM module. JSHost.ImportAsync awaits this ES module's
// evaluation, and this top-level await keeps the module from finishing loading
// until the WASM runtime is ready. By the time .NET invokes transpileToGlsl the
// module is fully initialized, so transpileToGlsl can be SYNCHRONOUS (the
// [JSImport] returns `string`, not Promise<string>).
//
// Issue #271: a trap inside the module (stack overflow, out-of-bounds access, abort)
// leaves its memory and C++ state undefined, so the instance is discarded and
// ensureReady() instantiates a fresh one before the next transpile (the slangc pattern
// from PR #266). `api` is null between a trap and that reload.
let api = bind(await createSpirvCrossModule());
let reloadPromise = null;

// cwrap the exact spvc_* entry points the desktop transpiler uses.
// Pointers / handles / enums are 32-bit ('number') in wasm32.
function bind(Module) {
    return {
        Module,
        spvc_context_create:
            Module.cwrap('spvc_context_create', 'number', ['number']),
        spvc_context_destroy:
            Module.cwrap('spvc_context_destroy', null, ['number']),
        spvc_context_get_last_error_string:
            Module.cwrap('spvc_context_get_last_error_string', 'number', ['number']),
        spvc_context_parse_spirv:
            Module.cwrap('spvc_context_parse_spirv', 'number', ['number', 'number', 'number', 'number']),
        spvc_context_create_compiler:
            Module.cwrap('spvc_context_create_compiler', 'number', ['number', 'number', 'number', 'number', 'number']),
        spvc_compiler_create_compiler_options:
            Module.cwrap('spvc_compiler_create_compiler_options', 'number', ['number', 'number']),
        spvc_compiler_options_set_bool:
            Module.cwrap('spvc_compiler_options_set_bool', 'number', ['number', 'number', 'number']),
        spvc_compiler_options_set_uint:
            Module.cwrap('spvc_compiler_options_set_uint', 'number', ['number', 'number', 'number']),
        spvc_compiler_install_compiler_options:
            Module.cwrap('spvc_compiler_install_compiler_options', 'number', ['number', 'number']),
        spvc_compiler_build_combined_image_samplers:
            Module.cwrap('spvc_compiler_build_combined_image_samplers', 'number', ['number']),
        spvc_compiler_compile:
            Module.cwrap('spvc_compiler_compile', 'number', ['number', 'number']),
    };
}

/**
 * Resolves once a usable SPIRV-Cross instance exists. A no-op unless a trap discarded
 * the instance, in which case it instantiates a fresh one (the .wasm download is cached).
 *
 * Exposed to .NET via [JSImport("ensureReady","shadowdusk-spirv-cross")] Task EnsureReadyAsync().
 * @returns {Promise<void>}
 */
export function ensureReady() {
    if (api) return Promise.resolve();
    if (!reloadPromise) {
        reloadPromise = createSpirvCrossModule().then(
            (mod) => { api = bind(mod); reloadPromise = null; },
            (e) => { reloadPromise = null; throw e; });
    }
    return reloadPromise;
}

// A SPIRV-Cross failure reported through the C API (a real diagnostic, not a trap).
class SpvcError extends Error {}

function lastError(a, ctx, stage) {
    let msg = '(no error string)';
    try {
        const p = a.spvc_context_get_last_error_string(ctx);
        if (p) msg = a.Module.UTF8ToString(p);
    } catch { /* ignore — fall through to the stage label */ }
    return `SPIRV-Cross [${stage}]: ${msg}`;
}

/**
 * Transpile a SPIR-V module to GLSL text, mirroring the desktop
 * SpirvCrossGlslTranspiler exactly.
 *
 * @param {Uint8Array} spirv  SPIR-V module bytes (little-endian word stream).
 * @param {boolean} flipVertexY           SPVC_COMPILER_OPTION_FLIP_VERTEX_Y.
 * @param {boolean} fixupDepthConvention  SPVC_COMPILER_OPTION_FIXUP_DEPTH_CONVENTION.
 * @param {number}  glslVersion           SPVC_COMPILER_OPTION_GLSL_VERSION (140).
 * @param {boolean} glslEs                SPVC_COMPILER_OPTION_GLSL_ES.
 * @param {boolean} vulkanSemantics       SPVC_COMPILER_OPTION_GLSL_VULKAN_SEMANTICS.
 * @returns {string} GLSL source text.
 * @throws on any SPIRV-Cross failure (message carries the last-error string), and with a
 *         'SPIRV-Cross trapped:' message when the module itself trapped (the instance is
 *         then discarded; JsSpirvToGlslTranspiler maps that prefix to SD1906).
 */
export function transpileToGlsl(spirv, flipVertexY, fixupDepthConvention, glslVersion, glslEs, vulkanSemantics, relaxNanChecks) {
    const a = api;
    if (!a) {
        throw new Error('The SPIRV-Cross WASM module is not loaded (a previous transpile trapped and the ' +
            'instance was discarded); await ensureReady() first.');
    }
    let trapped = false;
    try {
        return transpileCore(a, spirv, flipVertexY, fixupDepthConvention, glslVersion, glslEs, vulkanSemantics, relaxNanChecks,
            () => { trapped = true; });
    } catch (e) {
        if (e instanceof SpvcError) throw new Error(e.message);
        trapped = true;
        throw new Error('SPIRV-Cross trapped: ' + (e instanceof Error ? `${e.name}: ${e.message}` : String(e)));
    } finally {
        if (trapped) api = null;
    }
}

function transpileCore(a, spirv, flipVertexY, fixupDepthConvention, glslVersion, glslEs, vulkanSemantics, relaxNanChecks, markTrapped) {
    const Module = a.Module;
    const bytes = spirv instanceof Uint8Array ? spirv : new Uint8Array(spirv);
    if ((bytes.length & 3) !== 0)
        throw new SpvcError(`SPIRV-Cross: SPIR-V byte length ${bytes.length} is not a multiple of 4`);
    const wordCount = bytes.length >>> 2;

    // Allocations to free in reverse order. The context owns the parsed IR and the
    // compiler (TAKE_OWNERSHIP), so destroying the context frees those; we only have
    // to free our own malloc'd buffers and the context.
    let ctx = 0;
    let spirvPtr = 0;
    let outIrPtr = 0;       // spvc_parsed_ir*
    let outCompilerPtr = 0; // spvc_compiler*
    let outOptionsPtr = 0;  // spvc_compiler_options*
    let outSourcePtr = 0;   // const char**
    let ok = false;         // false while a module call is in flight: a throw then means a trap

    try {
        // context
        const ctxOut = Module._malloc(4);
        if (!ctxOut) throw new SpvcError('SPIRV-Cross WASM: _malloc(4) failed (out of memory).');
        try {
            if (a.spvc_context_create(ctxOut) !== SPVC_SUCCESS)
                throw new SpvcError('SPIRV-Cross [context_create]: failed to create context');
            ctx = Module.getValue(ctxOut, 'i32');
        } finally {
            Module._free(ctxOut);
        }

        // copy SPIR-V words into the heap
        spirvPtr = Module._malloc(bytes.length);
        if (!spirvPtr) throw new SpvcError('SPIRV-Cross WASM: _malloc(' + bytes.length + ') failed (out of memory).');
        Module.HEAPU8.set(bytes, spirvPtr);

        // parse_spirv(ctx, spirv, wordCount, &ir)
        outIrPtr = Module._malloc(4);
        if (!outIrPtr) throw new SpvcError('SPIRV-Cross WASM: _malloc(4) failed (out of memory).');
        if (a.spvc_context_parse_spirv(ctx, spirvPtr, wordCount, outIrPtr) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'parse_spirv'));
        const ir = Module.getValue(outIrPtr, 'i32');

        // create_compiler(ctx, GLSL, ir, TAKE_OWNERSHIP, &compiler)
        outCompilerPtr = Module._malloc(4);
        if (!outCompilerPtr) throw new SpvcError('SPIRV-Cross WASM: _malloc(4) failed (out of memory).');
        if (a.spvc_context_create_compiler(ctx, SPVC_BACKEND_GLSL, ir, SPVC_CAPTURE_MODE_TAKE_OWNERSHIP, outCompilerPtr) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'create_compiler'));
        const compiler = Module.getValue(outCompilerPtr, 'i32');

        // create_compiler_options(compiler, &options)
        outOptionsPtr = Module._malloc(4);
        if (!outOptionsPtr) throw new SpvcError('SPIRV-Cross WASM: _malloc(4) failed (out of memory).');
        if (a.spvc_compiler_create_compiler_options(compiler, outOptionsPtr) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'create_compiler_options'));
        const options = Module.getValue(outOptionsPtr, 'i32');

        // Options — SAME order/values as the desktop transpiler.
        if (a.spvc_compiler_options_set_bool(options, OPT_FLIP_VERTEX_Y, flipVertexY ? 1 : 0) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'set_option FlipVertexY'));
        if (a.spvc_compiler_options_set_bool(options, OPT_FIXUP_DEPTH_CONVENTION, fixupDepthConvention ? 1 : 0) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'set_option FixupDepthConvention'));
        if (a.spvc_compiler_options_set_uint(options, OPT_GLSL_VERSION, glslVersion >>> 0) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'set_option GlslVersion'));
        if (a.spvc_compiler_options_set_bool(options, OPT_GLSL_ES, glslEs ? 1 : 0) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'set_option GlslEs'));
        if (a.spvc_compiler_options_set_bool(options, OPT_GLSL_VULKAN_SEMANTICS, vulkanSemantics ? 1 : 0) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'set_option GlslVulkanSemantics'));
        // Issue #149 parity (bug-hunt 2026-07-27 C4): the desktop transpiler sets
        // RelaxNanChecks so SPIRV-Cross emits plain min/max/clamp instead of the
        // isnan()-ternary that versionless GLSL cannot declare. Omitting it here made
        // browser output diverge from desktop and reopened the #149 macOS load failure.
        if (a.spvc_compiler_options_set_bool(options, OPT_RELAX_NAN_CHECKS, relaxNanChecks ? 1 : 0) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'set_option RelaxNanChecks'));

        if (a.spvc_compiler_install_compiler_options(compiler, options) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'install_compiler_options'));

        // Always before compile — safe no-op on texture-free shaders; required for textured ones.
        if (a.spvc_compiler_build_combined_image_samplers(compiler) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'build_combined_image_samplers'));

        // compile(compiler, &source) — source is owned by the context.
        outSourcePtr = Module._malloc(4);
        if (!outSourcePtr) throw new SpvcError('SPIRV-Cross WASM: _malloc(4) failed (out of memory).');
        if (a.spvc_compiler_compile(compiler, outSourcePtr) !== SPVC_SUCCESS)
            throw new SpvcError(lastError(a, ctx, 'compile'));
        const srcPtr = Module.getValue(outSourcePtr, 'i32');
        const glsl = srcPtr ? Module.UTF8ToString(srcPtr) : '';

        ok = true;
        return glsl;
    } catch (e) {
        ok = e instanceof SpvcError;
        throw e;
    } finally {
        if (ok) {
            // Free our own buffers; context_destroy frees IR + compiler + the GLSL string.
            if (outSourcePtr) Module._free(outSourcePtr);
            if (outOptionsPtr) Module._free(outOptionsPtr);
            if (outCompilerPtr) Module._free(outCompilerPtr);
            if (outIrPtr) Module._free(outIrPtr);
            if (spirvPtr) Module._free(spirvPtr);
            if (ctx) a.spvc_context_destroy(ctx);
        } else {
            // A trap: never call back into the instance; it is discarded with its heap.
            markTrapped();
        }
    }
}
