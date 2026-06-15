#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// How GLSL is emitted for an OpenGL-family target. This is the "dialect" capability axis of a
/// <see cref="CapabilityProfile"/>: it selects whether ShadowDusk down-converts SPIR-V/GLSL into
/// the MonoGame/KNI MojoShader dialect that every shipping GL runtime consumes today, or emits the
/// un-down-converted modern GLSL a future runtime might consume. Non-GL targets (DirectX DXBC, FNA
/// fx_2_0) carry <see cref="NotApplicable"/>.
/// </summary>
public enum ShaderDialect
{
    /// <summary>
    /// Not a GLSL target (DirectX DXBC, FNA fx_2_0, or a reserved SPIR-V backend). No GL dialect
    /// applies, so the MonoGame/KNI GL rewrite never runs.
    /// </summary>
    NotApplicable = 0,

    /// <summary>
    /// MonoGame/KNI MojoShader-dialect GLSL (the managed down-convert rewrite,
    /// <c>MonoGameGlslRewriter</c>). The default and only render-proven GL output: it links in
    /// every shipping MonoGame/KNI GL runtime (SM2-3 via MojoShader).
    /// </summary>
    LegacyMojoShader = 1,

    /// <summary>
    /// Un-down-converted modern GLSL (the SPIRV-Cross output without the MojoShader rewrite).
    /// <b>Reserved:</b> no shipping MonoGame/KNI GL runtime consumes it today (KNI GL is still
    /// MojoShader-capped at SM2-3), so no <see cref="CapabilityProfile"/> selects it yet and it is
    /// never auto-selected. It exists so the dialect seam is ready the day a runtime proves it.
    /// </summary>
    ModernGlsl = 2,
}
