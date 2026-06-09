#nullable enable
using ShadowDusk.Core;
using ShadowDusk.HLSL;
using FluentAssertions;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

// -----------------------------------------------------------------------------
// Sm3StageReservationRewriter: vkd3d 1.17 fails with E5017 "Reservation shader
// target" on D3D9 stage-scoped register binds (': register(vs, c0)') but honors
// the plain ': register(c0)' form, so the FNA fx_2_0 pipeline rewrites the source
// per stage before each vkd3d compile — the compiling stage's reservation loses
// its stage prefix, the other stage's reservation is removed entirely.
// -----------------------------------------------------------------------------

public sealed class Sm3StageReservationRewriterTests
{
    // -------------------------------------------------------------------------
    // Compiling-stage match: stage prefix dropped, body kept
    // -------------------------------------------------------------------------

    [Fact]
    public void Rewrite_VertexCompile_VsReservation_KeepsBodyWithoutStagePrefix()
    {
        const string source = "float4 WorldViewProj : register(vs, c0);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Vertex);

        rewritten.Should().Be("float4 WorldViewProj : register(c0);");
    }

    [Fact]
    public void Rewrite_PixelCompile_PsReservation_KeepsBodyWithoutStagePrefix()
    {
        const string source = "float4 Tint : register(ps, c1);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Pixel);

        rewritten.Should().Be("float4 Tint : register(c1);");
    }

    // -------------------------------------------------------------------------
    // Other-stage match: the whole reservation (including the ':') is removed
    // -------------------------------------------------------------------------

    [Fact]
    public void Rewrite_VertexCompile_PsReservation_RemovedEntirely()
    {
        const string source = "float4 Tint : register(ps, c1);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Vertex);

        rewritten.Should().Be("float4 Tint ;");
        rewritten.Should().NotContain("register");
    }

    [Fact]
    public void Rewrite_PixelCompile_VsReservation_RemovedEntirely()
    {
        const string source = "float4 WorldViewProj : register(vs, c0);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Pixel);

        rewritten.Should().Be("float4 WorldViewProj ;");
        rewritten.Should().NotContain("register");
    }

    // -------------------------------------------------------------------------
    // Plain (stage-less) reservations are never touched
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(ShaderStage.Vertex)]
    [InlineData(ShaderStage.Pixel)]
    public void Rewrite_PlainReservation_Untouched(ShaderStage stage)
    {
        const string source = "float4 Color : register(c0);\nsampler s : register(s0);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, stage);

        rewritten.Should().Be(source);
    }

    // -------------------------------------------------------------------------
    // Both-stage declaration: one reservation kept (de-prefixed), the other removed
    // -------------------------------------------------------------------------

    [Fact]
    public void Rewrite_BothStageDeclaration_VertexCompile_KeepsVsDropsPs()
    {
        const string source = "float4 C : register(vs, c0) : register(ps, c4);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Vertex);

        rewritten.Should().Be("float4 C : register(c0) ;");
        rewritten.Should().NotContain("c4");
    }

    [Fact]
    public void Rewrite_BothStageDeclaration_PixelCompile_KeepsPsDropsVs()
    {
        const string source = "float4 C : register(vs, c0) : register(ps, c4);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Pixel);

        rewritten.Should().Be("float4 C  : register(c4);");
        rewritten.Should().NotContain("c0");
    }

    // -------------------------------------------------------------------------
    // Case tolerance: stage token (and 'register') matched case-insensitively
    // -------------------------------------------------------------------------

    [Fact]
    public void Rewrite_UppercaseStageToken_StillMatched()
    {
        const string source = "float4 X : register(VS, c2);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Vertex);

        rewritten.Should().Be("float4 X : register(c2);");
    }

    [Fact]
    public void Rewrite_MixedCaseRegisterKeyword_StillMatched()
    {
        const string source = "float4 X : Register(Ps, c3);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Pixel);

        rewritten.Should().Be("float4 X : register(c3);");
    }

    // -------------------------------------------------------------------------
    // Flexible whitespace inside the reservation
    // -------------------------------------------------------------------------

    [Fact]
    public void Rewrite_FlexibleWhitespace_MatchedAndBodyTrimmed()
    {
        const string source = "float4 Y :  register ( vs ,  c0 ) ;";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Vertex);

        rewritten.Should().Be("float4 Y : register(c0) ;");
    }

    // -------------------------------------------------------------------------
    // Comments and string literals are never rewritten
    // -------------------------------------------------------------------------

    [Fact]
    public void Rewrite_PatternInsideLineComment_Untouched()
    {
        const string source = "// old: float4 X : register(ps, c0);\nfloat4 Y;";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Vertex);

        rewritten.Should().Be(source);
    }

    [Fact]
    public void Rewrite_PatternInsideBlockComment_Untouched_RealCodeStillRewritten()
    {
        const string source = "/* float4 X : register(ps, c0); */ float4 Y : register(vs, c1);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Vertex);

        rewritten.Should().Be("/* float4 X : register(ps, c0); */ float4 Y : register(c1);");
    }

    [Fact]
    public void Rewrite_PatternInsideStringLiteral_Untouched()
    {
        const string source = "string note = \": register(vs, c0)\";";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Pixel);

        rewritten.Should().Be(source);
    }

    // -------------------------------------------------------------------------
    // Sampler registers use the same stage-scoped form
    // -------------------------------------------------------------------------

    [Fact]
    public void Rewrite_SamplerRegister_PixelCompile_KeepsBody()
    {
        const string source = "sampler TextureSampler : register(ps, s0);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Pixel);

        rewritten.Should().Be("sampler TextureSampler : register(s0);");
    }

    [Fact]
    public void Rewrite_SamplerRegister_VertexCompile_Removed()
    {
        const string source = "sampler TextureSampler : register(ps, s0);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Vertex);

        rewritten.Should().Be("sampler TextureSampler ;");
    }

    // -------------------------------------------------------------------------
    // Identifiers that merely start with a stage token are not reservations
    // -------------------------------------------------------------------------

    [Fact]
    public void Rewrite_RegisterBodyStartingWithStageLetters_Untouched()
    {
        // 'vs'/'ps' must be a standalone token followed by ',' — an identifier
        // like 'psX' (or a malformed body) never matches.
        const string source = "float4 Z : register(psX);";

        string rewritten = Sm3StageReservationRewriter.Rewrite(source, ShaderStage.Pixel);

        rewritten.Should().Be(source);
    }
}
