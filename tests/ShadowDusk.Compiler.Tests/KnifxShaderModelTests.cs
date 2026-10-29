#nullable enable

using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Ast;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// The KNIFX per-shader <c>ShaderVersion</c> is read from the resolved profile the way KNI's own
/// effect processor reads it (<c>ShaderVersion.ParseShaderModel</c>, KNI 4.2.9001 and 4.3.9001):
/// the feature-level-9 forms are 2.0 and 3.0, not 4.0. Every expected value here was measured by
/// running KNI's own MGCB on the profile and reading the version it wrote.
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

    [Theory]
    [InlineData("vs_4_0_level_9_0", ShaderStage.Vertex, "V")]
    [InlineData("ps_4_0_level_9_0", ShaderStage.Pixel, "P")]
    public void ProfileCheck_Level90_IsRefusedAsKniDoes(string profile, ShaderStage stage, string entry)
    {
        ShaderError? error = KnifxShaderModel.ProfileCheck(
            profile, stage, new SourceSpan(7, 12, 7, 28), "a.fx", entry);

        error.ShouldNotBeNull();
        error.Code.ShouldBe("SD0018");
        error.File.ShouldBe("a.fx");
        error.Line.ShouldBe(7);
        error.Column.ShouldBe(12);
        string stageWord = stage == ShaderStage.Vertex ? "Vertex" : "Pixel";
        error.Message.ShouldContain($"Invalid profile '{profile}'. {stageWord} shader '{entry}'.", Case.Sensitive);
    }

    [Theory]
    [InlineData("ps_4_0_level_9_1")]
    [InlineData("ps_4_0_level_9_3")]
    [InlineData("ps_4_0")]
    [InlineData("ps_3_0")]
    [InlineData(null)]
    public void ProfileCheck_ProfilesKniReads_PassUntouched(string? profile)
    {
        KnifxShaderModel.ProfileCheck(profile, ShaderStage.Pixel, null, "a.fx", "P").ShouldBeNull();
    }
}
