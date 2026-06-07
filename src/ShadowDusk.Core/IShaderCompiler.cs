#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The core consumer contract: compiles HLSL <c>.fx</c> source into a MonoGame/KNI
/// <c>.mgfx</c> effect entirely in memory, with no <c>fxc.exe</c>, <c>mgfxc</c>, Wine, or
/// Windows SDK required. This is the product's public entry point — add the library to a
/// MonoGame/KNI project and call <see cref="CompileAsync"/> at runtime or build time.
/// </summary>
/// <remarks>
/// The same contract abstracts every delivery shape: the in-process desktop library
/// (<c>EffectCompiler</c>), the CLI, and the in-browser WASM compiler all implement it and
/// produce the same <c>.mgfx</c> bytes for a given source and target. Output is
/// behaviorally equivalent to <c>mgfxc</c>'s — it loads into MonoGame's <c>Effect</c> and
/// renders the same image — but is not byte-identical to <c>mgfxc</c> (the two are different
/// compilers). Determinism is ShadowDusk's own reproducibility only: the same ShadowDusk
/// version, source, and target yield the same bytes.
/// </remarks>
public interface IShaderCompiler
{
    /// <summary>
    /// Compiles the given HLSL source into a compiled effect for the target in
    /// <paramref name="options"/>, returning the <c>.mgfx</c> bytes in memory.
    /// </summary>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to compile.</param>
    /// <param name="options">
    /// Compilation settings: the <see cref="PlatformTarget"/>, include resolution,
    /// debug mode, MGFX version, and DirectX DXBC backend selection.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel a long-running compile.</param>
    /// <returns>
    /// A <see cref="Result{T, TError}"/> that is either a successful
    /// <see cref="CompiledShader"/> (the target plus its <c>.mgfx</c> bytes) or, on failure,
    /// an array of <see cref="ShaderError"/> with source file, line, column, code, and message.
    /// Compilation failures are returned as errors, not thrown as exceptions.
    /// </returns>
    Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default);
}
