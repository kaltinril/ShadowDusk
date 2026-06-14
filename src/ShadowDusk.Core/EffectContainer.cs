#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// Which effect container ShadowDusk serializes the compiled effect into.
/// <para>
/// <see cref="Mgfx"/> (the default) is the MGFX container, the universally loadable
/// format every MonoGame and KNI runtime accepts (version controlled by
/// <see cref="CompilerOptions.MgfxVersion"/>, default v10). This is the seamless product
/// default and is <b>never</b> something a consumer must change to get correct output.
/// </para>
/// <para>
/// <see cref="Knifx"/> emits KNI's newer <b>KNIFX v11</b> container (additive, opt-in) so
/// consumers on KNI v4.02+ can use the newer container's features. It is an escape hatch /
/// additive target, not a replacement for the v10 default. Ignored for
/// <see cref="PlatformTarget.Fna"/>, whose output is always the D3D9 fx_2_0 container.
/// </para>
/// </summary>
public enum EffectContainer
{
    /// <summary>The MGFX container (default; loads on every MonoGame/KNI runtime).</summary>
    Mgfx,

    /// <summary>KNI's KNIFX v11 container (additive; KNI v4.02+).</summary>
    Knifx,
}
