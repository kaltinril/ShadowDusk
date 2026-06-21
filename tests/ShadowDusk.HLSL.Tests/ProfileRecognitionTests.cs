#nullable enable
using ShadowDusk.HLSL;
using FluentAssertions;
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
        FxPreParser.IsKnownProfile(profile).Should().BeTrue();

    [Theory]
    // W0 (load-bearing): the standard MonoGame DirectX header expands to these.
    [InlineData("vs_4_0_level_9_1")]
    [InlineData("vs_4_0_level_9_3")]
    [InlineData("ps_4_0_level_9_1")]
    [InlineData("ps_4_0_level_9_3")]
    [InlineData("vs_4_0_level_9_0")]
    [InlineData("ps_4_0_level_9_0")]
    public void IsKnownProfile_AcceptsFeatureLevel9Profiles(string profile) =>
        FxPreParser.IsKnownProfile(profile).Should().BeTrue(
            because: "the standard MonoGame DirectX *_SHADERMODEL header expands to these; " +
                     "rejecting them would break every stock MonoGame DirectX shader");

    [Theory]
    [InlineData("ps_9_9")]   // profile-shaped but bogus
    [InlineData("ps_2_5")]   // profile-shaped but bogus
    [InlineData("a")]        // typo
    [InlineData("ps_shadermodel")] // macro name (lowercased)
    [InlineData("ps_4_0_level_9_2")] // there is no level_9_2
    public void IsKnownProfile_RejectsUnknownTokens(string token) =>
        FxPreParser.IsKnownProfile(token).Should().BeFalse();

    [Theory]
    [InlineData("ps_3_0")]
    [InlineData("ps_9_9")]   // shaped, even though not a real profile
    [InlineData("vs_2_0")]
    [InlineData("ps_4_0_level_9_1")]
    public void LooksLikeProfile_TrueForProfileShapedTokens(string token) =>
        FxPreParser.LooksLikeProfile(token).Should().BeTrue();

    [Theory]
    [InlineData("a")]                 // typo
    [InlineData("ps_shadermodel")]    // macro NAME — must NOT be treated as a shaped literal
    [InlineData("vs_shadermodel")]
    [InlineData("PS_SHADERMODEL")]
    [InlineData("foo")]
    public void LooksLikeProfile_FalseForMacroNamesAndTypos(string token) =>
        FxPreParser.LooksLikeProfile(token).Should().BeFalse(
            because: "a macro name may still expand to a real profile, so it must take the " +
                     "expansion path rather than being rejected as a bogus literal");
}
