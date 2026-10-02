// ShadowDusk: in-browser slangc for the `shadowdusk-slangc` [JSImport] module contract
// (src/ShadowDusk.Slang.Wasm/SlangcModule.cs):
//   [JSImport("ensureReady", "shadowdusk-slangc")] static partial Task     EnsureReadyJs();
//   [JSImport("runSlangc",   "shadowdusk-slangc")] static partial string[] RunSlangc(string source, string[] args);
//
// Issue #257. The module under ./slangc/ is the pinned slangc (v2026.14.1) linked from
// upstream's OWN prebuilt wasm static libraries plus a thin glue that replays slangc's
// innerMain (.wasm-build/slang-wasm/slangc-wasm-glue.cpp). `args` is the exact list
// ShadowDusk.Slang's SlangcArguments.Build produces and is handed to slang's own
// command-line parser VERBATIM: no flag translation here (the property upstream's own
// session-API slang-wasm lacks, which is why that build is not used; see Phase 67).
//
// Slang is an INPUT language only: this module turns .slang into HLSL text. The HLSL then
// goes through the faithful DXC/vkd3d modules of ShadowDusk.Wasm like any other .fx body.
//
// Loading mirrors shadowdusk-dxc.js: evaluating this module is instant; ensureReady()
// fetches and instantiates the ~23 MB wasm once (retrying after a failed attempt), and
// runSlangc() is synchronous against the instantiated module.

let instance = null;
let loadPromise = null;
let initError = null;

async function load() {
    const factoryUrl = new URL('./slangc/shadowdusk-slangc.js', import.meta.url).href;
    const factory = (await import(factoryUrl)).default;
    if (typeof factory !== 'function') {
        throw new Error('shadowdusk-slangc.js did not export a default factory.');
    }
    const mod = await factory();
    if (!mod || typeof mod.runSlangc !== 'function') {
        throw new Error('the slangc module is missing the runSlangc embind binding.');
    }
    instance = mod;
}

export function ensureReady() {
    if (instance) return Promise.resolve();
    if (!loadPromise) {
        initError = null;
        loadPromise = load().catch((e) => {
            initError = e instanceof Error ? e : new Error(String(e));
            loadPromise = null;
            throw initError;
        });
    }
    return loadPromise;
}

/**
 * Runs slangc once.
 * @param {string}   source  The Slang source (what the desktop route pipes to stdin).
 * @param {string[]} args    slangc's command line without the executable name.
 * @returns {string[]}       [exitCode, stdout, stderr]
 */
export function runSlangc(source, args) {
    if (initError) {
        throw new Error('The in-browser slangc module failed to initialize: ' + initError.message);
    }
    if (!instance) {
        throw new Error('The in-browser slangc module is not loaded; await ensureReady() first.');
    }
    const r = instance.runSlangc(source, args);
    return [String(r.exitCode), r.stdout, r.stderr];
}
