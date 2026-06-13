#nullable enable

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Fine-grained tuning for a <see cref="DxcCompileRequest"/>: whether to tolerate
/// compiler warnings rather than fail, and whether to embed debug information in the
/// emitted module.
/// </summary>
public sealed class DxcCompileOptions
{
    /// <summary>When <see langword="true"/>, warnings do not fail the compile.</summary>
    public bool AllowWarnings { get; init; } = false;

    /// <summary>When <see langword="true"/>, debug information is embedded in the output module.</summary>
    public bool EmbedDebugInfo { get; init; } = false;
}
