#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// The pure part of the OpenGL combined-sampler pin: only the sampler half of a combined sampler
/// gives up its register (to a texture-unit pin); every other declaration is untouched; two
/// combined samplers on one unit are refused. The real-slangc proof is
/// <c>SlangGlSamplerSlotTests.CombinedSamplerRegister_*</c>.
/// </summary>
public sealed class SlangcCombinedSamplerGlSlotsTests
{
    private static (string Hlsl, IReadOnlyDictionary<string, int> Slots) Pinned(
        string hlsl, string[] combined, string source = "")
    {
        var result = SlangcCombinedSamplerGlSlots.Pin(hlsl, combined, source, "Pin.slang");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        return result.Value;
    }

    [Fact]
    public void SamplerHalfRegister_BecomesThePin_AndLeavesTheHlsl()
    {
        const string hlsl =
            "#line 93 \"core\"\nTexture2D<float4 > A;\n#line 1188 \"hlsl.meta.slang\"\nSamplerState A_sampler_0 : register(s2);\n" +
            "#line 3 \"<stdin>\"\nTexture2D<float4 > T;\nSamplerState S : register(s0);\n";

        var (pinned, slots) = Pinned(hlsl, ["A"]);

        slots.ShouldBe(new Dictionary<string, int> { ["A"] = 2 });
        pinned.ShouldContain("SamplerState A_sampler_0;\n", Case.Sensitive);
        pinned.ShouldNotContain("register(s2)", Case.Sensitive);
        // The author's split pair keeps its register: it is a reservation, not a pin.
        pinned.ShouldContain("SamplerState S : register(s0);", Case.Sensitive);
    }

    [Theory]
    // slangc's own spelling of 'register(s2, space1)', and hand-spaced variants: OpenGL has no
    // register spaces, so each pins unit 2.
    [InlineData("SamplerState A_sampler_0 : register(s2, space1);\n")]
    [InlineData("SamplerState A_sampler_0 : register( s2 , space0 ) ;\n")]
    [InlineData("SamplerState  A_sampler_0:register(s2);\n")]
    public void RegisterSpellings_AllPinTheUnit(string declaration)
    {
        var (pinned, slots) = Pinned("Texture2D<float4 > A;\n" + declaration, ["A"]);

        slots.ShouldBe(new Dictionary<string, int> { ["A"] = 2 });
        pinned.ShouldNotContain("register", Case.Sensitive);
    }

    [Fact]
    public void ACombinedSamplerWithoutASamplerRegister_PinsNothing()
    {
        const string hlsl = "Texture2D<float4 > A : register(t2);\nSamplerState A_sampler_0;\n";

        var (pinned, slots) = Pinned(hlsl, ["A"]);

        slots.ShouldBeEmpty();
        pinned.ShouldBe(hlsl);
    }

    [Fact]
    public void ASamplerNamedLikeAHalf_OfNoCombinedSampler_IsNotTouched()
    {
        // 'B_sampler_0' with no combined sampler 'B' (an author's own name): not in the list.
        const string hlsl = "Texture2D<float4 > B;\nSamplerState B_sampler_0 : register(s1);\n";

        var (pinned, slots) = Pinned(hlsl, ["A"]);

        slots.ShouldBeEmpty();
        pinned.ShouldBe(hlsl);
    }

    [Fact]
    public void TwoCombinedSamplersOnOneUnit_AreRefused_NamingBoth()
    {
        const string source = "Sampler2D A : register(s0);\nSampler2D B : register(s0);\n";
        const string hlsl =
            "Texture2D<float4 > A;\nSamplerState A_sampler_0 : register(s0);\nTexture2D<float4 > B;\nSamplerState B_sampler_0 : register(s0);\n";

        Result<(string, IReadOnlyDictionary<string, int>), ShaderError> result =
            SlangcCombinedSamplerGlSlots.Pin(hlsl, ["B", "A"], source, "Pin.slang");

        result.IsFailure.ShouldBeTrue();
        (result.Error.File, result.Error.Line, result.Error.Column, result.Error.Code).ShouldBe(("Pin.slang", 2, 1, "SD0644"));
        result.Error.Message.ShouldContain("the combined samplers 'A' and 'B' all declare register(s0)", Case.Sensitive);
    }

    [Theory]
    // The raw text can only order what every macro setting compiles; anything that can add or
    // rename a declaration means no raw order at all.
    [InlineData("#define T2 Texture2D\nT2 A;\n")]
    [InlineData("#include \"x.slang\"\nTexture2D A;\n")]
    [InlineData("Texture2D A##B;\n")]
    [InlineData("Texture2D A;\\\n")]
    [InlineData("#if X\nTexture2D A;\n")]
    [InlineData("#if X\nstruct P {\n#else\nstruct P {\n#endif\nTexture2D A; };\nTexture2D B;\n")]
    public void UnconditionalRawText_IsNull_WhenTheRawTextCannotSpeakForSlangc(string source) =>
        SlangcCombinedSamplerGlSlots.UnconditionalRawText(source, []).ShouldBeNull();

    [Fact]
    public void UnconditionalRawText_BlanksEveryConditionalBranch()
    {
        string text = SlangcCombinedSamplerGlSlots.UnconditionalRawText(
            "#if 0\nfloat A;\n#else\nTexture2D C;\n#endif\nTexture2D B;\nTexture2D A;\n", []).ShouldNotBeNull();

        var order = SlangcCombinedSamplerGlSlots.DeclarationOrder(text, "Raw.slang", rawSource: true);
        order.Keys.ShouldBe(["B", "A"]);
    }

    [Fact]
    public void DeclarationOrder_IsEmpty_WhenANameIsDeclaredTwice()
    {
        SlangcCombinedSamplerGlSlots.DeclarationOrder(
            "Texture2D A;\nTexture2D B;\nTexture2D A;\n", "Twice.slang", rawSource: true).ShouldBeEmpty();
    }

    [Fact]
    public void DeclarationOrder_IsTheAuthorsOrder_NotSlangcsFirstUseOrder()
    {
        var order = SlangcCombinedSamplerGlSlots.DeclarationOrder(
            "Texture2D T;\nSamplerState S : register(s0);\nSampler2D A;\n", "Order.slang", rawSource: true);

        order["T"].ShouldBeLessThan(order["A"]);
    }
}
