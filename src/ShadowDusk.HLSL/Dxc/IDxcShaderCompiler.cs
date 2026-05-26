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
    Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        DxcCompileRequest request,
        CancellationToken cancellationToken = default);
}
