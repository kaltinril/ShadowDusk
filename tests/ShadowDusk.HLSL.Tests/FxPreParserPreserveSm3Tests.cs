#nullable enable
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.Core;
using FluentAssertions;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

// -----------------------------------------------------------------------------
// FxSourceMode.PreserveSm3 (the FNA fx_2_0 target): the pre-parser still strips
// technique/pass and parameter-annotation blocks and captures all the same
// metadata, but every legacy D3D9 construct in the shader body — sampler_state
// initializers, 'texture' declarations, tex2D calls, COLOR semantics — passes
// through VERBATIM, because vkd3d's D3D_BYTECODE profile accepts them natively.
// -----------------------------------------------------------------------------

public sealed class FxPreParserPreserveSm3Tests
{
    // -------------------------------------------------------------------------
    // (a) tex2D call survives verbatim — no '.Sample' rewrite, no synthesized texture
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_Tex2DCall_SurvivesVerbatim()
    {
        const string source = """
            texture t;
            sampler s = sampler_state { Texture = <t>; };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(s, uv);
            }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("tex2D(s, uv)");
        stripped.Should().NotContain(".Sample");
        stripped.Should().NotContain("_SDTexture");
    }

    // -------------------------------------------------------------------------
    // (b) ': COLOR' / ': COLOR0' return semantics survive — no SV_Target rewrite
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_ColorReturnSemantics_SurviveVerbatim()
    {
        const string source = """
            float4 PSA(float2 uv : TEXCOORD0) : COLOR
            {
                return float4(1, 0, 0, 1);
            }

            float4 PSB(float2 uv : TEXCOORD0) : COLOR0
            {
                return float4(0, 1, 0, 1);
            }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.Should().BeTrue();

        // Normalize line endings so the bare ': COLOR' (no digit) can be asserted
        // distinctly from ': COLOR0' regardless of checkout newline style.
        string stripped = result.Value.StrippedHlsl.Replace("\r\n", "\n");

        stripped.Should().Contain(": COLOR\n");
        stripped.Should().Contain(": COLOR0\n");
        stripped.Should().NotContain("SV_Target");
    }

    // -------------------------------------------------------------------------
    // (c) legacy 'texture t;' declaration survives — no Texture2D rewrite
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_LegacyTextureDecl_SurvivesVerbatim()
    {
        const string source = """
            texture t;

            float4 PS() : COLOR { return float4(1, 1, 1, 1); }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain("texture t;");
        result.Value.StrippedHlsl.Should().NotContain("Texture2D");
    }

    // -------------------------------------------------------------------------
    // (d) sampler_state declaration survives verbatim AND SamplerInfo is still captured
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_SamplerStateDecl_VerbatimAndMetadataCaptured()
    {
        const string source = """
            texture t;
            sampler s = sampler_state { Texture = <t>; MipFilter = LINEAR; };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(s, uv);
            }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The whole declaration — including the angle-bracket texture binding and
        // the state block — passes through verbatim. No erasure, no SamplerState.
        stripped.Should().Contain("sampler s = sampler_state { Texture = <t>; MipFilter = LINEAR; };");
        stripped.Should().NotContain("SamplerState");

        // Metadata capture is identical to the default mode.
        result.Value.Samplers.Should().ContainSingle();
        var sampler = result.Value.Samplers[0];
        sampler.Name.Should().Be("s");
        sampler.SamplerType.Should().Be("sampler");
        sampler.TextureReference.Should().Be("t");
        sampler.StateEntries.Should().ContainSingle(e => e.Key == "MipFilter" && e.Value == "LINEAR");
    }

    // -------------------------------------------------------------------------
    // (e) bare 'sampler s0 : register(s0);' survives, even when tex2D references it
    //     (the default mode would synthesize a Texture2D + SamplerState pair)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_BareSamplerWithRegister_SurvivesVerbatim()
    {
        const string source = """
            sampler s0 : register(s0);

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(s0, uv);
            }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("sampler s0 : register(s0);");
        stripped.Should().NotContain("SamplerState");
        stripped.Should().NotContain("_SDTexture");
        stripped.Should().Contain("tex2D(s0, uv)");
    }

    // -------------------------------------------------------------------------
    // (f) technique blocks ARE still stripped (blank lines) and fully captured
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_TechniqueBlock_StillStrippedAndCaptured()
    {
        const string source =
            "float4 MyColor;\n" +                                                 // line 1
            "\n" +                                                                // line 2
            "technique T\n" +                                                     // line 3
            "{\n" +                                                               // line 4
            "    pass P1\n" +                                                     // line 5
            "    {\n" +                                                           // line 6
            "        CullMode     = None;\n" +                                    // line 7
            "        VertexShader = compile vs_3_0 VSMain();\n" +                 // line 8
            "        PixelShader  = compile ps_3_0 PSMain();\n" +                 // line 9
            "    }\n" +                                                           // line 10
            "}\n";                                                                // line 11

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The technique block is gone from the output, replaced by blank lines so
        // the total line count is unchanged.
        stripped.Should().NotContain("technique");
        stripped.Should().NotContain("compile");
        var lines = stripped.Replace("\r\n", "\n").Split('\n');
        lines.Length.Should().Be(source.Replace("\r\n", "\n").Split('\n').Length);
        lines[0].Should().Contain("MyColor");
        for (int i = 2; i <= 10; i++)
            lines[i].Trim().Should().BeEmpty($"line {i + 1} held stripped technique text");

        // Metadata capture is identical to the default mode.
        result.Value.Techniques.Should().ContainSingle();
        var tech = result.Value.Techniques[0];
        tech.Name.Should().Be("T");
        tech.Passes.Should().ContainSingle();

        var pass = tech.Passes[0];
        pass.Name.Should().Be("P1");
        pass.VertexEntryPoint.Should().Be("VSMain");
        pass.VertexProfile.Should().Be("vs_3_0");
        pass.PixelEntryPoint.Should().Be("PSMain");
        pass.PixelProfile.Should().Be("ps_3_0");
        pass.RenderStates.Should().ContainSingle(rs => rs.Key == "CullMode" && rs.Value == "None");
    }

    // -------------------------------------------------------------------------
    // (g) parameter annotations are still stripped and captured
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_ParameterAnnotation_StillStrippedAndCaptured()
    {
        const string source = "float P < float UIMin = 0; > = 0.5;";

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.Should().BeTrue();
        result.Value.ParameterAnnotations.Should().ContainSingle();

        var pa = result.Value.ParameterAnnotations[0];
        pa.ParameterName.Should().Be("P");
        pa.Entries.Should().ContainSingle(e => e.Name == "UIMin");

        // The annotation block is stripped (vkd3d's acceptance of global
        // annotations is unverified); the assignment survives.
        result.Value.StrippedHlsl.Should().NotContain("<");
        result.Value.StrippedHlsl.Should().NotContain("UIMin");
        result.Value.StrippedHlsl.Should().Contain("= 0.5");
    }

    // -------------------------------------------------------------------------
    // (h) an UNUSED sampler_state decl (no tex2D reference) also survives verbatim —
    //     the default mode erases it; assert the mode difference explicitly
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_UnusedSamplerStateDecl_SurvivesWhereDefaultModeErases()
    {
        const string source = """
            sampler2D UnusedSampler = sampler_state
            {
                Texture = <SomeTexture>;
            };

            float4 PS() : COLOR { return float4(1, 1, 1, 1); }
            """;

        var preserved = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);
        var rewritten = FxPreParser.Parse(source, "test.fx", FxSourceMode.RewriteToSm4);

        preserved.IsSuccess.Should().BeTrue();
        rewritten.IsSuccess.Should().BeTrue();

        // PreserveSm3: the declaration stays, verbatim.
        preserved.Value.StrippedHlsl.Should().Contain("sampler2D UnusedSampler = sampler_state");
        preserved.Value.StrippedHlsl.Should().Contain("Texture = <SomeTexture>;");

        // RewriteToSm4 (pre-existing behavior): the unused declaration is erased.
        rewritten.Value.StrippedHlsl.Should().NotContain("sampler_state");
        rewritten.Value.StrippedHlsl.Should().NotContain("UnusedSampler");

        // Both modes capture the same sampler metadata.
        preserved.Value.Samplers.Should().ContainSingle();
        preserved.Value.Samplers[0].Name.Should().Be("UnusedSampler");
        rewritten.Value.Samplers.Should().ContainSingle();
        rewritten.Value.Samplers[0].Name.Should().Be("UnusedSampler");
    }

    // -------------------------------------------------------------------------
    // (i) regression pin: the 2-arg Parse is exactly Parse(..., RewriteToSm4)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_TwoArgOverload_IsExactlyRewriteToSm4()
    {
        // A representative legacy source exercising every rewrite the default mode
        // performs: texture decl, sampler_state decl, tex2D, COLOR return semantic,
        // parameter annotation, and a technique block.
        const string source = """
            texture _tex < string ResourceName = "wall.png"; >;
            sampler _texSampler = sampler_state { Texture = <_tex>; MinFilter = LINEAR; };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(_texSampler, uv);
            }

            technique T
            {
                pass P1
                {
                    PixelShader = compile ps_3_0 PS();
                }
            }
            """;

        var twoArg = FxPreParser.Parse(source, "test.fx");
        var threeArg = FxPreParser.Parse(source, "test.fx", FxSourceMode.RewriteToSm4);

        twoArg.IsSuccess.Should().BeTrue();
        threeArg.IsSuccess.Should().BeTrue();

        twoArg.Value.StrippedHlsl.Should().Be(threeArg.Value.StrippedHlsl);
        twoArg.Value.Techniques.Should().BeEquivalentTo(threeArg.Value.Techniques);
        twoArg.Value.Samplers.Should().BeEquivalentTo(threeArg.Value.Samplers);
        twoArg.Value.ParameterAnnotations.Should().BeEquivalentTo(threeArg.Value.ParameterAnnotations);
    }

    // -------------------------------------------------------------------------
    // (j) brace-form sampler declarations (finding F1): fxc treats
    //     'sampler S { ... };' exactly like 'sampler S = sampler_state { ... };'.
    //     On the FNA path the un-recognized brace form silently lost ALL sampler
    //     states AND the texture binding — capture must be identical to the
    //     keyword form, and the declaration must still pass through verbatim.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_BraceFormSampler_CaptureIdenticalToKeywordForm()
    {
        const string braceForm = """
            texture t;
            sampler s { Texture = <t>; MinFilter = LINEAR; AddressU = CLAMP; };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(s, uv);
            }
            """;
        const string keywordForm = """
            texture t;
            sampler s = sampler_state { Texture = <t>; MinFilter = LINEAR; AddressU = CLAMP; };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(s, uv);
            }
            """;

        var brace = FxPreParser.Parse(braceForm, "test.fx", FxSourceMode.PreserveSm3);
        var keyword = FxPreParser.Parse(keywordForm, "test.fx", FxSourceMode.PreserveSm3);

        brace.IsSuccess.Should().BeTrue();
        keyword.IsSuccess.Should().BeTrue();

        // Identical SamplerInfo capture (spans differ — the sources differ in length).
        brace.Value.Samplers.Should().ContainSingle();
        keyword.Value.Samplers.Should().ContainSingle();

        var b = brace.Value.Samplers[0];
        var k = keyword.Value.Samplers[0];
        b.Name.Should().Be(k.Name).And.Be("s");
        b.SamplerType.Should().Be(k.SamplerType).And.Be("sampler");
        b.TextureReference.Should().Be(k.TextureReference).And.Be("t");
        b.StateEntries.Select(e => (e.Key, e.Value)).Should().Equal(
            k.StateEntries.Select(e => (e.Key, e.Value)));
        b.StateEntries.Select(e => (e.Key, e.Value)).Should().Equal(
            ("MinFilter", "LINEAR"), ("AddressU", "CLAMP"));

        // Same passthrough behavior as the keyword form: the declaration (and the
        // tex2D call) survive verbatim — no erasure, no SamplerState rewrite.
        string stripped = brace.Value.StrippedHlsl;
        stripped.Should().Contain("sampler s { Texture = <t>; MinFilter = LINEAR; AddressU = CLAMP; };");
        stripped.Should().Contain("tex2D(s, uv)");
        stripped.Should().NotContain("SamplerState");
        stripped.Should().NotContain("_SDTexture");
    }

    [Fact]
    public void Parse_PreserveSm3_BraceFormSamplerWithRegister_VerbatimAndCaptured()
    {
        // ': register(s0)' between the name and the '{' (the lexer drops the ':').
        // Without brace-form recognition this matched the bare Form 3 shape and
        // captured nothing.
        const string source = """
            texture t;
            sampler s : register(s0)
            {
                Texture = <t>;
                MinFilter = LINEAR;
            };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(s, uv);
            }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.Should().BeTrue();

        result.Value.Samplers.Should().ContainSingle();
        var sampler = result.Value.Samplers[0];
        sampler.Name.Should().Be("s");
        sampler.TextureReference.Should().Be("t");
        sampler.StateEntries.Should().ContainSingle(e => e.Key == "MinFilter" && e.Value == "LINEAR");

        // The whole declaration — register clause included — passes through verbatim.
        string stripped = result.Value.StrippedHlsl;
        stripped.Should().Contain("sampler s : register(s0)");
        stripped.Should().Contain("Texture = <t>;");
        stripped.Should().Contain("MinFilter = LINEAR;");
        stripped.Should().NotContain("SamplerState");
    }

    [Fact]
    public void Parse_PreserveSm3_ParenTextureRef_CapturedInBothBlockForms()
    {
        // 'Texture = (X);' — ubiquitous legacy XNA syntax fxc accepts identically
        // to '<X>'. Previously a parse ERROR, in both block forms.
        const string source = """
            texture tA;
            texture tB;
            sampler kw = sampler_state { Texture = (tA); };
            sampler br { Texture = (tB); };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(kw, uv) + tex2D(br, uv);
            }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.Should().BeTrue();
        result.Value.Samplers.Should().HaveCount(2);
        result.Value.Samplers[0].Name.Should().Be("kw");
        result.Value.Samplers[0].TextureReference.Should().Be("tA");
        result.Value.Samplers[1].Name.Should().Be("br");
        result.Value.Samplers[1].TextureReference.Should().Be("tB");

        // Both declarations pass through verbatim, parens intact.
        result.Value.StrippedHlsl.Should().Contain("Texture = (tA);");
        result.Value.StrippedHlsl.Should().Contain("Texture = (tB);");
    }

    // -------------------------------------------------------------------------
    // (k) the live bug case: tests/fixtures/shaders/ClipShaderNew.fx uses the
    //     brace form with a paren texture ref. Before brace-form recognition its
    //     FNA compile silently lost every sampler state and the MaskSampler→Mask
    //     binding (no texture parameter, no sampler→texture map record).
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_ClipShaderNewFixture_SamplerMetadataCaptured()
    {
        string source = ReadFixture("ClipShaderNew.fx");

        var result = FxPreParser.Parse(source, "ClipShaderNew.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.Should().BeTrue();

        // The brace-form sampler is captured with its texture binding and all
        // five state entries (TextureSampler is a bare Form 3 decl — no block,
        // so no SamplerInfo, as for every bare sampler).
        result.Value.Samplers.Should().ContainSingle();
        var sampler = result.Value.Samplers[0];
        sampler.Name.Should().Be("MaskSampler");
        sampler.SamplerType.Should().Be("sampler");
        sampler.TextureReference.Should().Be("Mask");
        sampler.StateEntries.Select(e => (e.Key, e.Value)).Should().Equal(
            ("MagFilter", "LINEAR"),
            ("MinFilter", "LINEAR"),
            ("Mipfilter", "LINEAR"),
            ("AddressU", "CLAMP"),
            ("AddressV", "CLAMP"));

        // The declaration still passes through verbatim for vkd3d, and the
        // technique block is stripped and captured as usual.
        string stripped = result.Value.StrippedHlsl;
        stripped.Should().Contain("sampler MaskSampler");
        stripped.Should().Contain("Texture = (Mask);");
        stripped.Should().NotContain("technique");
        result.Value.Techniques.Should().ContainSingle();
        result.Value.Techniques[0].Name.Should().Be("SpriteBatch");
        result.Value.Techniques[0].Passes.Should().ContainSingle();
        result.Value.Techniques[0].Passes[0].PixelEntryPoint.Should().Be("SpritePixelShader");
    }

    /// <summary>Reads a fixture embedded into this test assembly (see the csproj) —
    /// the real on-disk fixture file, without a runtime disk dependency.</summary>
    private static string ReadFixture(string fileName)
    {
        using Stream stream = typeof(FxPreParserPreserveSm3Tests).Assembly
            .GetManifestResourceStream($"ShadowDusk.HLSL.Tests.fixtures.{fileName}")
            ?? throw new InvalidOperationException($"Embedded fixture '{fileName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
