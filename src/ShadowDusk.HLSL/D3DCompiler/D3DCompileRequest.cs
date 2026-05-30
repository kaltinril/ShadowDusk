#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.HLSL.D3DCompiler;

/// <summary>
/// A request to compile preprocessed HLSL to SM5 DXBC via d3dcompiler_47
/// (the fxc engine). Mirrors <see cref="ShadowDusk.HLSL.Dxc.DxcCompileRequest"/>
/// but is fixed to the DirectX11/DXBC target — the Windows-only "oracle" backend.
/// </summary>
public sealed class D3DCompileRequest
{
    public required string      HlslSource     { get; init; }
    public required string      SourceFileName { get; init; }
    public required string      EntryPoint     { get; init; }
    public required ShaderStage Stage          { get; init; }
    public bool                 EmbedDebugInfo { get; init; }
    public bool                 AllowWarnings  { get; init; }
}
