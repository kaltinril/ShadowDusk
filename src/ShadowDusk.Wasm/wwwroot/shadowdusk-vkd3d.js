// ShadowDusk — FAITHFUL in-browser HLSL -> D3D-bytecode backend for the
// `shadowdusk-vkd3d` [JSImport] module contract (see
// src/ShadowDusk.Wasm/JsShaderBackends.cs:
//   [JSImport("ensureReady", "shadowdusk-vkd3d")] static partial Task     EnsureReadyAsync();
//   [JSImport("compile",     "shadowdusk-vkd3d")] static partial JSObject Compile(
//       byte[] sourceUtf8, string entryPoint, string profile, string sourceName, int targetType,
//       int[] options);   // -> { code: Uint8Array, messages: string }
//
// THIS IS THE PRODUCT DXBC/FNA BACKEND FOR THE BROWSER (Phase 4.1, Option A). It
// wraps the SAME pinned vkd3d-shader 2.1 the desktop pipeline P/Invokes
// (src/ShadowDusk.HLSL/Vkd3d/Vkd3dShaderCompiler.cs), compiled to WebAssembly —
// NO substitute compiler — so its output is asserted byte-identical to the desktop
// backend over the corpus (tests/ShadowDusk.BrowserTests/node-test-vkd3d-wasm.mjs,
// the Phase 23 G1-gate pattern). One artifact closes two cells: targetType 5
// (DXBC_TPF) serves browser DirectX export; targetType 4 (D3D_BYTECODE, SM1-3)
// serves browser FNA fx_2_0 export.
//
// The emscripten module (./vkd3d/vkd3d-shader.{js,wasm}, MODULARIZE + EXPORT_ES6,
// `export default` factory) is a RESTORED artifact (release tag
// native-vkd3d-wasm-2.1; see ./vkd3d/RESTORE.md + tools/restore.*) and is NOT
// committed. It exports this C ABI (the Phase 4.1 wrapper contract):
//
//   // 0 (VKD3D_OK) on success, negative vkd3d error code on failure.
//   // target_type: 4 = VKD3D_SHADER_TARGET_D3D_BYTECODE, 5 = VKD3D_SHADER_TARGET_DXBC_TPF.
//   // options: option_count (name, value) pairs of 32-bit words, passed to vkd3d untouched.
//   // out_messages: vkd3d's verbatim message text, set on failure AND on a success that
//   //               carries non-fatal diagnostics (the wrapper compiles at LOG_WARNING).
//   int  sdw_vkd3d_compile_options(const unsigned char* source, int source_len,
//                          const char* entry_point, const char* profile,
//                          const char* source_name, int target_type,
//                          const unsigned int* options, int option_count,
//                          unsigned char** out_code, int* out_size, char** out_messages);
//   void sdw_vkd3d_free_code(unsigned char* p);
//   void sdw_vkd3d_free_messages(char* p);
//
// COMPILE OPTIONS (issue #295). The vkd3d_shader_compile_option list is chosen in ONE
// place, the managed Vkd3dCompileContract.ResolveCompileOptions, which the desktop
// backend marshals into vkd3d_shader_compile_info and the browser backend hands to
// compile() below. This shim and the C wrapper only FORWARD that list: neither adds,
// drops nor defaults an option, so the two hosts cannot compile with different ones.
// A module built before issue #295 (the one still hosted on native-vkd3d-wasm-2.1)
// exports only the older sdw_vkd3d_compile, which has no option parameters and always
// compiled with none; with such a module compile() still works but CANNOT honour the
// options, warns once on the console, and an SM4+ shader that relies on
// MAP_SEMANTIC_NAMES (SM1-3 semantics on struct fields) compiles differently from the
// desktop or is refused with E5013. That path goes away with the re-pinned module.
//
// MESSAGES ON SUCCESS (issue #335). vkd3d's message buffer is populated on a successful
// compile too (W5300 implicit truncation, W5302 unrecognized attribute, ...), and the
// desktop backend turns that text into PlatformBlob.Warnings / CompiledShader.Warnings.
// This shim used to read out_messages and use it only in the failure branch, returning
// the bytecode alone on success, so the browser user never saw a warning the desktop
// user saw: identical bytes, different warnings, invisible to every byte gate. compile()
// now returns BOTH, verbatim; the managed WasmVkd3dShaderCompiler parses and relocates
// the text through the same shared code the desktop runs. Nothing here interprets,
// filters or reformats the text. Every pinned module already writes out_messages on
// success (the wrapper has always forwarded vkd3d's buffer), so this needed no rebuild.
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
    for (const required of ['_sdw_vkd3d_free_code', '_sdw_vkd3d_free_messages', '_malloc', '_free']) {
        if (!mod || typeof mod[required] !== 'function') {
            throw new Error(`vkd3d-shader module is missing the required export '${required}'.`);
        }
    }
    // The compile entry point: sdw_vkd3d_compile_options (takes the caller's compile
    // options), or, in a module built before issue #295, only sdw_vkd3d_compile.
    if (typeof mod._sdw_vkd3d_compile_options !== 'function' && typeof mod._sdw_vkd3d_compile !== 'function') {
        throw new Error("vkd3d-shader module is missing the required export '_sdw_vkd3d_compile_options'.");
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

/**
 * Whether the loaded module can take compile options (it exports
 * sdw_vkd3d_compile_options). false for a module built before issue #295, which
 * compiles with no options whatever compile() is handed. For the gates, so they can
 * tell the two apart; undefined before ensureReady() has resolved.
 * @returns {boolean|undefined}
 */
export function honoursCompileOptions() {
    return vkd3dInstance ? typeof vkd3dInstance._sdw_vkd3d_compile_options === 'function' : undefined;
}

// Set once a pre-#295 module has been asked for options it cannot take (one warning per
// page, not one per compile).
let droppedOptionsWarned = false;

// The caller's compile options as 32-bit words: (name, value) pairs, exactly as handed
// over. A missing or odd-length list is a caller bug and is refused here rather than
// compiled with no options (the silent form of the issue #295 defect).
function optionWords(options) {
    if (options === null || options === undefined || typeof options.length !== 'number' || options.length % 2 !== 0) {
        throw new Error('vkd3d-shader WASM: compile() needs the vkd3d compile options as (name, value) pairs ' +
            '(Vkd3dCompileContract.ResolveCompileOptions; an empty list for none).');
    }
    return Uint32Array.from(options, (v) => v >>> 0);
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
 * profile: string, sourceName: string, targetType: number, options: number[]):
 * { code: Uint8Array, messages: string }, throwing a plain Error on failure whose
 * message is vkd3d's VERBATIM diagnostic text (surfaced to .NET as JSException and
 * parsed by the shared Vkd3dCompileContract.MapCompileFailure — constraint 5, no
 * swallowing). On success `messages` is vkd3d's verbatim message text too ('' when it
 * said nothing): the non-fatal diagnostics the managed side turns into warnings through
 * the same shared contract the desktop uses (issue #335).
 *
 * @param {Uint8Array} sourceUtf8 Preprocessed, #include-flattened HLSL as UTF-8 bytes.
 * @param {string}     entryPoint Shader entry point (C string at the ABI).
 * @param {string}     profile    Shader profile, e.g. "ps_5_0" / "vs_2_0" (C string).
 * @param {string}     sourceName Diagnostic source name (C string; may be null).
 * @param {number}     targetType 4 = D3D_BYTECODE (SM1-3, FNA), 5 = DXBC_TPF (SM4/5, DX11).
 * @param {ArrayLike<number>} options The vkd3d_shader_compile_option list as flat
 *        (name, value) pairs: Vkd3dCompileContract.ResolveCompileOptions, the same list
 *        the desktop backend passes. REQUIRED (an empty list for none); forwarded to
 *        vkd3d untouched, never extended or defaulted here.
 * @returns {{ code: Uint8Array, messages: string }} The compiled bytecode (copied out of
 *        the WASM heap) and vkd3d's verbatim message text.
 */
export function compile(sourceUtf8, entryPoint, profile, sourceName, targetType, options) {
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
    const words = optionWords(options);
    const optionCount = words.length / 2;
    const takesOptions = typeof mod._sdw_vkd3d_compile_options === 'function';
    if (!takesOptions && optionCount > 0 && !droppedOptionsWarned) {
        droppedOptionsWarned = true;
        console.warn('ShadowDusk: the loaded vkd3d-shader WASM module predates issue #295 and cannot take ' +
            'vkd3d compile options, so it compiles without them. A DirectX (SM4+) shader with SM1-3 ' +
            'semantics on struct fields (POSITION0 / COLOR0) compiles differently from the desktop, or ' +
            'fails with E5013. Restore the current module (tools/restore.*).');
    }

    let srcPtr = 0, entryPtr = 0, profilePtr = 0, namePtr = 0, optsPtr = 0, outPtrs = 0;
    let trapped = false;
    try {
        // Source bytes: raw UTF-8 + explicit length (NOT null-terminated at the ABI).
        // Empty source is NOT pre-judged here — it goes to vkd3d (pointer + length 0)
        // and vkd3d speaks for itself, exactly like the desktop backend (Phase 27;
        // Vkd3dShaderCompiler.CompileCore allocates max(1, len) the same way because
        // a zero-byte allocation may legally return a null pointer).
        srcPtr = mod._malloc(Math.max(source.length, 1));
        if (!srcPtr) throw new Error(`vkd3d-shader WASM: _malloc(${Math.max(source.length, 1)}) failed (out of memory).`);
        mod.HEAPU8.set(source, srcPtr);

        // C strings for entry point / profile / source name.
        entryPtr = allocCString(mod, entryPoint);
        profilePtr = allocCString(mod, profile);
        namePtr = allocCString(mod, sourceName);

        // The caller's compile options, as (name, value) pairs of 32-bit words.
        if (takesOptions && optionCount > 0) {
            optsPtr = mod._malloc(words.length * 4);
            if (!optsPtr) throw new Error(`vkd3d-shader WASM: _malloc(${words.length * 4}) failed (out of memory).`);
            const view = new DataView(mod.HEAPU8.buffer);
            words.forEach((word, i) => view.setUint32(optsPtr + 4 * i, word, /* littleEndian */ true));
        }

        // out_code / out_size / out_messages — three contiguous 32-bit out-slots.
        outPtrs = mod._malloc(12);
        if (!outPtrs) throw new Error('vkd3d-shader WASM: _malloc(12) failed (out of memory).');
        mod.HEAPU8.fill(0, outPtrs, outPtrs + 12);
        const outCodePtr = outPtrs, outSizePtr = outPtrs + 4, outMsgsPtr = outPtrs + 8;

        let rc;
        try {
            rc = takesOptions
                ? mod._sdw_vkd3d_compile_options(
                    srcPtr, source.length,
                    entryPtr, profilePtr, namePtr, targetType | 0,
                    optsPtr, optionCount,
                    outCodePtr, outSizePtr, outMsgsPtr)
                // A module built before issue #295: no option parameters (see the header).
                : mod._sdw_vkd3d_compile(
                    srcPtr, source.length,
                    entryPtr, profilePtr, namePtr, targetType | 0,
                    outCodePtr, outSizePtr, outMsgsPtr);
        } catch (e) {
            // vkd3d reports diagnostics through out_messages, so anything THROWN out of the
            // module is a trap (stack overflow, out-of-bounds access, abort). The instance's
            // memory and C state are now undefined: drop it (the frees in the finally below are
            // skipped too) so the next ensureReady() instantiates a fresh module (issue #271,
            // the slangc pattern from PR #266). WasmVkd3dShaderCompiler keys SD1907 on the
            // 'vkd3d trapped:' prefix.
            trapped = true;
            vkd3dInstance = null;
            loadPromise = null;
            throw new Error('vkd3d trapped: ' + (e instanceof Error ? `${e.name}: ${e.message}` : String(e)));
        }

        // Messages first (present on failure AND on warning-bearing success); always
        // freed via the ABI's own free function. Read VERBATIM: never trimmed, filtered
        // or reformatted here (the managed side parses the exact text, issue #335).
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
            // Copy the bytecode OUT of the WASM heap before freeing it, and hand back the
            // message text beside it: on a successful compile it carries vkd3d's non-fatal
            // diagnostics, which the desktop surfaces as warnings and so must this host.
            return {
                code: new Uint8Array(mod.HEAPU8.subarray(codePtr, codePtr + codeSize)),
                messages,
            };
        } finally {
            if (codePtr) mod._sdw_vkd3d_free_code(codePtr);
        }
    } finally {
        // Never call back into a trapped instance; it is discarded with its whole heap.
        if (!trapped) {
            if (outPtrs) mod._free(outPtrs);
            if (optsPtr) mod._free(optsPtr);
            if (namePtr) mod._free(namePtr);
            if (profilePtr) mod._free(profilePtr);
            if (entryPtr) mod._free(entryPtr);
            if (srcPtr) mod._free(srcPtr);
        }
    }
}
