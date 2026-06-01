// Phase 19 — WASM Shader Fiddle (Mode 2) host JavaScript contract.
//
// ShadowDusk.Wasm composes the real managed compilation pipeline (EffectCompiler)
// with two browser-backed native stages reached through [JSImport]. The .NET WASM
// host must register the two JS modules below BEFORE calling WasmShaderCompiler.
//
// Registration (host responsibility), conceptually:
//
//   import { dotnet } from './_framework/dotnet.js';
//   const { setModuleImports } = await dotnet.create();
//   setModuleImports('shadowdusk-dxc',          { compileToSpirv });
//   setModuleImports('shadowdusk-spirv-cross',  { transpileToGlsl });
//
// The two functions are backed by WASM-compiled DXC and SPIRV-Cross respectively.
// On ANY error each function MUST throw (the managed side catches the resulting
// JSException and surfaces it as a ShaderError — never return a partial/empty blob).

// --------------------------------------------------------------------------
// Module "shadowdusk-dxc"
//
// [JSImport("ensureReady", "shadowdusk-dxc")]
//   [return: JSMarshalAs<JSType.Promise<JSType.Void>>] static partial Task EnsureReadyAsync();
// [JSImport("compileToSpirv", "shadowdusk-dxc")]
//   static partial byte[] CompileToSpirv(string hlslSource, string[] args);
//
// args is the EXACT DXC argument list ShadowDusk built via DxcFlagBuilder for the
// OpenGL/SPIR-V target (e.g. -E <entry> -T ps_5_0 -spirv -fvk-use-dx-layout
// -auto-binding-space 1 -Zpr -WX ...). The compile backend (slang-wasm — Slang's
// prebuilt WebAssembly build, which accepts HLSL syntax) translates the parts with a
// Slang equivalent and ignores DXC-only flags; the desktop spike proved its SPIR-V
// reflects identically through ShadowDusk's SpirvReflector. See
// samples/ShaderFiddle.Web/wwwroot/shadowdusk-dxc.js for the real implementation.
//
// ensureReady() lazily loads the ~21 MB WASM exactly once and resolves when ready;
// JsDxcShaderCompiler.CompileAsync awaits it before the (synchronous) compileToSpirv,
// so the heavy download stays off the page-init path.
//
// @returns {Promise<void>}      ensureReady — rejects (-> SD1900) on load failure.
// @param {string}   hlslSource  Preprocessed, #include-flattened HLSL.
// @param {string[]} args        DXC command-line arguments.
// @returns {Uint8Array}         Compiled SPIR-V module (little-endian word stream).
// @throws on compile failure (message should carry compiler diagnostics).
function ensureReady() {
    return Promise.reject(new Error('shadowdusk-dxc.ensureReady not wired to a WASM build'));
}
function compileToSpirv(hlslSource, args) {
    throw new Error('shadowdusk-dxc.compileToSpirv not wired to a WASM build');
}

// --------------------------------------------------------------------------
// Module "shadowdusk-spirv-cross"
//
// [JSImport("transpileToGlsl", "shadowdusk-spirv-cross")]
//   static partial string TranspileToGlsl(
//       byte[] spirv, bool flipVertexY, bool fixupDepthConvention,
//       int glslVersion, bool glslEs, bool vulkanSemantics);
//
// The five option arguments mirror EXACTLY what the desktop SpirvCrossGlslTranspiler
// installs, so the browser-emitted GLSL is identical to the desktop output:
//   flipVertexY=true, fixupDepthConvention=true, glslVersion=140,
//   glslEs=false, vulkanSemantics=false.
// The host must also call spvc_compiler_build_combined_image_samplers() before
// compiling (as the desktop transpiler does) so textured shaders do not crash.
//
// @param {Uint8Array} spirv                 SPIR-V module bytes.
// @param {boolean}    flipVertexY           SPVC_COMPILER_OPTION_FLIP_VERTEX_Y.
// @param {boolean}    fixupDepthConvention  SPVC_COMPILER_OPTION_FIXUP_DEPTH_CONVENTION.
// @param {number}     glslVersion           SPVC_COMPILER_OPTION_GLSL_VERSION (140).
// @param {boolean}    glslEs                SPVC_COMPILER_OPTION_GLSL_ES.
// @param {boolean}    vulkanSemantics       SPVC_COMPILER_OPTION_GLSL_VULKAN_SEMANTICS.
// @returns {string}                         GLSL source text.
// @throws on transpile failure (message should carry the SPIRV-Cross error string).
function transpileToGlsl(spirv, flipVertexY, fixupDepthConvention, glslVersion, glslEs, vulkanSemantics) {
    // return Module.spirvCrossToGlsl(spirv, { flipVertexY, fixupDepthConvention, glslVersion, glslEs, vulkanSemantics });
    throw new Error('shadowdusk-spirv-cross.transpileToGlsl not wired to a WASM SPIRV-Cross build');
}

export { ensureReady, compileToSpirv, transpileToGlsl };
