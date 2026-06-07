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
}
