#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// Provides the standard preprocessor macros (e.g. <c>MGFX</c>, <c>GLSL</c>/<c>HLSL</c>,
/// per-target flags) that shaders branch on for each <see cref="PlatformTarget"/>, mirroring
/// the macros <c>mgfxc</c> defines.
/// </summary>
public static class PlatformMacros
{
    /// <summary>
    /// Returns the macro set for the given platform target.
    /// </summary>
    /// <param name="platform">The target backend.</param>
    /// <returns>The macros to define for that target.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown for an unsupported target (e.g. Metal, which is not yet implemented).
    /// </exception>
    public static MacroSet For(PlatformTarget platform) => platform switch
    {
        PlatformTarget.DirectX => new MacroSet([new("MGFX"), new("HLSL"), new("SM4")]),
        PlatformTarget.OpenGL  => new MacroSet([new("MGFX"), new("GLSL"), new("OPENGL")]),
        PlatformTarget.Vulkan  => new MacroSet([new("MGFX"), new("HLSL"), new("VULKAN"), new("SM6")]),
        // FNA compiles D3D9-style HLSL at SM1–3 (fx_2_0). Deliberately does NOT define
        // MGFX (the output is not an .mgfx container) nor SM4/SM6/OPENGL/VULKAN, so
        // MonoGame-template sources (Macros.fxh) fall through to their DX9/SM2 branch.
        PlatformTarget.Fna     => new MacroSet([new("FNA"), new("HLSL"), new("SM3")]),
        // Matches real mgfxc's DirectX12ShaderProfile.AddMacros exactly: HLSL + SM6, no
        // VULKAN-equivalent macro (Phase 54 research) — a shader gated on #if SM6 takes
        // the same branch Vulkan does; one gated on #if VULKAN falls to its #else, which
        // is correct (DX12 doesn't share Vulkan's native-backend format quirks).
        PlatformTarget.DirectX12 => new MacroSet([new("MGFX"), new("HLSL"), new("SM6")]),
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };

    /// <summary>
    /// Returns the macro set for the given target AND output container. Identical to the
    /// single-target <c>For</c> overload, plus <c>__KNIFX__ = 1</c> when the container is
    /// <see cref="EffectContainer.Knifx"/>.
    /// </summary>
    /// <remarks>
    /// KNI's effect compiler (<c>MojoEffectProcessor</c>) <b>unconditionally</b> defines
    /// <c>__KNIFX__ = 1</c> for every KNI build, and KNI's own <c>Macros.fxh</c> plus real KNI
    /// shaders (e.g. <c>Apos.Shapes</c>, used by Gum) branch on it — <c>#ifdef __KNIFX__</c>
    /// switches the <c>_vs/_ps/_cb</c> register macros, and <c>#if __KNIFX__</c> selects the
    /// SM4 profile. Targeting the KNIFX container IS, by definition, a KNI build, so ShadowDusk
    /// defines <c>__KNIFX__</c> there to stay faithful to KNI's compiler. The default MGFX
    /// container is target-agnostic (it loads on MonoGame AND KNI), so it deliberately does NOT
    /// define <c>__KNIFX__</c> — that would make the universal output KNI-specific.
    /// </remarks>
    public static MacroSet For(PlatformTarget platform, EffectContainer container)
    {
        MacroSet baseMacros = For(platform);
        return container == EffectContainer.Knifx
            ? new MacroSet([.. baseMacros.Macros, new MacroDefinition("__KNIFX__")])
            : baseMacros;
    }

    /// <summary>
    /// Whether <c>For</c> can produce a macro set for the given target — i.e. the
    /// target is one ShadowDusk compiles. Callers pre-check this instead of catching the
    /// <see cref="ArgumentOutOfRangeException"/> (no exception-as-control-flow).
    /// </summary>
    public static bool IsSupported(PlatformTarget platform) => platform
        is PlatformTarget.DirectX
        or PlatformTarget.OpenGL
        or PlatformTarget.Vulkan
        or PlatformTarget.Fna
        or PlatformTarget.DirectX12;
}
