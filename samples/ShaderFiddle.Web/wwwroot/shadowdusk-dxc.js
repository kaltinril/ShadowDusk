// ShadowDusk Phase 19 mode-2 DXC backend — the app-side registration of the
// `shadowdusk-dxc` [JSImport] module contract (see src/ShadowDusk.Wasm/Phase19.js
// and JsShaderBackends.cs: [JSImport("compileToSpirv", "shadowdusk-dxc")]).
//
// STUB: the emscripten WASM build of DXC is deferred to Phase 100. Registering
// this module via JSHost.ImportAsync (instead of leaving it unregistered) is
// what makes the failure GRACEFUL: the [JSImport] call dispatches here and
// throws a *catchable* JS Error — surfaced to .NET as a JSException and reported
// as ShaderError SD1900 — instead of the .NET WASM runtime aborting on an
// unresolved module import (which would crash the page).
//
// When Phase 100 lands: replace the throw with a real call into a WASM-compiled
// DXC, returning a Uint8Array of SPIR-V for (hlslSource, args).
export function compileToSpirv(hlslSource, args) {
    throw new Error('in-browser DXC unavailable — WASM DXC module deferred to Phase 100 (mode 2 not yet wired).');
}
