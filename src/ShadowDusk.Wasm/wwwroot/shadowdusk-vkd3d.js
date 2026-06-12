// ShadowDusk — FAITHFUL in-browser HLSL -> D3D-bytecode backend for the
// `shadowdusk-vkd3d` [JSImport] module contract (see
// src/ShadowDusk.Wasm/JsShaderBackends.cs:
//   [JSImport("ensureReady", "shadowdusk-vkd3d")] static partial Task   EnsureReadyAsync();
//   [JSImport("compile",     "shadowdusk-vkd3d")] static partial byte[] Compile(
//       byte[] sourceUtf8, string entryPoint, string profile, string sourceName, int targetType);
//
// THIS IS THE PRODUCT DXBC/FNA BACKEND FOR THE BROWSER (Phase 4.1, Option A). It
// wraps the SAME pinned vkd3d-shader 1.17 the desktop pipeline P/Invokes
// (src/ShadowDusk.HLSL/Vkd3d/Vkd3dShaderCompiler.cs), compiled to WebAssembly —
// NO substitute compiler — so its output is asserted byte-identical to the desktop
// backend over the corpus (tests/ShadowDusk.BrowserTests/node-test-vkd3d-wasm.mjs,
// the Phase 23 G1-gate pattern). One artifact closes two cells: targetType 5
// (DXBC_TPF) serves browser DirectX export; targetType 4 (D3D_BYTECODE, SM1-3)
// serves browser FNA fx_2_0 export.
//
// The emscripten module (./vkd3d/vkd3d-shader.{js,wasm}, MODULARIZE + EXPORT_ES6,
// `export default` factory) is a RESTORED artifact (release tag
// native-vkd3d-wasm-1.17; see ./vkd3d/RESTORE.md + tools/restore.*) and is NOT
// committed. It exports this C ABI (the Phase 4.1 wrapper contract):
//
//   // 0 (VKD3D_OK) on success, negative vkd3d error code on failure.
//   // target_type: 4 = VKD3D_SHADER_TARGET_D3D_BYTECODE, 5 = VKD3D_SHADER_TARGET_DXBC_TPF.
//   int  sdw_vkd3d_compile(const unsigned char* source, int source_len,
//                          const char* entry_point, const char* profile,
//                          const char* source_name, int target_type,
//                          unsigned char** out_code, int* out_size, char** out_messages);
//   void sdw_vkd3d_free_code(unsigned char* p);
//   void sdw_vkd3d_free_messages(char* p);
//
// Glue requirements on the module instance (beyond the C exports `_sdw_*`): only
// `_malloc`, `_free`, and the `HEAPU8` view — strings are encoded/decoded with
// TextEncoder/TextDecoder and pointers are read via a DataView over HEAPU8.buffer,
// so no cwrap/getValue/UTF8ToString runtime exports are needed.
//
// LAZY LOADING — same contract as the shadowdusk-dxc shim: this module evaluates
// instantly (no top-level await); ensureReady() performs the one-time download +
// instantiation and is awaited by WasmVkd3dShaderCompiler before the synchronous
// compile. Throwing a plain Error is what surfaces to .NET as a JSException:
// a load failure becomes ShaderError SD1902; a compile failure carries vkd3d's
// VERBATIM diagnostics (file:line,col: error EXXXX: message) which .NET parses
// with the SAME reformatter the desktop backend uses (constraint 5).

let vkd3dInstance = null;  // the instantiated emscripten Module (cached)
let loadPromise = null;    // in-flight/settled load; ensures we load exactly once
let initError = null;      // sticky load failure, surfaced on every later call

const utf8Encoder = new TextEncoder();
const utf8Decoder = new TextDecoder('utf-8');

async function loadVkd3d() {
    // Resolve vkd3d-shader.js relative to THIS module's URL so it works identically
    // whether served from the package's _content/ wwwroot in the browser or imported
    // from disk under node (the byte-identity gate). vkd3d-shader.js then finds
    // vkd3d-shader.wasm via its own import.meta.url (co-located in ./vkd3d/), so no
    // locateFile override is needed.
    const factoryUrl = new URL('./vkd3d/vkd3d-shader.js', import.meta.url).href;
    const createVkd3dModule = (await import(factoryUrl)).default;
    if (typeof createVkd3dModule !== 'function') {
        throw new Error('vkd3d-shader.js did not export a default factory (createVkd3dModule).');
    }

    const mod = await createVkd3dModule();
    for (const required of ['_sdw_vkd3d_compile', '_sdw_vkd3d_free_code', '_sdw_vkd3d_free_messages', '_malloc', '_free']) {
        if (!mod || typeof mod[required] !== 'function') {
            throw new Error(`vkd3d-shader module is missing the required export '${required}'.`);
        }
    }
    if (!mod.HEAPU8) {
        throw new Error('vkd3d-shader module does not expose the HEAPU8 memory view.');
    }
    vkd3dInstance = mod;
}

/**
 * Idempotently load + initialize the faithful vkd3d-shader->WASM compiler. Resolves
 * when the module is instantiated (or rejects with the load error — e.g. when
 * ./vkd3d/vkd3d-shader.{js,wasm} has not been restored). The host MUST await this
 * once before the first compile call. Safe to call repeatedly; the download +
 * instantiation happen exactly once.
 *
 * Exposed to .NET via [JSImport("ensureReady","shadowdusk-vkd3d")] Task EnsureReadyAsync().
 * @returns {Promise<void>}
 */
export function ensureReady() {
    if (vkd3dInstance) return Promise.resolve();
    if (!loadPromise) {
        initError = null;
        loadPromise = loadVkd3d().catch((e) => {
            initError = e instanceof Error ? e : new Error(String(e));
            // Reset so a LATER ensureReady() retries the download instead of the
            // session staying bricked on a transient fetch failure — mirrors
            // WasmModuleRegistration.RegisterOnceAsync's reset-on-failure. initError
            // stays set between the failure and the next attempt so a stray compile()
            // call still surfaces the load error rather than "not ready".
            loadPromise = null;
            // Re-throw so the awaiting host sees the failure (surfaced as SD1902).
            throw initError;
        });
    }
    return loadPromise;
}

// Allocate a NUL-terminated UTF-8 C string on the module heap. null/undefined -> 0
// (the ABI accepts a NULL source_name). Caller frees with mod._free.
function allocCString(mod, value) {
    if (value === null || value === undefined) return 0;
    const bytes = utf8Encoder.encode(String(value));
    const ptr = mod._malloc(bytes.length + 1);
    if (!ptr) throw new Error(`vkd3d-shader WASM: _malloc(${bytes.length + 1}) failed (out of memory).`);
    mod.HEAPU8.set(bytes, ptr);
    mod.HEAPU8[ptr + bytes.length] = 0;
    return ptr;
}

// Read a 32-bit little-endian value from the module heap. Always goes through the
// CURRENT mod.HEAPU8 (the view is replaced when emscripten memory grows, so it must
// never be cached across the compile call).
function readU32(mod, ptr) {
    return new DataView(mod.HEAPU8.buffer).getUint32(ptr, /* littleEndian */ true);
}

// Read a NUL-terminated UTF-8 C string from the module heap ('' for NULL). Bounded
// by the heap length so a missing terminator can never scan past the end (heap[i]
// is undefined !== 0 there, which would loop forever otherwise).
function readCString(mod, ptr) {
    if (!ptr) return '';
    const heap = mod.HEAPU8;
    let end = ptr;
    while (end < heap.length && heap[end] !== 0) end++;
    return utf8Decoder.decode(heap.subarray(ptr, end));
}

/**
 * Compile HLSL (UTF-8 source bytes, NOT null-terminated — passed to the ABI as
 * pointer + length) to D3D bytecode via the faithful vkd3d-shader->WASM module.
 * JS contract (to .NET): compile(sourceUtf8: Uint8Array, entryPoint: string,
 * profile: string, sourceName: string, targetType: number): Uint8Array, throwing a
 * plain Error on failure whose message is vkd3d's VERBATIM diagnostic text
 * (surfaced to .NET as JSException and parsed by the shared
 * Vkd3dCompileContract.MapCompileFailure — constraint 5, no swallowing).
 *
 * @param {Uint8Array} sourceUtf8 Preprocessed, #include-flattened HLSL as UTF-8 bytes.
 * @param {string}     entryPoint Shader entry point (C string at the ABI).
 * @param {string}     profile    Shader profile, e.g. "ps_5_0" / "vs_2_0" (C string).
 * @param {string}     sourceName Diagnostic source name (C string; may be null).
 * @param {number}     targetType 4 = D3D_BYTECODE (SM1-3, FNA), 5 = DXBC_TPF (SM4/5, DX11).
 * @returns {Uint8Array} The compiled bytecode (copied out of the WASM heap).
 */
export function compile(sourceUtf8, entryPoint, profile, sourceName, targetType) {
    if (initError) {
        throw new Error('Faithful vkd3d-shader WASM compiler failed to initialize: ' + initError.message);
    }
    const mod = vkd3dInstance;
    if (!mod) {
        // Should not happen — the host awaits ensureReady() before the first compile —
        // but fail loudly rather than silently returning empty bytecode.
        throw new Error('Faithful vkd3d-shader WASM compiler is not ready (call ensureReady() first).');
    }

    const source = sourceUtf8 instanceof Uint8Array ? sourceUtf8 : new Uint8Array(sourceUtf8 || 0);
    if (source.length === 0) {
        throw new Error('compile: empty HLSL source.');
    }

    let srcPtr = 0, entryPtr = 0, profilePtr = 0, namePtr = 0, outPtrs = 0;
    try {
        // Source bytes: raw UTF-8 + explicit length (NOT null-terminated at the ABI).
        srcPtr = mod._malloc(source.length);
        if (!srcPtr) throw new Error(`vkd3d-shader WASM: _malloc(${source.length}) failed (out of memory).`);
        mod.HEAPU8.set(source, srcPtr);

        // C strings for entry point / profile / source name.
        entryPtr = allocCString(mod, entryPoint);
        profilePtr = allocCString(mod, profile);
        namePtr = allocCString(mod, sourceName);

        // out_code / out_size / out_messages — three contiguous 32-bit out-slots.
        outPtrs = mod._malloc(12);
        if (!outPtrs) throw new Error('vkd3d-shader WASM: _malloc(12) failed (out of memory).');
        mod.HEAPU8.fill(0, outPtrs, outPtrs + 12);
        const outCodePtr = outPtrs, outSizePtr = outPtrs + 4, outMsgsPtr = outPtrs + 8;

        const rc = mod._sdw_vkd3d_compile(
            srcPtr, source.length,
            entryPtr, profilePtr, namePtr, targetType | 0,
            outCodePtr, outSizePtr, outMsgsPtr);

        // Messages first (present on failure AND on warning-bearing success); always
        // freed via the ABI's own free function.
        const msgPtr = readU32(mod, outMsgsPtr);
        let messages = '';
        if (msgPtr) {
            try {
                messages = readCString(mod, msgPtr);
            } finally {
                mod._sdw_vkd3d_free_messages(msgPtr);
            }
        }

        const codePtr = readU32(mod, outCodePtr);
        const codeSize = readU32(mod, outSizePtr);
        try {
            if (rc !== 0 || !codePtr || codeSize === 0) {
                // Re-throw vkd3d's verbatim diagnostics; .NET parses file/line/column
                // out of them (same fallback text shape as the desktop SD0212 path).
                throw new Error(messages.trim().length > 0
                    ? messages
                    : `vkd3d-shader WASM compilation failed (rc=${rc}) with no diagnostics`);
            }
            // Copy the bytecode OUT of the WASM heap before freeing it.
            return new Uint8Array(mod.HEAPU8.subarray(codePtr, codePtr + codeSize));
        } finally {
            if (codePtr) mod._sdw_vkd3d_free_code(codePtr);
        }
    } finally {
        if (outPtrs) mod._free(outPtrs);
        if (namePtr) mod._free(namePtr);
        if (profilePtr) mod._free(profilePtr);
        if (entryPtr) mod._free(entryPtr);
        if (srcPtr) mod._free(srcPtr);
    }
}
