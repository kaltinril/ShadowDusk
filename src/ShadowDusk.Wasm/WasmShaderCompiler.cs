#nullable enable

using System.Runtime.Versioning;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Wasm;

/// <summary>
/// Browser/WASM <see cref="IShaderCompiler"/>. Composes the real managed compilation
/// pipeline (<see cref="EffectCompiler"/>) with browser-backed native stages:
/// <list type="bullet">
///   <item>HLSL → SPIR-V via <see cref="JsDxcShaderCompiler"/> (host JS, WASM DXC).</item>
///   <item>SPIR-V → GLSL via <see cref="JsSpirvToGlslTranspiler"/> (host JS, WASM SPIRV-Cross).</item>
///   <item>SPIR-V reflection via <see cref="SpirvReflector"/> (pure managed — runs in-browser).</item>
///   <item>HLSL → DXBC (DirectX) / D3D9 bytecode (FNA) via
///   <see cref="WasmVkd3dShaderCompiler"/> (host JS, WASM vkd3d-shader — Phase 4.1).</item>
/// </list>
/// Injecting the reflector makes the OpenGL path reflect SPIR-V directly and skip the
/// native DXIL reflection oracle, so no Windows-only reflection is ever required.
/// Injecting the vkd3d backend routes <see cref="PlatformTarget.DirectX"/> and
/// <see cref="PlatformTarget.Fna"/> through the SAME pinned vkd3d 1.17 the desktop
/// uses, compiled to WASM (never a substitute compiler) — the rest of both pipelines
/// (RdefReader, Fx2EffectWriter, D3d9BytecodePatcher, CTAB reflection, MGFX writer) is
/// pure managed C# that already runs in-browser. The host-appropriate backend is
/// injected automatically — the consumer never sets a flag to get correct output.
/// (This replaced the Phase 39 SD0304 "FNA unavailable on WASM" guard; if the vkd3d
/// WASM module itself cannot load, the compile fails loudly with SD1902 instead.)
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WasmShaderCompiler : IShaderCompiler
{
    /// <inheritdoc/>
    public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        var compiler = new EffectCompiler(
            dxcCompilerFactory: () => new JsDxcShaderCompiler(),
            glslTranspilerFactory: () => new JsSpirvToGlslTranspiler(),
            reflectorFactory: () => new SpirvReflector(),
            dxbcCompilerFactory: () => new WasmVkd3dShaderCompiler());

        return compiler.CompileAsync(hlslSource, options, cancellationToken);
    }
}
