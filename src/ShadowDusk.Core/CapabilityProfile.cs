#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// A named, render-proven runtime contract: a validated point in ShadowDusk's capability space
/// (the effect <see cref="EffectContainer"/> + <see cref="MgfxVersion"/> "format" axis and the GL
/// <see cref="ShaderDialect"/>; later, allowed GL features). The closed set spans every
/// (runtime, format) cell ShadowDusk targets, so one profile names a full contract.
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
    private CapabilityProfile(string name, EffectContainer container, int mgfxVersion, ShaderDialect dialect)
    {
        Name = name;
        Container = container;
        MgfxVersion = mgfxVersion;
        Dialect = dialect;
    }

    /// <summary>The stable, human-readable identifier (also what <see cref="ToString"/> returns).</summary>
    public string Name { get; }

    /// <summary>
    /// The effect container this profile emits (<see cref="EffectContainer.Mgfx"/> or
    /// <see cref="EffectContainer.Knifx"/>). Together with <see cref="MgfxVersion"/> this is the
    /// "format" axis, so one profile fully specifies a (runtime, format) contract. Ignored for
    /// <see cref="PlatformTarget.Fna"/> (always the D3D9 fx_2_0 <c>.fxb</c>).
    /// </summary>
    public EffectContainer Container { get; }

    /// <summary>
    /// The MGFX container version (10 or 11). Ignored when <see cref="Container"/> is
    /// <see cref="EffectContainer.Knifx"/> (KNIFX carries its own version) and for
    /// <see cref="PlatformTarget.Fna"/>.
    /// </summary>
    public int MgfxVersion { get; }

    /// <summary>The GL dialect this profile emits. <see cref="ShaderDialect.NotApplicable"/> for non-GL contracts.</summary>
    public ShaderDialect Dialect { get; }

    /// <summary>
    /// MonoGame / KNI OpenGL (DesktopGL / WebGL), MGFX v10, MojoShader-dialect GLSL. The default GL
    /// contract; render-proven in real MonoGame (Phase 17/28/43) and KNI v4.02 desktop.
    /// </summary>
    public static readonly CapabilityProfile MonoGameGL_3_8_2 =
        new("MonoGameGL_3_8_2", EffectContainer.Mgfx, 10, ShaderDialect.LegacyMojoShader);

    /// <summary>
    /// MonoGame / KNI DirectX 11, MGFX v10, DXBC SM5. No GL dialect (DXBC bytecode); render-proven
    /// in real MonoGame WindowsDX (Phase 18).
    /// </summary>
    public static readonly CapabilityProfile MonoGameDX_SM5 =
        new("MonoGameDX_SM5", EffectContainer.Mgfx, 10, ShaderDialect.NotApplicable);

    /// <summary>
    /// MonoGame OpenGL, MGFX <b>v11</b>, MojoShader-dialect GLSL. The 3.8.5+ container (the v10
    /// body plus the per-shader SourceFile/Entrypoint strings). Render-proven in real MonoGame
    /// 3.8.5 (<c>validation/MonoGameV11</c>); opt-in (3.8.5 is pre-release, the default stays v10).
    /// </summary>
    public static readonly CapabilityProfile MonoGameGL_3_8_5 =
        new("MonoGameGL_3_8_5", EffectContainer.Mgfx, 11, ShaderDialect.LegacyMojoShader);

    /// <summary>
    /// KNI OpenGL, the <b>KNIFX v11</b> container, MojoShader-dialect GLSL. KNI v4.02+. The corpus
    /// is render-proven in real KNI v4.2.9001 (<c>validation/KniDesktopGL knifx</c>, maxd 0 vs v10);
    /// the KNIFX-specific fixes (optimized <c>Matrix4x4</c>, sampler-without-texture) are still
    /// pending validation against a KNIFXC golden. Opt-in / additive (the default stays v10).
    /// </summary>
    public static readonly CapabilityProfile KniGL_4_02 =
        new("KniGL_4_02", EffectContainer.Knifx, 11, ShaderDialect.LegacyMojoShader);

    /// <summary>
    /// FNA, the D3D9 fx_2_0 <c>.fxb</c> container (SM1-3). No GL dialect; render-proven in real FNA
    /// via MojoShader (Phase 39/40). Container/version fields are inert for the FNA target.
    /// </summary>
    public static readonly CapabilityProfile Fna_Fx2 =
        new("Fna_Fx2", EffectContainer.Mgfx, 10, ShaderDialect.NotApplicable);

    /// <inheritdoc/>
    public override string ToString() => Name;
}
