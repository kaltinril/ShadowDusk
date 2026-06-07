// ShadowDusk — FAITHFUL in-browser HLSL -> SPIR-V backend for the
// `shadowdusk-dxc` [JSImport] module contract (see
// src/ShadowDusk.Wasm/JsShaderBackends.cs:
//   [JSImport("ensureReady",    "shadowdusk-dxc")] static partial Task   EnsureReadyAsync();
//   [JSImport("compileToSpirv", "shadowdusk-dxc")] static partial byte[] CompileToSpirv(string hlsl, string[] args);
//
// THIS IS THE PRODUCT FRONTEND (Phase 23, Option A). It wraps the pinned desktop
// DirectXShaderCompiler compiled to WebAssembly — the SAME compiler + version the
// desktop pipeline uses (Vortice.Dxc 3.3.4 == DXC 1.7.2212.40, commit e043f4a1) —
// so its SPIR-V is byte-identical to the desktop CLI on the corpus (node gate
// .wasm-build/node-test-dxc-wasm.mjs == 10/10). Because the bytes are identical to
// the desktop-validated compiler, the in-browser render is transitively proven.
//
// This is NOT the Slang sample shim. Slang stays sample-only
// (samples/ShaderFiddle.Web/wwwroot/shadowdusk-dxc.js) and is never the product
// frontend: a substitute compiler produces different bytes and forfeits the
// transitive proof (see plan/PHASE-23-in-browser-compilation.md, "no substitute
// compilers"). The faithful module is the one that ships.
//
// KEY DIFFERENCE vs the Slang shim: the incoming `args` are forwarded to DXC
// VERBATIM. The faithful module IS DXC, so it accepts the exact DxcFlagBuilder
// list (-E <entry> -T ps_5_0 -spirv -fvk-use-dx-layout -auto-binding-space 1
// -Zpr -WX). There is NO -E/-T parsing or flag translation here — the property
// Slang lacked, which is precisely why DXC is the faithful path.
//
// The emscripten module is a MODULARIZE + EXPORT_ES6 build:
//   export default createDxcModule;  // factory; await it -> instance
//   instance.compileToSpirv(hlsl: string, args: string[]) -> Uint8Array (embind)
// It locates ./dxc/dxcompiler.wasm via `new URL("dxcompiler.wasm", import.meta.url)`
// relative to the dxcompiler.js it lives next to, so co-locating the two in ./dxc/
// is all the wiring needed (no explicit locateFile). The optional DXIL-validator
// dlopen probe is stubbed inside the module at link time (--js-library
// dxc-dlopen-stub.js), so this shim needs no extra files.
//
// The [JSImport] compileToSpirv is SYNCHRONOUS (returns byte[], not a Task). The
// 17.4 MB dxcompiler.wasm must NOT be downloaded at page init (JSHost.ImportAsync
// awaits this module's evaluation during boot, which also drives the mode-1 render).
// So we DEFER loading: this module evaluates instantly (no top-level await), exposes
// an async ensureReady() the host awaits ONCE before the first compile
// (JsDxcShaderCompiler.CompileAsync awaits it), and the synchronous compileToSpirv
// then uses the already-instantiated module. Throwing a plain Error on failure is
// what surfaces to .NET as a JSException -> ShaderError SD1900.

let dxcInstance = null;     // the instantiated emscripten Module (cached)
let loadPromise = null;     // in-flight/settled load; ensures we load exactly once
let initError = null;       // sticky load failure, surfaced on every later call

async function loadDxc() {
    // Resolve dxcompiler.js relative to THIS module's URL so it works identically
    // whether served from a package's _content/ wwwroot in the browser or imported
    // from disk under node. dxcompiler.js then finds dxcompiler.wasm via its own
    // import.meta.url (co-located in ./dxc/), so no locateFile override is needed.
    const factoryUrl = new URL('./dxc/dxcompiler.js', import.meta.url).href;
    const createDxcModule = (await import(factoryUrl)).default;
    if (typeof createDxcModule !== 'function') {
        throw new Error('dxcompiler.js did not export a default factory (createDxcModule).');
    }

    const mod = await createDxcModule();
    if (!mod || typeof mod.compileToSpirv !== 'function') {
        throw new Error('dxcompiler module is missing the compileToSpirv embind binding.');
    }
    dxcInstance = mod;
}

/**
 * Idempotently load + initialize the faithful DXC->WASM compiler. Resolves when the
 * module is instantiated (or rejects with the load error). The host MUST await this
 * once before the first compileToSpirv call. Safe to call repeatedly; the heavy
 * download + instantiation happen exactly once.
 *
 * Exposed to .NET via [JSImport("ensureReady","shadowdusk-dxc")] Task EnsureReadyAsync().
 * @returns {Promise<void>}
 */
export function ensureReady() {
    if (dxcInstance) return Promise.resolve();
    if (initError) return Promise.reject(initError);
    if (!loadPromise) {
        loadPromise = loadDxc().catch((e) => {
            initError = e instanceof Error ? e : new Error(String(e));
            // Re-throw so the awaiting host sees the failure (surfaced as SD1900).
            throw initError;
        });
    }
    return loadPromise;
}

/**
 * Compile HLSL source to a SPIR-V byte stream via the faithful DXC->WASM module.
 * JS contract (to .NET): compileToSpirv(hlslSource: string, args: string[]): Uint8Array,
 * throwing a plain Error on failure (surfaced to .NET as JSException).
 *
 * Phase 38: the underlying embind module now returns `{ spirv: Uint8Array, error: string }`
 * instead of throwing. A non-empty `error` carries DXC's VERBATIM diagnostics
 * (`file:line:col: error: message`); we re-throw it as a normal Error so the text reaches
 * .NET as a readable `JSException.Message`, where `DxcDiagnosticReformatter` parses it into
 * line/column `ShaderError`s. (A C++ throw would arrive as an opaque `WebAssembly.Exception`
 * under -fwasm-exceptions and the text would be lost.) A back-compat branch still accepts a
 * bare `Uint8Array` from an older glue build, so this shim works before/after the relink.
 *
 * `args` is forwarded to DXC VERBATIM — no parsing, no translation (the faithful
 * module IS DXC and accepts the exact DxcFlagBuilder list).
 *
 * @param {string}   hlslSource  Preprocessed, #include-flattened HLSL.
 * @param {string[]} args        DXC command-line arguments (forwarded as-is).
 * @returns {Uint8Array}         Compiled SPIR-V module (little-endian word stream).
 */
export function compileToSpirv(hlslSource, args) {
    if (initError) {
        throw new Error('Faithful DXC WASM compiler failed to initialize: ' + initError.message);
    }
    if (!dxcInstance) {
        // Should not happen — the host awaits ensureReady() before the first compile —
        // but fail loudly rather than silently returning empty SPIR-V.
        throw new Error('Faithful DXC WASM compiler is not ready (call ensureReady() first).');
    }
    if (typeof hlslSource !== 'string' || hlslSource.length === 0) {
        throw new Error('compileToSpirv: empty HLSL source.');
    }

    // Forward args VERBATIM. embind expects a JS string[]; ensure that shape.
    const dxcArgs = Array.isArray(args) ? args : [];

    const res = dxcInstance.compileToSpirv(hlslSource, dxcArgs);

    // Current glue (Phase 38): a { spirv, error } object. Re-throw DXC's verbatim
    // diagnostic text on failure so .NET can parse line/column from it.
    if (res && typeof res === 'object' && !(res instanceof Uint8Array) && 'error' in res) {
        if (res.error) {
            throw new Error(res.error);
        }
        return validateSpirv(res.spirv);
    }

    // Back-compat: an older glue build returned a bare Uint8Array (and threw on failure).
    return validateSpirv(res);
}

// Copy the embind heap view into a standalone Uint8Array and sanity-check it.
function validateSpirv(out) {
    out = out instanceof Uint8Array ? new Uint8Array(out) : new Uint8Array(out || 0);
    if (out.length === 0) {
        throw new Error('DXC produced empty SPIR-V (compile likely failed; -WX warnings-as-errors).');
    }
    // Sanity: SPIR-V magic 0x07230203 (little-endian byte stream: 03 02 23 07).
    if (out.length < 4 || out[0] !== 0x03 || out[1] !== 0x02 || out[2] !== 0x23 || out[3] !== 0x07) {
        throw new Error('DXC output is not a SPIR-V module (bad magic word).');
    }
    return out;
}
