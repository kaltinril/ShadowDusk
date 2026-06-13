#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Compiles preprocessed HLSL to SPIR-V or DXBC using DXC.
/// NOT thread-safe: do not share a single instance across concurrent compilations.
/// Create one instance per parallel worker or serialize access.
/// </summary>
public interface IDxcShaderCompiler
{
    /// <summary>
    /// Compiles a preprocessed HLSL request to a SPIR-V or DXBC blob via DXC.
    /// </summary>
    /// <param name="request">The compile request: source, entry point, profile, and flags.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The compiled <see cref="PlatformBlob"/> on success, or a <see cref="ShaderError"/>
    /// carrying the DXC diagnostics on failure.
    /// </returns>
    Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        DxcCompileRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous counterpart of <see cref="CompileAsync"/>: compiles the request on the
    /// calling thread. The DXC compile itself is synchronous work on every host —
    /// <see cref="CompileAsync"/> is only a thread-offload (desktop) or one-time-module-load
    /// (WASM) shell over this — so both entry points produce identical bytes by
    /// construction. On the browser/WASM host the DXC module must already be loaded
    /// (<c>IShaderCompiler.InitializeAsync</c>); otherwise a clear <see cref="ShaderError"/>
    /// (<c>SD1903</c>) is returned. Never blocks on a task internally.
    /// </summary>
    /// <param name="request">The compile request: source, entry point, profile, and flags.</param>
    /// <param name="cancellationToken">Token checked before the compile starts.</param>
    /// <returns>
    /// The compiled <see cref="PlatformBlob"/> on success, or a <see cref="ShaderError"/>
    /// carrying the DXC diagnostics on failure.
    /// </returns>
    Result<PlatformBlob, ShaderError> Compile(
        DxcCompileRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Preprocess-only (<c>-P</c>): expand <c>#include</c>s, <c>#define</c>s and conditional
    /// directives into a single flat HLSL text WITHOUT compiling — no entry point, stage,
    /// or profile. Used by the zero-technique fallback in the pipeline to macro-expand the
    /// stock-effect <c>TECHNIQUE(...)</c> declarations into literal <c>technique</c> blocks
    /// the FX pre-parser can read. Synchronous (the DXC preprocessor is in-process work on
    /// every host).
    /// </summary>
    /// <param name="request">The preprocess request: source, file name, and macro defines.</param>
    /// <param name="cancellationToken">Token checked before preprocessing starts.</param>
    /// <returns>
    /// The expanded HLSL text on success, or a <see cref="ShaderError"/> carrying the DXC
    /// diagnostics on failure.
    /// </returns>
    Result<string, ShaderError> Preprocess(
        DxcPreprocessRequest request,
        CancellationToken cancellationToken = default);
}
