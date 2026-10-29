#nullable enable

using ShadowDusk.HLSL;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// The compile targets KNI's own DirectX pipeline accepts, for a KNIFX container on
/// <see cref="ShadowDusk.Core.PlatformTarget.DirectX"/>. An MGFX DirectX compile follows mgfxc
/// instead (<see cref="FxPreParser.IsDirectX11Profile"/>, <c>SD0015</c>).
/// </summary>
/// <remarks>
/// KNI's <c>ShaderProfileDX11.CreateShader</c> (the same code in KNI 4.2.9001 and 4.3.9001)
/// rewrites the profile before it calls fxc: <c>s_2_0</c> becomes <c>s_4_0_level_9_1</c> and
/// <c>s_3_0</c> becomes <c>s_4_0_level_9_3</c>. The recorded shader model comes from the profile as
/// written (2.0 and 3.0, see <see cref="KnifxShaderModel"/>), which is also what the mapped profile
/// records. Measured 2026-10-28 with KNI's own MGCB (<c>/platform:Windows</c>, 4.2.9001 and
/// 4.3.9001): an effect at <c>vs_2_0</c>/<c>ps_2_0</c>, literal or through a macro, builds to an
/// <c>.xnb</c> byte-identical to the same effect at <c>vs_4_0_level_9_1</c>/<c>ps_4_0_level_9_1</c>,
/// and <c>_3_0</c> to <c>_4_0_level_9_3</c>.
///
/// <para>KNI then refuses whatever its <c>ShaderVersion</c> regex
/// (<c>^ps_(1|2|3|4|5)_(0|1|)(_level_(9_1|9_3))?$</c>) cannot read, and anything below SM 2.0
/// (<c>ValidateShaderModels</c>). Over the profiles ShadowDusk recognizes that leaves the mgfxc
/// DirectX_11 set plus <c>*_2_0</c> and <c>*_3_0</c>, so a profile is accepted exactly when its
/// mapped profile is one mgfxc's DirectX_11 profile accepts. Refused, as KNI's MGCB refuses them:
/// <c>*_2_a</c>, <c>ps_2_b</c>, <c>*_2_sw</c>, <c>*_3_sw</c>, SM1, <c>*_4_0_level_9_0</c>, SM6.</para>
///
/// <para>ShadowDusk compiles every DirectX 11 shader at SM 5 whatever the profile
/// (<c>D3DCompileRequest</c>'s default), so the mapped profile only decides acceptance: a
/// <c>ps_2_0</c> shader gets the DXBC its <c>ps_4_0_level_9_1</c> twin gets, as in KNI.</para>
/// </remarks>
internal static class KniDirectXProfile
{
    /// <summary>
    /// The profile KNI's DirectX pipeline compiles a (lowercased) compile target at:
    /// <c>*_2_0</c> is <c>*_4_0_level_9_1</c>, <c>*_3_0</c> is <c>*_4_0_level_9_3</c>, and every
    /// other profile is unchanged.
    /// </summary>
    internal static string CompileProfile(string profile) => profile switch
    {
        "vs_2_0" => "vs_4_0_level_9_1",
        "ps_2_0" => "ps_4_0_level_9_1",
        "vs_3_0" => "vs_4_0_level_9_3",
        "ps_3_0" => "ps_4_0_level_9_3",
        _ => profile,
    };

    /// <summary>
    /// True when KNI's own DirectX pipeline accepts <paramref name="profile"/> (a recognized,
    /// lowercased profile): its mapped profile is one mgfxc's DirectX_11 profile accepts.
    /// </summary>
    internal static bool Accepts(string profile) => FxPreParser.IsDirectX11Profile(CompileProfile(profile));
}
