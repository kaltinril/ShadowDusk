#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// Issue #447: a legacy sampler's texture the shader also uses DIRECTLY gets a second <c>mgfxc</c>
/// DirectX 11 record (<c>A@t0 Tb@t1 Tb@t2</c>, measured, goldens <c>M10</c>, <c>M18</c>, <c>M20</c>
/// in <c>tests/fixtures/golden/dx11-texture-slots/</c>), which the SM4 rewrite's single
/// <c>Texture2D</c> cannot give, so the DirectX 11 slot check refuses with <c>SD0230</c>. A method
/// call was the only use looked for; any value use is now, outside function bodies too, and so is
/// the texture of a <c>sampler_state</c> sampler the rewrite never saw (an <c>#include</c>, a macro).
/// Pure tests on the preprocessed reading.
/// </summary>
public sealed class LegacyTextureDirectUseTests
{
    private const string Ps = """

        float4 P(float2 uv : TEXCOORD0) : COLOR0 { return G + tex2D(A, uv) + tex2D(SB, uv); }
        technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
        """;

    private static GlSamplerSlots Slots(string raw, string? flattened = null)
    {
        var parsed = FxPreParser.Parse(raw, "t.fx");
        parsed.IsSuccess.ShouldBeTrue(parsed.IsFailure ? parsed.Error.Message : "ok");
        var result = FxPreParser.CollectGlSamplerSlots(
            PlatformMacros.For(PlatformTarget.DirectX).ToTextPrepend("t.fx") + (flattened ?? raw), "t.fx", parsed.Value);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        return result.Value;
    }

    [Theory]
    [InlineData("static float4 G = Tb[int2(0, 0)];")]                                                     // M10
    [InlineData("static Texture2D Tc = Tb;\nstatic float4 G = 0;")]                                      // M18
    [InlineData("float4 L(Texture2D t) { return t.Load(int3(0, 0, 0)); }\nstatic float4 G = L(Tb);")]   // M20, through an argument
    [InlineData("static float4 G = 0;\nfloat4 Q() { return Tb[int2(1, 1)]; }")]                          // inside a function body
    public void AValueUse_IsFound(string use)
    {
        GlSamplerSlots slots = Slots("sampler2D A; Texture2D Tb; sampler SB = sampler_state { Texture = <Tb>; };\n" + use + Ps);
        slots.ValueUseTextures.ShouldBe(["Tb"]);
    }

    [Theory]
    [InlineData("Texture2D Tb; sampler SB = sampler_state { Texture = <Tb>; };")]
    [InlineData("texture2D Tb; sampler SB = sampler_state { Texture = (Tb); };")]
    [InlineData("texture Tb : register(t3); sampler SB = sampler_state { texture = Tb; };")]
    [InlineData("Texture2D<float4> Tb; sampler SB = sampler_state { Texture = <Tb>; };")]
    [InlineData("texture2D Tz, Tb; sampler SB = sampler_state { Texture = <Tb>; };")]                    // M21: a declarator list
    [InlineData("texture2D Tb; sampler SB { Texture = <Tb>; };")]
    public void ADeclarationOrAStateBinding_IsNotAValueUse(string declarations)
    {
        GlSamplerSlots slots = Slots("sampler2D A; " + declarations + "\nstatic float4 G = 0;" + Ps);
        slots.ValueUseTextures.ShouldBeEmpty();
        slots.ModernMethodTextures.ShouldBeEmpty();
    }

    [Fact]
    public void AMethodCall_IsAMethodUse_NotAValueUse()
    {
        GlSamplerSlots slots = Slots("sampler2D A; Texture2D Tb; sampler SB = sampler_state { Texture = <Tb>; };\n" +
                                     "static float4 G = Tb.Load(int3(0, 0, 0));" + Ps);
        slots.ModernMethodTextures.ShouldBe(["Tb"]);
        slots.ValueUseTextures.ShouldBeEmpty();
    }

    [Fact]
    public void AnIncludedStateSampler_IsKnown_AndItsTexturesDirectUseFound()
    {
        // I08: the raw pre-parse never sees SB (it is in the header), so the rewrite does not bind
        // it; the preprocessed view still knows SB stands for Tb, and that Tb is loaded directly.
        const string raw = "sampler2D A;\n#include \"s.fxh\"\nstatic float4 G = Tb.Load(int3(0, 0, 0));" + Ps;
        const string flattened = "sampler2D A;\n#line 1 \"s.fxh\"\ntexture2D Tb; sampler SB = sampler_state { Texture = <Tb>; };\n" +
                                 "#line 3 \"t.fx\"\nstatic float4 G = Tb.Load(int3(0, 0, 0));" + Ps;
        GlSamplerSlots slots = Slots(raw, flattened);
        slots.StateTextures["SB"].ShouldBe("Tb");
        slots.ModernMethodTextures.ShouldBe(["Tb"]);
    }

    [Fact]
    public void ARegisteredStateSampler_IsKnownByItsName()
    {
        GlSamplerSlots slots = Slots("sampler2D A; texture2D Tb; sampler SB : register(s2) = sampler_state { Texture = <Tb>; };\n" +
                                     "static float4 G = 0;" + Ps);
        slots.StateTextures["SB"].ShouldBe("Tb");
    }
}
