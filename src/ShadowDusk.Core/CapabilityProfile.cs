#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// A named, render-proven runtime contract: a validated point in ShadowDusk's capability space
/// (today, the GL <see cref="ShaderDialect"/>; later, allowed GL features and container framing).
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>closed set</b>, not an open cross-product. The constructor is private and the only
/// instances are the <c>static readonly</c> members below, each rung-4 proven against a specific
/// runtime. That is deliberate: it keeps "give the consumer the newest best experience" from
/// degrading into "emit an untested combination of bytes." A consumer cannot invent an anonymous
/// profile, so the compiler can never be asked for a tuple no runtime honors.
/// </para>
/// <para>
/// The axis is <b>additive and optional</b>: <see cref="CompilerOptions.Profile"/> defaults to
/// <see langword="null"/>, which reproduces today's behavior exactly (the dialect is derived from
/// <see cref="CompilerOptions.Target"/>). An explicit profile only <i>refines</i> that behavior; it
/// never becomes a flag a consumer must set to get correct output. The effect container stays the
/// separate <see cref="EffectContainer"/> / <see cref="CompilerOptions.MgfxVersion"/> axis.
/// </para>
/// </remarks>
public sealed class CapabilityProfile
{
    private CapabilityProfile(string name, ShaderDialect dialect)
    {
        Name = name;
        Dialect = dialect;
    }

    /// <summary>The stable, human-readable identifier (also what <see cref="ToString"/> returns).</summary>
    public string Name { get; }

    /// <summary>The GL dialect this profile emits. <see cref="ShaderDialect.NotApplicable"/> for non-GL contracts.</summary>
    public ShaderDialect Dialect { get; }

    /// <summary>
    /// MonoGame / KNI OpenGL (DesktopGL / WebGL), MGFX v10, MojoShader-dialect GLSL. The default GL
    /// contract; render-proven in real MonoGame (Phase 17/28/43) and KNI v4.02 desktop.
    /// </summary>
    public static readonly CapabilityProfile MonoGameGL_3_8_2 =
        new("MonoGameGL_3_8_2", ShaderDialect.LegacyMojoShader);

    /// <summary>
    /// MonoGame / KNI DirectX 11, MGFX v10, DXBC SM5. No GL dialect (DXBC bytecode); render-proven
    /// in real MonoGame WindowsDX (Phase 18).
    /// </summary>
    public static readonly CapabilityProfile MonoGameDX_SM5 =
        new("MonoGameDX_SM5", ShaderDialect.NotApplicable);

    /// <summary>
    /// FNA, the D3D9 fx_2_0 <c>.fxb</c> container (SM1-3). No GL dialect; render-proven in real FNA
    /// via MojoShader (Phase 39/40).
    /// </summary>
    public static readonly CapabilityProfile Fna_Fx2 =
        new("Fna_Fx2", ShaderDialect.NotApplicable);

    /// <inheritdoc/>
    public override string ToString() => Name;
}
