#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Pure unit tests for the FNA profile policy (<see cref="CompilationPipeline.ResolveFnaProfile"/>).
/// The policy is a SHAPE test, not a known-profiles lookup, because the pre-parser runs
/// before macro expansion (a macro name like <c>PS_SHADERMODEL</c> arrives literally).
/// These run in CI on every OS — no vkd3d native is involved; the policy fires before
/// any compile. The integration suite exercises the same codes end-to-end where vkd3d
/// is available.
/// </summary>
public sealed class FnaProfilePolicyTests
{
    private const string File = "test.fx";

    [Theory]
    [InlineData("ps_2_0", ShaderStage.Pixel)]
    [InlineData("ps_3_0", ShaderStage.Pixel)]
    [InlineData("vs_2_0", ShaderStage.Vertex)]
    [InlineData("vs_3_0", ShaderStage.Vertex)]
    public void LiteralSm23Profile_MatchingStage_IsHonoredAsWritten(string profile, ShaderStage stage)
    {
        var result = CompilationPipeline.ResolveFnaProfile(profile, stage, File);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(profile, because: "fxc fidelity: a literal SM 2–3 profile is honored verbatim");
    }

    [Theory]
    [InlineData(null, ShaderStage.Pixel, "ps_3_0")]
    [InlineData(null, ShaderStage.Vertex, "vs_3_0")]
    [InlineData("PS_SHADERMODEL", ShaderStage.Pixel, "ps_3_0")] // pre-parser lowercases; either way: macro shape
    [InlineData("ps_shadermodel", ShaderStage.Pixel, "ps_3_0")]
    [InlineData("vs_shadermodel", ShaderStage.Vertex, "vs_3_0")]
    [InlineData("sp_3_0", ShaderStage.Pixel, "ps_3_0")] // typo'd prefix classifies as macro shape (documented small print)
    public void MacroOrAbsentProfile_DefaultsToSm3Ceiling(string? profile, ShaderStage stage, string expected)
    {
        var result = CompilationPipeline.ResolveFnaProfile(profile, stage, File);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected,
            because: "macro-indirected profiles are unknowable pre-expansion and default to the SM3 ceiling");
    }

    [Theory]
    [InlineData("ps_4_0", ShaderStage.Pixel)]
    [InlineData("vs_5_0", ShaderStage.Vertex)]
    [InlineData("ps_4_0_level_9_1", ShaderStage.Pixel)] // the MonoGame Reach profile — shape test catches it
    [InlineData("ps_6_0", ShaderStage.Pixel)]
    public void LiteralSm4PlusProfile_FailsSd0300(string profile, ShaderStage stage)
    {
        var result = CompilationPipeline.ResolveFnaProfile(profile, stage, File);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0300");
        result.Error.Message.Should().Contain(profile,
            because: "the diagnostic must name the offending profile as written");
    }

    [Theory]
    [InlineData("ps_1_1", ShaderStage.Pixel)]
    [InlineData("ps_1_4", ShaderStage.Pixel)]
    [InlineData("vs_1_1", ShaderStage.Vertex)]
    public void LiteralSm1Profile_FailsSd0300(string profile, ShaderStage stage)
    {
        // vkd3d 1.17's SM1 backend has known instruction gaps and the SM1 output path
        // has never been validated against real FNA — refuse loudly rather than risk
        // silently-wrong output (Constraint 5).
        var result = CompilationPipeline.ResolveFnaProfile(profile, stage, File);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0300");
        result.Error.Message.Should().Contain("ps_2_0",
            because: "the diagnostic must point at the safest supported profile");
    }

    [Theory]
    [InlineData("ps_3_0", ShaderStage.Vertex)]
    [InlineData("ps_2_0", ShaderStage.Vertex)]
    [InlineData("vs_3_0", ShaderStage.Pixel)]
    [InlineData("vs_2_0", ShaderStage.Pixel)]
    public void CrossStageProfile_FailsSd0300(string profile, ShaderStage stage)
    {
        // `VertexShader = compile ps_3_0 …` would compile a pixel shader and bind it as
        // the pass's vertex shader — fxc rejects this at compile time; shipping it would
        // break only inside the consumer's FNA at load/draw.
        var result = CompilationPipeline.ResolveFnaProfile(profile, stage, File);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0300");
        result.Error.Message.Should().Contain(profile)
            .And.Contain("prefix", because: "the diagnostic must explain the stage/prefix mismatch");
    }

    [Fact]
    public void OddButShapedToken_PassesThroughForVkd3dToReject()
    {
        // ps_3_9 is profile-shaped but not a real profile — pass it through so vkd3d
        // rejects it with its own (more specific) diagnostic.
        var result = CompilationPipeline.ResolveFnaProfile("ps_3_9", ShaderStage.Pixel, File);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ps_3_9");
    }
}
