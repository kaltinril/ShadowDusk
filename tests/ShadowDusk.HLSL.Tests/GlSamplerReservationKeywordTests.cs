#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// Issue #309: EVERY sampler-typed declaration with an explicit <c>register(sN)</c> reserves
/// register N on OpenGL, not only the exact keyword <c>SamplerState</c>. Measured against the
/// pinned <c>mgfxc</c> 3.8.4.1 <c>/Profile:OpenGL</c> (2026-10-02), one texture read through
/// <c>Tex.Sample(S, uv)</c>: <c>sampler S : register(s0)</c> is <c>ps_s1</c> (ShadowDusk said
/// <c>ps_s0</c>); an UNUSED <c>sampler</c>, <c>sampler2D</c>, <c>samplerCUBE</c> or
/// <c>SamplerComparisonState</c> with <c>register(s0)</c> next to an unannotated
/// <c>SamplerState S</c> is <c>ps_s1</c> too; and a legacy <c>sampler X : register(s0)</c> that
/// only ANOTHER entry point reads through <c>tex2D</c> still pushes this entry point's
/// <c>sampler2D Y</c> to <c>ps_s1</c>. Pure tests on the preprocessed reading.
/// </summary>
public sealed class GlSamplerReservationKeywordTests
{
    private static string Gl(string body) =>
        PlatformMacros.For(PlatformTarget.OpenGL).ToTextPrepend("t.fx") + body;

    private static GlSamplerSlots Slots(string raw)
    {
        var parsed = FxPreParser.Parse(raw, "t.fx");
        parsed.IsSuccess.ShouldBeTrue(parsed.IsFailure ? parsed.Error.Message : "ok");
        var result = FxPreParser.CollectGlSamplerSlots(Gl(raw), "t.fx", parsed.Value);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        return result.Value;
    }

    private const string ModernPs = """

        float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return Tex.Sample(S, uv); }
        technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
        """;

    [Theory]
    [InlineData("sampler S : register(s0);")]
    [InlineData("sampler S : register(s0) = sampler_state { Texture = <Tex>; };")]
    [InlineData("SamplerState S : register(s0);")]
    public void ASamplerReadThroughSample_ReservesItsRegister_WhateverTheKeyword(string declaration)
    {
        GlSamplerSlots slots = Slots("Texture2D Tex;\n" + declaration + ModernPs);

        slots.Reserved.ShouldBe(new[] { 0 });
        slots.Explicit.ShouldBeEmpty("a reservation is not a pin");
    }

    [Theory]
    [InlineData("sampler U : register(s0);")]
    [InlineData("sampler2D U : register(s0);")]
    [InlineData("samplerCUBE U : register(s0);")]
    [InlineData("sampler2D U : register(s0) = sampler_state { Texture = <Tex>; };")]
    [InlineData("SamplerComparisonState U : register(s0);")]
    public void AnUnusedSamplerWithARegister_StillReserves(string declaration)
    {
        GlSamplerSlots slots = Slots("Texture2D Tex;\nSamplerState S;\n" + declaration + ModernPs);

        slots.Reserved.ShouldBe(new[] { 0 });
    }

    [Fact]
    public void ALegacySamplerReadByTex2D_IsPinnedAndReserved()
    {
        // Pinned, so its own pair lands on s1 in pass 1 of ResolveSlots; reserved, so a pair in
        // an entry point that does not read it (mgfxc compiles each one separately) stays off s1.
        const string raw = """
            sampler X : register(s1);
            sampler2D Y;
            float4 PSA(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(X, uv); }
            float4 PSB(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(Y, uv); }
            technique A { pass P { PixelShader = compile ps_3_0 PSA(); } }
            technique B { pass P { PixelShader = compile ps_3_0 PSB(); } }
            """;

        GlSamplerSlots slots = Slots(raw);

        slots.Explicit.ShouldBe(new Dictionary<string, int> { ["X_SDTexture"] = 1 });
        slots.Reserved.ShouldBe(new[] { 1 });
    }

    [Fact]
    public void ARegisterOnlyInTheInactiveBranch_OrWithoutARegister_ReservesNothing()
    {
        Slots("Texture2D Tex;\n#if OPENGL\nsampler S;\n#else\nsampler S : register(s0);\n#endif" + ModernPs)
            .Reserved.ShouldBeEmpty();
        Slots("Texture2D Tex;\nsampler S;\nSamplerComparisonState C;" + ModernPs).Reserved.ShouldBeEmpty();
    }

    [Fact]
    public void ARegisterSpelledThroughAMacro_OrDeclaredInAnInclude_Reserves()
    {
        Slots("Texture2D Tex;\n#define REG s0\nsampler S : register(REG);" + ModernPs).Reserved.ShouldBe(new[] { 0 });
        Slots("Texture2D Tex;\n#define SLOT(n) : register(n)\nsampler S SLOT(s2);" + ModernPs).Reserved.ShouldBe(new[] { 2 });

        // An include, as the flattener leaves it.
        string flattened = Gl("Texture2D Tex;\n#line 1 \"s.fxh\"\nsampler S : register(s3);\n#line 3 \"t.fx\"\n" + ModernPs);
        var parsed = FxPreParser.Parse("Texture2D Tex;\n#include \"s.fxh\"" + ModernPs, "t.fx");
        FxPreParser.CollectGlSamplerSlots(flattened, "t.fx", parsed.Value).Value.Reserved.ShouldBe(new[] { 3 });
    }

    [Fact]
    public void TheRawReading_WidensTheSameWay()
    {
        var parsed = FxPreParser.Parse("Texture2D Tex;\nsampler S : register(s0);\nSamplerComparisonState C : register(s2);" + ModernPs, "t.fx");

        parsed.Value.ReservedGlSamplerSlots.OrderBy(x => x).ShouldBe(new[] { 0, 2 });
    }
}
