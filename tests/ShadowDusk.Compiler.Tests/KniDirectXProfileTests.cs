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

    public static TheoryData<string> RecognizedProfiles()
    {
        var data = new TheoryData<string>();
        foreach (string prefix in new[] { "vs", "ps" })
        {
            foreach (string suffix in new[]
                     {
                         "2_0", "2_a", "2_sw", "3_0", "3_sw", "4_0", "4_1",
                         "4_0_level_9_0", "4_0_level_9_1", "4_0_level_9_3", "5_0",
                         "6_0", "6_1", "6_2", "6_3", "6_4", "6_5", "6_6", "6_7", "6_8", "6_9",
                     })
                data.Add($"{prefix}_{suffix}");
        }
        data.Add("vs_1_1");
        foreach (string ps in new[] { "ps_1_1", "ps_1_2", "ps_1_3", "ps_1_4", "ps_2_b" })
            data.Add(ps);
        return data;
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
