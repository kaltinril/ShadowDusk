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

    /// <summary>
    /// When <see langword="true"/>, passes DXC's <c>-Vd</c> flag to skip DXIL container
    /// validation. Only ever set for the OpenGL path's reflection-only companion DXIL
    /// compile (never for shipped bytecode): that compile's bytes are discarded (only its
    /// structure is walked for reflection), so skipping the validator there cannot ship an
    /// invalid module. It exists because DXC's validator is version-sensitive between
    /// <c>dxcompiler.dll</c> and <c>dxil.dll</c> — a hosted CI runner's own preinstalled
    /// Windows SDK can put a mismatched <c>dxil.dll</c> ahead of the one this library is
    /// pinned to on the native search path, which fails validation with a "DXIL container
    /// mismatch" error even though the module itself compiled correctly.
    /// </summary>
    public bool SkipValidation { get; init; } = false;
}
