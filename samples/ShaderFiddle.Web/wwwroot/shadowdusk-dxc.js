// ShadowDusk Phase 19 mode-2 HLSL -> SPIR-V backend — REAL in-browser compiler.
//
// This is the app-side registration of the `shadowdusk-dxc` [JSImport] module
// contract (see src/ShadowDusk.Wasm/Phase19.js and JsShaderBackends.cs:
// `[JSImport("compileToSpirv", "shadowdusk-dxc")]  static partial byte[]
// CompileToSpirv(string hlslSource, string[] args)`).
//
// Backend: **Slang** (shader-slang) compiled to WebAssembly — the official
// `slang-wasm.{js,wasm}` embind build (Slang v2026.10), vendored under
// ./slang/. We do NOT use DXC: DXC has no maintained WASM build, whereas Slang
// ships a prebuilt browser WASM with an embind API and compiles HLSL-syntax
// source straight to SPIR-V. A desktop spike proved Slang's SPIR-V flows through
// ShadowDusk's pure-managed SpirvReflector unchanged (Grayscale + TintShader both
// reflect identically to the DXC/-fvk-use-dx-layout oracle for the SM3 PS corpus).
//
// The incoming `args` are the EXACT DXC argument list ShadowDusk's DxcFlagBuilder
// produced for the OpenGL/SPIR-V target, e.g.:
//     -E MainPS -T ps_5_0 -spirv -fvk-use-dx-layout -auto-binding-space 1 -Zpr -WX
// We translate the parts that have a Slang equivalent and ignore DXC-only flags:
//   -E <entry>            -> entry-point name passed to findAndCheckEntryPoint
//   -T ps_5_0 / vs_ / cs_ -> Slang stage (pixel=FRAGMENT / vertex / compute)
//   -spirv                -> implied (we always target SPIRV)
//   -Zpr                  -> row-major matrices: ALREADY Slang's default, so a no-op
//   -fvk-use-entrypoint-name -> see note below
//   -fvk-use-dx-layout    -> see note below
//   -auto-binding-space N -> DXC-only flat binding allocation; not needed (the
//                            reflector buckets resources per-class itself)
//   -WX                   -> warnings-as-errors; the playground embind surface has
//                            no equivalent; Slang still throws on hard errors
//
// NOTE on -fvk-use-dx-layout / -fvk-use-entrypoint-name:
//   The slang-wasm *embind* surface only exposes `globalSession.createSession(targetEnum)`
//   — it constructs a SessionDesc with DEFAULT compiler options and gives no way to
//   pass CompilerOptionEntry flags from JS. So those two DXC flags cannot be forwarded.
//   In practice it does not matter for ShadowDusk's SPIR-V consumer:
//     * Matrix layout: Slang defaults to ROW-MAJOR (same as DXC -Zpr), and for the
//       PS corpus the only cbuffer member is a float4, which packs identically under
//       row-major and -fvk-use-dx-layout (verified: TintColor @ offset 0, size 16).
//     * Entry-point name: this build emits OpEntryPoint "main" (the OpName of the
//       function is still the source name, e.g. "MainPS", and the output var is
//       "entryPointParam_MainPS"). ShadowDusk's SpirvReflector keys on types and
//       decorations, NOT the entry-point name, so "main" vs "MainPS" is irrelevant
//       to reflection. (Verified end-to-end against the real SpirvReflector.)
//
// The [JSImport] compileToSpirv is SYNCHRONOUS (returns byte[], not a Task), so it
// must not be async. But loading the ~21 MB slang-wasm is async, and we must NOT
// block module evaluation on it — JSHost.ImportAsync (the host's registration call)
// awaits module evaluation during page init, so a top-level `await loadSlang()` would
// stall the whole UI boot (and the mode-1 cat render) behind a 21 MB download.
//
// Instead: load lazily and expose an async `ensureReady()` the host awaits ONCE before
// the first compile (JsDxcShaderCompiler.CompileAsync calls it via a separate async
// [JSImport]). The synchronous compileToSpirv then just uses the already-loaded module.
// Throwing a plain Error on failure is what surfaces to .NET as JSException -> SD1900.

// SLANG SlangStage enum values (slang.h: SLANG_STAGE_*). Only these are needed here.
const SLANG_STAGE_VERTEX = 1;
const SLANG_STAGE_FRAGMENT = 5;
const SLANG_STAGE_COMPUTE = 6;

// Loaded once. `slangModule` is the embind MainModule; `globalSession` is reused
// across compiles (the playground reuses a single global session the same way).
let slangModule = null;
let globalSession = null;
let spirvTargetValue = 0;
let initError = null;
let loadPromise = null;   // in-flight/settled load; ensures we load exactly once

// Resolve slang-wasm relative to THIS module's URL so it works identically whether
// served from wwwroot/ in the browser or imported from disk under node. The emscripten
// loader finds slang-wasm.wasm via its own import.meta.url (co-located in ./slang/),
// so no explicit locateFile is required.
async function loadSlang() {
    const factoryUrl = new URL('./slang/slang-wasm.js', import.meta.url).href;
    const factoryModule = await import(factoryUrl);
    const Module = factoryModule.default;
    const mod = await Module();

    const gs = mod.createGlobalSession();
    if (!gs) {
        const e = mod.getLastError ? mod.getLastError() : null;
        throw new Error('slang-wasm: createGlobalSession failed' + (e ? `: ${e.type} ${e.message}` : ''));
    }

    // Resolve the SPIRV target enum dynamically (do not hardcode — getCompileTargets
    // returns [{name,value},...]; SPIRV's numeric value is not part of the public API).
    const targets = mod.getCompileTargets();
    let spirv = 0;
    for (let i = 0; i < targets.length; i++) {
        if (targets[i].name === 'SPIRV') { spirv = targets[i].value; break; }
    }
    if (!spirv) throw new Error('slang-wasm: SPIRV target not found in getCompileTargets()');

    slangModule = mod;
    globalSession = gs;
    spirvTargetValue = spirv;
}

/**
 * Idempotently load + initialize the Slang WASM compiler. Resolves when the
 * compiler is ready (or rejects with the load error). The host MUST await this
 * once before the first compileToSpirv call. Safe to call repeatedly; the heavy
 * work happens exactly once.
 *
 * Exposed to .NET via [JSImport("ensureReady","shadowdusk-dxc")] Task EnsureReadyAsync().
 * @returns {Promise<void>}
 */
export function ensureReady() {
    if (slangModule && globalSession) return Promise.resolve();
    if (!loadPromise) {
        loadPromise = loadSlang().catch((e) => {
            initError = e instanceof Error ? e : new Error(String(e));
            // Re-throw so the awaiting host sees the failure (surfaced as SD1900).
            throw initError;
        });
    }
    return loadPromise;
}

/**
 * Parse the DXC `-T` target profile (e.g. "ps_5_0", "vs_6_0", "cs_5_1") into a
 * Slang stage enum. Defaults to FRAGMENT (the OpenGL PS-only corpus is the only
 * path ShadowDusk's WASM compiler is asked to take today).
 */
function stageFromProfile(profile) {
    if (!profile) return SLANG_STAGE_FRAGMENT;
    const p = profile.toLowerCase();
    if (p.startsWith('vs_')) return SLANG_STAGE_VERTEX;
    if (p.startsWith('cs_')) return SLANG_STAGE_COMPUTE;
    // ps_*, and anything else, compile as a pixel/fragment shader.
    return SLANG_STAGE_FRAGMENT;
}

/** Extract { entry, profile } from the DXC argument list. */
function parseArgs(args) {
    let entry = null;
    let profile = null;
    const a = args || [];
    for (let i = 0; i < a.length; i++) {
        if (a[i] === '-E' && i + 1 < a.length) entry = a[i + 1];
        else if (a[i] === '-T' && i + 1 < a.length) profile = a[i + 1];
    }
    return { entry, profile };
}

/**
 * Compile HLSL source to a SPIR-V byte stream via Slang (WASM).
 * JS contract: compileToSpirv(hlslSource: string, args: string[]): Uint8Array.
 * Throws a plain Error on any failure (surfaced to .NET as JSException -> SD1900).
 *
 * @param {string}   hlslSource  Preprocessed, #include-flattened HLSL.
 * @param {string[]} args        DXC command-line arguments (see header).
 * @returns {Uint8Array}         Compiled SPIR-V module (little-endian word stream).
 */
export function compileToSpirv(hlslSource, args) {
    if (initError) {
        throw new Error('Slang WASM compiler failed to initialize: ' + initError.message);
    }
    if (!slangModule || !globalSession) {
        // Should not happen (top-level await completed before registration resolved),
        // but fail loudly rather than silently returning empty SPIR-V.
        throw new Error('Slang WASM compiler is not ready.');
    }
    if (typeof hlslSource !== 'string' || hlslSource.length === 0) {
        throw new Error('compileToSpirv: empty HLSL source.');
    }

    const { entry, profile } = parseArgs(args);
    if (!entry) {
        throw new Error('compileToSpirv: no entry point (-E <name>) in arguments.');
    }
    const stage = stageFromProfile(profile);

    const lastError = () => {
        const e = slangModule.getLastError ? slangModule.getLastError() : null;
        return e && e.message ? `${e.type} error: ${e.message}` : 'see log';
    };

    let session = null;
    try {
        session = globalSession.createSession(spirvTargetValue);
        if (!session) {
            throw new Error('Slang createSession(SPIRV) failed: ' + lastError());
        }

        // Load the user's HLSL as a Slang module. Slang accepts HLSL syntax directly.
        const module = session.loadModuleFromSource(hlslSource, 'user', '/user.slang');
        if (!module) {
            throw new Error('Slang compile error: ' + lastError());
        }

        // Resolve and type-check the requested entry point at the requested stage.
        const entryPoint = module.findAndCheckEntryPoint(entry, stage);
        if (!entryPoint) {
            throw new Error(`Slang: entry point '${entry}' not found / failed type-check: ${lastError()}`);
        }

        const program = session.createCompositeComponentType([module, entryPoint]);
        const linked = program.link();
        if (!linked) {
            throw new Error('Slang link failed: ' + lastError());
        }

        // getEntryPointCodeBlob(entryPointIndex=0, targetIndex=0) returns the SPIR-V
        // binary for our single entry point as a Uint8Array (emscripten copies the
        // ISlangBlob bytes into JS heap). This is exactly the little-endian SPIR-V
        // word stream ShadowDusk's SpirvReflector parses.
        const blob = linked.getEntryPointCodeBlob(0, 0);
        if (!blob || blob.byteLength === 0) {
            throw new Error('Slang produced empty SPIR-V: ' + lastError());
        }

        // Copy out of the WASM-owned view into a standalone Uint8Array so the bytes
        // survive after we delete the session (the embind view may alias WASM heap).
        const out = new Uint8Array(blob.byteLength);
        out.set(blob);

        // Sanity: SPIR-V magic 0x07230203 (little-endian: 03 02 23 07).
        if (out.length < 4 || out[0] !== 0x03 || out[1] !== 0x02 || out[2] !== 0x23 || out[3] !== 0x07) {
            throw new Error('Slang output is not a SPIR-V module (bad magic word).');
        }
        return out;
    } finally {
        // Free the per-compile session; keep the cached global session + module alive.
        if (session && typeof session.delete === 'function') {
            try { session.delete(); } catch { /* ignore double-free */ }
        }
    }
}
