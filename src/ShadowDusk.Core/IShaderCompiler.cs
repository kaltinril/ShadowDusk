#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The core consumer contract: compiles HLSL <c>.fx</c> source into a compiled effect
/// entirely in memory, with no <c>fxc.exe</c>, <c>mgfxc</c>, Wine, or Windows SDK required.
/// For the MonoGame/KNI targets the output is a <c>.mgfx</c> effect; for
/// <see cref="PlatformTarget.Fna"/> it is the D3D9 fx_2_0 effects binary (<c>.fxb</c>) FNA
/// loads. This is the product's public entry point — add the library to a project and call
/// <see cref="CompileAsync"/> at runtime or build time.
/// </summary>
/// <remarks>
/// The same contract abstracts every delivery shape: the in-process desktop library
/// (<c>EffectCompiler</c>), the CLI, and the in-browser WASM compiler all implement it and
/// produce the same bytes for a given source and target. Output is behaviorally equivalent
/// to the reference compiler's (<c>mgfxc</c> for MonoGame/KNI targets; <c>fxc /T fx_2_0</c>
/// for FNA) — it loads into the real runtime's <c>Effect</c> and renders the same image —
/// but is not byte-identical to it (different compilers). Determinism is ShadowDusk's own
/// reproducibility only: the same ShadowDusk version, source, and target yield the same bytes.
/// </remarks>
public interface IShaderCompiler
{
    /// <summary>
    /// Compiles the given HLSL source into a compiled effect for the target in
    /// <paramref name="options"/>, returning the effect bytes in memory.
    /// </summary>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to compile.</param>
    /// <param name="options">
    /// Compilation settings: the <see cref="PlatformTarget"/>, include resolution,
    /// debug mode, MGFX version, and DirectX DXBC backend selection.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel a long-running compile.</param>
    /// <returns>
    /// A <see cref="Result{T, TError}"/> that is either a successful
    /// <see cref="CompiledShader"/> (the target plus its effect bytes) or, on failure,
    /// an array of <see cref="ShaderError"/> with source file, line, column, code, and message.
    /// Compilation failures are returned as errors, not thrown as exceptions.
    /// </returns>
    Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default);
}
