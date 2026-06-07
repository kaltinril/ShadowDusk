#nullable enable

using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.HLSL.D3DCompiler;

/// <summary>
/// Compiles preprocessed HLSL to SM5 DXBC (Shader Model ≤ 5) — the bytecode
/// MonoGame's DX11 runtime loads. This is the seam behind which a DXBC backend
/// sits: the current implementation is the d3dcompiler_47 "oracle"
/// (<see cref="D3DCompilerShaderCompiler"/>, Windows-only); a cross-platform
/// vkd3d-shader backend is intended to slot in here later (Phase 18 Track A)
/// without changing the pipeline.
/// </summary>
public interface IDxbcShaderCompiler
{
    /// <summary>
    /// Compiles a preprocessed HLSL request to an SM ≤ 5 DXBC blob.
    /// </summary>
    /// <param name="request">The compile request: source, entry point, stage, and flags.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The compiled DXBC <see cref="PlatformBlob"/> on success, or a <see cref="ShaderError"/>
    /// on failure.
    /// </returns>
    Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default);
}
