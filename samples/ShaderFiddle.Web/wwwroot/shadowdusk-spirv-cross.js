// ShadowDusk Phase 19 mode-2 SPIRV-Cross backend — app-side registration of the
// `shadowdusk-spirv-cross` [JSImport] module contract (see
// src/ShadowDusk.Wasm/Phase19.js and JsShaderBackends.cs:
// [JSImport("transpileToGlsl", "shadowdusk-spirv-cross")]).
//
// STUB: the emscripten WASM build of SPIRV-Cross is deferred to Phase 100.
// Registering this module makes the failure graceful (catchable JSException ->
// ShaderError SD1901) rather than aborting the .NET WASM runtime. The DXC stub
// throws first, so transpile is not reached today; this is kept faithful to the
// contract for when Phase 100 wires both.
//
// When Phase 100 lands: replace the throw with a real call into a WASM-compiled
// SPIRV-Cross, returning the GLSL source string.
export function transpileToGlsl(spirv, flipVertexY, fixupDepthConvention, glslVersion, glslEs, vulkanSemantics) {
    throw new Error('in-browser SPIRV-Cross unavailable — WASM module deferred to Phase 100 (mode 2 not yet wired).');
}
