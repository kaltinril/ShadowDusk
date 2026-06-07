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
/// </list>
/// Injecting the reflector makes the OpenGL path reflect SPIR-V directly and skip the
/// native DXIL reflection oracle, so no Windows-only reflection is ever required.
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
            reflectorFactory: () => new SpirvReflector());

        return compiler.CompileAsync(hlslSource, options, cancellationToken);
    }
}
