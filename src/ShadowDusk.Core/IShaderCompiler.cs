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

    /// <summary>
    /// One-time warm-up that makes the synchronous <see cref="Compile"/> usable from a
    /// synchronous call site. Idempotent and safe to await repeatedly. On the browser/WASM
    /// host this loads and instantiates every WASM compiler module a subsequent
    /// <see cref="Compile"/> of any supported target needs (DXC, SPIRV-Cross, and
    /// vkd3d-shader); on desktop it is effectively a no-op (the native compilers load
    /// lazily on first use).
    /// </summary>
    /// <remarks>
    /// Await this once from a legal async context — e.g. the Blazor bootstrap or an
    /// <c>async Main</c> — before calling <see cref="Compile"/> from synchronous code such
    /// as MonoGame/KNI's <c>Content.Load&lt;Effect&gt;</c>. Never block on it
    /// (<c>.Result</c> / <c>.Wait()</c>): on single-threaded browser WASM that is the
    /// exact sync-over-async deadlock this API exists to avoid. A failure to load a
    /// required module (e.g. the module asset cannot be fetched) is thrown from the
    /// returned task — loudly, with the underlying loader diagnostics.
    /// </remarks>
    /// <param name="cancellationToken">Token used to cancel the warm-up.</param>
    /// <returns>A task that completes when the compiler is ready for synchronous compiles.</returns>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous counterpart of <see cref="CompileAsync"/>: compiles the given HLSL
    /// source into a compiled effect for the target in <paramref name="options"/>, on the
    /// calling thread, returning the effect bytes in memory. Intended for synchronous call
    /// sites that cannot await — e.g. compiling inside MonoGame/KNI's
    /// <c>Content.Load&lt;Effect&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Runs the exact same pipeline as <see cref="CompileAsync"/> (one shared
    /// implementation, never a fork), so for the same source, options, and compiler
    /// version the output bytes are identical. The whole compile runs on the calling
    /// thread and never blocks on a task internally — safe on single-threaded browser
    /// WASM. <b>Precondition on the browser/WASM host:</b> <see cref="InitializeAsync"/>
    /// must have completed first (the WASM compiler modules load asynchronously);
    /// otherwise this returns a clear <see cref="ShaderError"/> (code <c>SD1903</c>)
    /// telling the caller to await <see cref="InitializeAsync"/>. On desktop no prior
    /// initialization is required.
    /// </remarks>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to compile.</param>
    /// <param name="options">
    /// Compilation settings: the <see cref="PlatformTarget"/>, include resolution,
    /// debug mode, MGFX version, and DirectX DXBC backend selection.
    /// </param>
    /// <param name="cancellationToken">Token observed between pipeline stages.</param>
    /// <returns>
    /// A <see cref="Result{T, TError}"/> that is either a successful
    /// <see cref="CompiledShader"/> (the target plus its effect bytes) or, on failure,
    /// an array of <see cref="ShaderError"/> with source file, line, column, code, and message.
    /// Compilation failures are returned as errors, not thrown as exceptions.
    /// </returns>
    Result<CompiledShader, ShaderError[]> Compile(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default);
}
