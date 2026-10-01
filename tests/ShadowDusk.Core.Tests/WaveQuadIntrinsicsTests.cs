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
}
