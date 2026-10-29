#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.HLSL;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// The compile targets KNI's own DirectX pipeline accepts and the profile it compiles each at
/// (<c>ShaderProfileDX11.CreateShader</c>, KNI 4.2.9001 and 4.3.9001): <c>*_2_0</c> is compiled as
/// <c>*_4_0_level_9_1</c> and <c>*_3_0</c> as <c>*_4_0_level_9_3</c>; every other profile is
/// compiled as written, and refused unless KNI's <c>ShaderVersion</c> regex reads it at SM 2.0 or
/// higher.
/// </summary>
public sealed class KniDirectXProfileTests
{
    [Theory]
    [InlineData("vs_2_0", "vs_4_0_level_9_1")]
    [InlineData("ps_2_0", "ps_4_0_level_9_1")]
    [InlineData("vs_3_0", "vs_4_0_level_9_3")]
    [InlineData("ps_3_0", "ps_4_0_level_9_3")]
    [InlineData("ps_4_0_level_9_1", "ps_4_0_level_9_1")]
    [InlineData("ps_4_0_level_9_3", "ps_4_0_level_9_3")]
    [InlineData("vs_4_0", "vs_4_0")]
    [InlineData("ps_4_1", "ps_4_1")]
    [InlineData("ps_5_0", "ps_5_0")]
    [InlineData("ps_2_a", "ps_2_a")]
    [InlineData("ps_2_b", "ps_2_b")]
    [InlineData("vs_2_sw", "vs_2_sw")]
    [InlineData("ps_3_sw", "ps_3_sw")]
    [InlineData("ps_1_1", "ps_1_1")]
    [InlineData("ps_4_0_level_9_0", "ps_4_0_level_9_0")]
    [InlineData("ps_6_0", "ps_6_0")]
    public void CompileProfile_IsTheProfileKniHandsFxc(string profile, string compiled)
    {
        KniDirectXProfile.CompileProfile(profile).ShouldBe(compiled);
    }

    [Theory]
    [InlineData("vs_2_0")]
    [InlineData("ps_2_0")]
    [InlineData("vs_3_0")]
    [InlineData("ps_3_0")]
    public void Accepts_Sm2Sm3_WhichMgfxcRefuses(string profile)
    {
        KniDirectXProfile.Accepts(profile).ShouldBeTrue();
        FxPreParser.IsDirectX11Profile(profile).ShouldBeFalse("the MGFX DirectX floor (mgfxc) still refuses it");
    }

    /// <summary>
    /// Over every profile ShadowDusk recognizes, the accepted set is exactly what KNI's own
    /// validation accepts, ported here from the decompile as the oracle: the <c>ShaderVersion</c>
    /// regex (major 1-5, minor 0/1, optional <c>_level_9_1</c>/<c>_level_9_3</c> on 4.0 only) and
    /// <c>ValidateShaderModels</c>'s SM 2.0 floor.
    /// </summary>
    [Theory]
    [MemberData(nameof(RecognizedProfiles))]
    public void Accepts_MatchesKnisOwnValidation(string profile)
    {
        FxPreParser.IsKnownProfile(profile).ShouldBeTrue();
        KniDirectXProfile.Accepts(profile).ShouldBe(KniValidates(profile));
    }

    /// <summary>The whole recognized set, so a profile added to it later is swept too.</summary>
    public static TheoryData<string> RecognizedProfiles()
    {
        var data = new TheoryData<string>();
        foreach (string profile in Swept())
            data.Add(profile);
        return data;
    }

    private static string[] Swept() =>
        FxPreParser.KnownProfileNames.OrderBy(p => p, StringComparer.Ordinal).ToArray();

    [Fact]
    public void RecognizedProfiles_CoverTheSm2Sm3ProfilesKniMaps()
    {
        // Guards the sweep: the profiles this change accepts must be in it.
        string[] swept = Swept();
        swept.ShouldContain("ps_2_0");
        swept.ShouldContain("vs_3_0");
        swept.Length.ShouldBeGreaterThan(40);
    }

    private static readonly Regex KniVertex = new(
        "^vs_(?<major>1|2|3|4|5)_(?<minor>0|1|)(_level_(?<level>9_1|9_3))?$", RegexOptions.CultureInvariant);

    private static readonly Regex KniPixel = new(
        "^ps_(?<major>1|2|3|4|5)_(?<minor>0|1|)(_level_(?<level>9_1|9_3))?$", RegexOptions.CultureInvariant);

    private static bool KniValidates(string profile)
    {
        Match m = (profile.StartsWith("vs_", StringComparison.Ordinal) ? KniVertex : KniPixel).Match(profile);
        if (!m.Success)
            return false;
        int major = int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture);
        int minor = int.Parse(m.Groups["minor"].Value, CultureInfo.InvariantCulture);
        if (m.Groups["level"].Success)
            major = major == 4 && minor == 0 ? (m.Groups["level"].Value == "9_1" ? 2 : 3) : -1;
        return major >= 2;
    }
}
