#nullable enable
using ShadowDusk.HLSL;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// Pure unit tests for the recognized-profile helpers <see cref="FxPreParser.IsKnownProfile"/>
/// and <see cref="FxPreParser.LooksLikeProfile"/> that back the Phase-48 compile-target
/// validation. <c>IsKnownProfile</c> must accept exactly the profiles fxc/mgfxc accept
/// (including the feature-level-9 variants the standard MonoGame DirectX header expands to,
/// work item W0); <c>LooksLikeProfile</c> must classify a profile-SHAPED token (real or
/// bogus) so the pipeline can reject a typo'd literal (<c>ps_9_9</c>) without macro
/// expansion while still expanding a macro NAME (<c>PS_SHADERMODEL</c>).
/// </summary>
public sealed class ProfileRecognitionTests
{
    [Theory]
    [InlineData("ps_3_0")]
    [InlineData("vs_3_0")]
    [InlineData("ps_2_0")]
    [InlineData("ps_4_0")]
    [InlineData("vs_5_0")]
    public void IsKnownProfile_AcceptsLiteralProfiles(string profile) =>
        FxPreParser.IsKnownProfile(profile).ShouldBeTrue();

    [Theory]
    // W0 (load-bearing): the standard MonoGame DirectX header expands to these.
    [InlineData("vs_4_0_level_9_1")]
    [InlineData("vs_4_0_level_9_3")]
    [InlineData("ps_4_0_level_9_1")]
    [InlineData("ps_4_0_level_9_3")]
    [InlineData("vs_4_0_level_9_0")]
    [InlineData("ps_4_0_level_9_0")]
    public void IsKnownProfile_AcceptsFeatureLevel9Profiles(string profile) =>
        FxPreParser.IsKnownProfile(profile).ShouldBeTrue("the standard MonoGame DirectX *_SHADERMODEL header expands to these; " +
                     "rejecting them would break every stock MonoGame DirectX shader");

    [Theory]
    // Completeness: profiles fxc/DXC accept that are easy to omit. The *_2_sw siblings were
    // already listed, so omitting the *_3_sw pair would over-reject a valid-to-fxc profile;
    // the SM6 list ran to 6_7, so 6_8/6_9 (which DXC, our frontend, accepts) round it out.
    [InlineData("vs_3_sw")]
    [InlineData("ps_3_sw")]
    [InlineData("vs_6_8")]
    [InlineData("vs_6_9")]
    [InlineData("ps_6_8")]
    [InlineData("ps_6_9")]
    public void IsKnownProfile_AcceptsLessCommonButValidProfiles(string profile) =>
        FxPreParser.IsKnownProfile(profile).ShouldBeTrue("fxc/DXC accept these, so rejecting them would diverge in the over-reject " +
                     "direction (accepting fewer than the reference compiler)");

    [Theory]
    [InlineData("ps_9_9")]   // profile-shaped but bogus
    [InlineData("ps_2_5")]   // profile-shaped but bogus
    [InlineData("a")]        // typo
    [InlineData("ps_shadermodel")] // macro name (lowercased)
    [InlineData("ps_4_0_level_9_2")] // there is no level_9_2
    public void IsKnownProfile_RejectsUnknownTokens(string token) =>
        FxPreParser.IsKnownProfile(token).ShouldBeFalse();

    [Theory]
    [InlineData("ps_3_0")]
    [InlineData("ps_9_9")]   // shaped, even though not a real profile
    [InlineData("vs_2_0")]
    [InlineData("ps_4_0_level_9_1")]
    public void LooksLikeProfile_TrueForProfileShapedTokens(string token) =>
        FxPreParser.LooksLikeProfile(token).ShouldBeTrue();

    [Theory]
    [InlineData("a")]                 // typo
    [InlineData("ps_shadermodel")]    // macro NAME — must NOT be treated as a shaped literal
    [InlineData("vs_shadermodel")]
    [InlineData("PS_SHADERMODEL")]
    [InlineData("foo")]
    public void LooksLikeProfile_FalseForMacroNamesAndTypos(string token) =>
        FxPreParser.LooksLikeProfile(token).ShouldBeFalse("a macro name may still expand to a real profile, so it must take the " +
                     "expansion path rather than being rejected as a bogus literal");

    // -------------------------------------------------------------------------
    // DirectX_11 profile floor (Phase 51 A10 — backs SD0015)
    // -------------------------------------------------------------------------
    //
    // These expectations are TRANSCRIBED FROM MEASUREMENT, not from the profile names.
    // Every KnownProfiles entry was swept through the pinned mgfxc (dotnet-mgcb 3.8.4.1)
    // for /Profile:DirectX_11 on 2026-07-31; the accept column below is exactly what came
    // back exit-0. If someone "simplifies" this to a major >= 4 comparison, the _level_9_0
    // and SM6 rows are the ones that will start silently diverging from mgfxc.

    [Theory]
    [InlineData("vs_4_0_level_9_1")]
    [InlineData("vs_4_0_level_9_3")]
    [InlineData("vs_4_0")]
    [InlineData("vs_4_1")]
    [InlineData("vs_5_0")]
    [InlineData("ps_4_0_level_9_1")]
    [InlineData("ps_4_0_level_9_3")]
    [InlineData("ps_4_0")]
    [InlineData("ps_4_1")]
    [InlineData("ps_5_0")]
    public void IsDirectX11Profile_AcceptsExactlyWhatMgfxcAccepts(string profile) =>
        FxPreParser.IsDirectX11Profile(profile).ShouldBeTrue(
            "mgfxc /Profile:DirectX_11 compiles this profile, so rejecting it would over-reject");

    [Theory]
    // Below the floor: everything SM1-3, including the software variants.
    [InlineData("vs_1_1")]
    [InlineData("vs_2_0")]
    [InlineData("vs_3_0")]
    [InlineData("vs_3_sw")]
    [InlineData("ps_2_0")]
    [InlineData("ps_2_b")]
    [InlineData("ps_3_0")]
    [InlineData("ps_3_sw")]
    // Unobvious #1: _level_9_0 is BELOW the floor — only _9_1 and _9_3 are accepted.
    [InlineData("vs_4_0_level_9_0")]
    [InlineData("ps_4_0_level_9_0")]
    // Unobvious #2: SM6 is refused too. MonoGame's DirectX_11 profile regex tops out at
    // major 5, so a numerically HIGHER profile still fails its "SM 4.0 level 9.1 or
    // higher!" check.
    [InlineData("vs_6_0")]
    [InlineData("ps_6_0")]
    [InlineData("ps_6_7")]
    public void IsDirectX11Profile_RejectsWhatMgfxcRejects(string profile) =>
        FxPreParser.IsDirectX11Profile(profile).ShouldBeFalse(
            "mgfxc /Profile:DirectX_11 fails this profile with \"must be SM 4.0 level 9.1 or higher!\", " +
            "so accepting it is the reject-fidelity gap SD0015 closes");

    [Fact]
    public void IsDirectX11Profile_AcceptedSetIsASubsetOfKnownProfiles()
    {
        // The floor check only ever runs on a token that already passed IsKnownProfile, so
        // an entry the recognized set does not contain would be dead and, worse, would hide
        // a typo in the floor set itself.
        string[] accepted =
        {
            "vs_4_0_level_9_1", "vs_4_0_level_9_3", "vs_4_0", "vs_4_1", "vs_5_0",
            "ps_4_0_level_9_1", "ps_4_0_level_9_3", "ps_4_0", "ps_4_1", "ps_5_0",
        };
        foreach (string profile in accepted)
            FxPreParser.IsKnownProfile(profile).ShouldBeTrue(profile);
    }
}
