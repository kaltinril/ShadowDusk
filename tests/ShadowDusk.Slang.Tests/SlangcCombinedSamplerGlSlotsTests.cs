#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// The pure part of the OpenGL combined-sampler pin: only the sampler half of a combined sampler
/// gives up its register (to a texture-unit pin); every other declaration is untouched. The
/// real-slangc proof is <c>SlangGlSamplerSlotTests.CombinedSamplerRegister_OnOpenGL_*</c>.
/// </summary>
public sealed class SlangcCombinedSamplerGlSlotsTests
{
    [Fact]
    public void SamplerHalfRegister_BecomesThePin_AndLeavesTheHlsl()
    {
        const string hlsl =
            "#line 93 \"core\"\nTexture2D<float4 > A;\n#line 1188 \"hlsl.meta.slang\"\nSamplerState A_sampler_0 : register(s2);\n" +
            "#line 3 \"<stdin>\"\nTexture2D<float4 > T;\nSamplerState S : register(s0);\n";

        var (pinned, slots) = SlangcCombinedSamplerGlSlots.Pin(hlsl, ["A"]);

        slots.ShouldBe(new Dictionary<string, int> { ["A"] = 2 });
        pinned.ShouldContain("SamplerState A_sampler_0;\n", Case.Sensitive);
        pinned.ShouldNotContain("register(s2)", Case.Sensitive);
        // The author's split pair keeps its register: it is a reservation, not a pin.
        pinned.ShouldContain("SamplerState S : register(s0);", Case.Sensitive);
    }

    [Fact]
    public void ACombinedSamplerWithoutASamplerRegister_PinsNothing()
    {
        const string hlsl = "Texture2D<float4 > A : register(t2);\nSamplerState A_sampler_0;\n";

        var (pinned, slots) = SlangcCombinedSamplerGlSlots.Pin(hlsl, ["A"]);

        slots.ShouldBeEmpty();
        pinned.ShouldBe(hlsl);
    }

    [Fact]
    public void ASamplerNamedLikeAHalf_OfNoCombinedSampler_IsNotTouched()
    {
        // 'B_sampler_0' with no combined sampler 'B' (an author's own name): not in the list.
        const string hlsl = "Texture2D<float4 > B;\nSamplerState B_sampler_0 : register(s1);\n";

        var (pinned, slots) = SlangcCombinedSamplerGlSlots.Pin(hlsl, ["A"]);

        slots.ShouldBeEmpty();
        pinned.ShouldBe(hlsl);
    }
}
