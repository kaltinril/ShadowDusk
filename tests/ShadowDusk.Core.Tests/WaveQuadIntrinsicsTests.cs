#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Pure tests for <see cref="WaveQuadIntrinsics"/>, the vocabulary and the <c>SD0218</c>
/// message both the <c>.fx</c> and the real-slangc <c>.slang</c> routes reject Vulkan wave/quad
/// intrinsics with (issue #229).
/// </summary>
public sealed class WaveQuadIntrinsicsTests
{
    // The regex is a hand-duplicated literal of Names (a source-generated regex needs one);
    // this keeps the two from drifting.
    [Fact]
    public void EveryListedIntrinsic_IsDetectedByTheRegex()
    {
        foreach (string name in WaveQuadIntrinsics.Names)
        {
            var hit = WaveQuadIntrinsics.FindFirst($"void F() {{ {name}(0); }}");
            hit.ShouldNotBeNull($"'{name}' is listed in Names but the regex did not match it");
            hit!.Value.Name.ShouldBe(name);
        }
    }

    [Fact]
    public void FindFirst_ReportsThe1BasedLine()
    {
        var hit = WaveQuadIntrinsics.FindFirst("float4 P()\n{\n    float v = QuadReadAcrossX(1.0);\n}\n");

        hit.ShouldNotBeNull();
        hit!.Value.Name.ShouldBe("QuadReadAcrossX");
        hit.Value.Line.ShouldBe(3);
    }

    [Fact]
    public void FindFirst_AnIdentifierThatOnlyContainsWave_IsNotFlagged()
        => WaveQuadIntrinsics.FindFirst("cbuffer P { float2 Wave; float WaveActiveSumX; }").ShouldBeNull();

    [Fact]
    public void VulkanMessage_NamesTheIntrinsicAndTheRuntimeReason()
    {
        string message = WaveQuadIntrinsics.VulkanUnsupportedMessage("WaveActiveSum");

        message.ShouldContain("'WaveActiveSum'", Case.Sensitive);
        message.ShouldContain("Vulkan 1.0", Case.Sensitive);
        message.ShouldContain("no subgroup support", Case.Sensitive);
        message.ShouldContain("not supported on the Vulkan target", caseSensitivity: Case.Sensitive);
    }

    [Fact]
    public void VulkanMessage_WithoutAnIdentifiedIntrinsic_StillReadsAsASentence()
        => WaveQuadIntrinsics.VulkanUnsupportedMessage(null)
            .ShouldStartWith("A Shader Model 6 wave/quad intrinsic is not supported on the Vulkan target", caseSensitivity: Case.Sensitive);

    [Fact]
    public void Code_IsSD0218()
        => WaveQuadIntrinsics.VulkanUnsupportedCode.ShouldBe("SD0218");

    [Fact]
    public void BelowSm6Code_IsSD0624()
        => WaveQuadIntrinsics.BelowSm6Code.ShouldBe("SD0624");

    [Theory]
    [InlineData(PlatformTarget.OpenGL, true)]
    [InlineData(PlatformTarget.DirectX, true)]
    [InlineData(PlatformTarget.Fna, true)]
    [InlineData(PlatformTarget.DirectX12, false)]
    [InlineData(PlatformTarget.Vulkan, false)]
    public void IsBelowSm6Target_MatchesTheArchitecturalCeiling(PlatformTarget target, bool expected)
        => WaveQuadIntrinsics.IsBelowSm6Target(target).ShouldBe(expected);

    [Theory]
    [InlineData(PlatformTarget.DirectX, "Function \"QuadReadAcrossX\" is not defined.", "QuadReadAcrossX")]
    [InlineData(PlatformTarget.Fna, "Function \"WaveActiveSum\" is not defined.", "WaveActiveSum")]
    [InlineData(PlatformTarget.DirectX, "error X3004: undeclared identifier 'WavePrefixSum'", "WavePrefixSum")]
    [InlineData(PlatformTarget.OpenGL, "Vulkan 1.1 is required for Wave Operation\n  float v = WaveReadLaneAt(a, 0);", "WaveReadLaneAt")]
    public void Relabel_BelowSm6Target_BecomesSD0624_KeepingTheCompilersLocation(
        PlatformTarget target, string compilerMessage, string intrinsic)
    {
        var original = new ShaderError("a.fx", 7, 13, "E5005", compilerMessage);

        ShaderError relabelled = WaveQuadIntrinsics.Relabel(original, target, "TheCompiler").ShouldNotBeNull();

        relabelled.Code.ShouldBe("SD0624");
        relabelled.File.ShouldBe("a.fx");
        relabelled.Line.ShouldBe(7);
        relabelled.Column.ShouldBe(13);
        relabelled.Message.ShouldContain($"'{intrinsic}'", Case.Sensitive);
        relabelled.Message.ShouldContain($"on the {target} target", Case.Sensitive);
        relabelled.Message.ShouldEndWith("TheCompiler: " + compilerMessage, Case.Sensitive);
    }

    [Theory]
    [InlineData("Function \"MyOwnHelper\" is not defined.")]
    [InlineData("undeclared identifier 'undefinedSymbol'")]
    [InlineData("syntax error near 'WaveActiveSum'")]
    public void Relabel_UnrelatedFailure_IsLeftAlone(string compilerMessage)
        => WaveQuadIntrinsics.Relabel(
            new ShaderError("a.fx", 1, 1, "E5005", compilerMessage), PlatformTarget.DirectX, "c").ShouldBeNull();

    [Theory]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Metal)]
    public void Relabel_TargetThatIsNotCapped_IsLeftAlone(PlatformTarget target)
        => WaveQuadIntrinsics.Relabel(
            new ShaderError("a.fx", 1, 1, "E5005", "Function \"WaveActiveSum\" is not defined."), target, "c").ShouldBeNull();

    [Fact]
    public void Relabel_Vulkan_StaysSD0218()
    {
        var original = new ShaderError("a.fx", 4, 15, "X0000", "Vulkan 1.1 is required for Wave Operation\n  WaveActiveSum(a)");

        WaveQuadIntrinsics.Relabel(original, PlatformTarget.Vulkan, "DXC")!.Code.ShouldBe("SD0218");
    }
}
