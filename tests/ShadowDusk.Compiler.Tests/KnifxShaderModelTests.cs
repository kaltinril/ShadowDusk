#nullable enable

using ShadowDusk.Compiler.Internal;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// The KNIFX per-shader <c>ShaderVersion</c> is read from the resolved profile the way KNI's own
/// effect processor reads it (<c>ShaderVersion.ParseShaderModel</c>, KNI 4.2.9001 and 4.3.9001):
/// the feature-level-9 forms are 2.0 and 3.0, not 4.0. The values in the first theory were measured by
/// running KNI's own MGCB on the profile (Windows on 4.2.9001 and 4.3.9001, DesktopGL on 4.3.9001)
/// and reading the version it wrote.
/// </summary>
public sealed class KnifxShaderModelTests
{
    [Theory]
    [InlineData("vs_4_0_level_9_1", 2, 0)]
    [InlineData("ps_4_0_level_9_1", 2, 0)]
    [InlineData("PS_4_0_LEVEL_9_1", 2, 0)]
    [InlineData("vs_4_0_level_9_3", 3, 0)]
    [InlineData("ps_4_0_level_9_3", 3, 0)]
    [InlineData("vs_4_0", 4, 0)]
    [InlineData("ps_4_0", 4, 0)]
    [InlineData("ps_4_1", 4, 1)]
    [InlineData("vs_5_0", 5, 0)]
    [InlineData("ps_5_0", 5, 0)]
    [InlineData("ps_3_0", 3, 0)]
    [InlineData("vs_2_0", 2, 0)]
    public void Parse_MatchesWhatKnisOwnPipelineRecords(string profile, int major, int minor)
    {
        KnifxShaderModel.Parse(profile).ShouldBe((major, minor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ps_shadermodel")]
    public void Parse_AbsentOrUnparseable_KeepsTheSm3Default(string? profile)
    {
        KnifxShaderModel.Parse(profile).ShouldBe((3, 0));
    }

    /// <summary>
    /// Profiles KNI's own processor refuses as invalid keep the reading ShadowDusk always gave
    /// them: their output already loads somewhere (a 4.0 desktop GL body on FL10_0 and up; the
    /// GLES/WebGL body records 0.0), so they are pinned here as known divergences, not fixed.
    /// </summary>
    [Theory]
    [InlineData("ps_4_0_level_9_0", 4, 0)]
    [InlineData("vs_4_0_level_9_0", 4, 0)]
    [InlineData("ps_6_0", 6, 0)]
    [InlineData("ps_2_a", 3, 0)]
    [InlineData("ps_3_sw", 3, 0)]
    [InlineData("ps_1_1", 1, 1)]
    public void Parse_ProfilesKniRefuses_KeepTheirExistingReading(string profile, int major, int minor)
    {
        KnifxShaderModel.Parse(profile).ShouldBe((major, minor));
    }
}
