#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.HLSL.D3DCompiler;

/// <summary>
/// A request to compile preprocessed HLSL to D3D bytecode behind
/// <see cref="IDxbcShaderCompiler"/>. Mirrors <see cref="ShadowDusk.HLSL.Dxc.DxcCompileRequest"/>.
/// By default the profile is derived from <see cref="Stage"/> as SM5 (vs_5_0/ps_5_0 — the
/// MonoGame DX11 path); <see cref="ProfileOverride"/> selects a different profile (the FNA
/// fx_2_0 path compiles at SM1–3, e.g. "ps_3_0").
/// </summary>
public sealed class D3DCompileRequest
{
    public required string      HlslSource     { get; init; }
    public required string      SourceFileName { get; init; }
    public required string      EntryPoint     { get; init; }
    public required ShaderStage Stage          { get; init; }
    public bool                 EmbedDebugInfo { get; init; }
    public bool                 AllowWarnings  { get; init; }

    /// <summary>
    /// Explicit shader profile (e.g. <c>"ps_2_0"</c>, <c>"vs_3_0"</c>). When
    /// <see langword="null"/> (the default), the backend derives SM5 from
    /// <see cref="Stage"/> — keeping the existing DirectX path byte-identical. An SM ≤ 3
    /// profile makes the vkd3d backend emit a bare D3D9 token stream
    /// (<see cref="ShadowDusk.HLSL.Dxc.BlobKind.D3dBytecode"/>) instead of DXBC_TPF.
    /// </summary>
    public string? ProfileOverride { get; init; }
}
