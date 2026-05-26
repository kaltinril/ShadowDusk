#nullable enable

namespace ShadowDusk.HLSL.Dxc;

public sealed class DxcCompileOptions
{
    public bool AllowWarnings { get; init; } = false;
    public bool EmbedDebugInfo { get; init; } = false;
}
