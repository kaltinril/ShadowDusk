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
        // The FNA fx_2_0 path needs vkd3d-shader's SM1–3 backend, which has no WASM build
        // yet. Per the one-faithful-pipeline rule a host must never swap in a substitute
        // compiler — so this host's FNA support is "not done", reported loudly, rather than
        // letting the native P/Invoke fail with a misleading desktop-restore message.
        if (options.Target == PlatformTarget.Fna)
        {
            return Task.FromResult(Result<CompiledShader, ShaderError[]>.Fail(
            [
                new ShaderError(
                    File: options.SourceFileName ?? "<source>",
                    Line: 0,
                    Column: 0,
                    Code: "SD0304",
                    Message: "The FNA (fx_2_0) target is not supported in the browser/WASM host: " +
                             "its SM1–3 backend (vkd3d-shader) has no WASM build. Compile FNA " +
                             "effects with the desktop library or the ShadowDuskCLI tool."),
            ]));
        }

        var compiler = new EffectCompiler(
            dxcCompilerFactory: () => new JsDxcShaderCompiler(),
            glslTranspilerFactory: () => new JsSpirvToGlslTranspiler(),
            reflectorFactory: () => new SpirvReflector());

        return compiler.CompileAsync(hlslSource, options, cancellationToken);
    }
}
