#nullable enable
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.Core;
using Shouldly;
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
    public void Parse_PreserveSm3_Tex2DlodCall_SurvivesVerbatim_NoFx0012()
    {
        // The RewriteToSm4 mode fails loudly (FX0012) on tex2Dlod; PreserveSm3 must
        // NOT — vkd3d compiles the legacy intrinsic natively for the FNA target.
        const string source = """
            texture t;
            sampler s = sampler_state { Texture = <t>; };

            float4 PS(float4 uv : TEXCOORD0) : COLOR
            {
                return tex2Dlod(s, uv);
            }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("tex2Dlod(s, uv)", Case.Sensitive);
    }

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

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("tex2D(s, uv)", Case.Sensitive);
        stripped.ShouldNotContain(".Sample", Case.Sensitive);
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);
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

        result.IsSuccess.ShouldBeTrue();

        // Normalize line endings so the bare ': COLOR' (no digit) can be asserted
        // distinctly from ': COLOR0' regardless of checkout newline style.
        string stripped = result.Value.StrippedHlsl.Replace("\r\n", "\n");

        stripped.ShouldContain(": COLOR\n", Case.Sensitive);
        stripped.ShouldContain(": COLOR0\n", Case.Sensitive);
        stripped.ShouldNotContain("SV_Target", Case.Sensitive);
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("texture t;", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("Texture2D", Case.Sensitive);
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

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The whole declaration — including the angle-bracket texture binding and
        // the state block — passes through verbatim. No erasure, no SamplerState.
        stripped.ShouldContain("sampler s = sampler_state { Texture = <t>; MipFilter = LINEAR; };", Case.Sensitive);
        stripped.ShouldNotContain("SamplerState", Case.Sensitive);

        // Metadata capture is identical to the default mode.
        result.Value.Samplers.ShouldHaveSingleItem();
        var sampler = result.Value.Samplers[0];
        sampler.Name.ShouldBe("s");
        sampler.SamplerType.ShouldBe("sampler");
        sampler.TextureReference.ShouldBe("t");
        sampler.StateEntries.Where(e => e.Key == "MipFilter" && e.Value == "LINEAR").ShouldHaveSingleItem();
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

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("sampler s0 : register(s0);", Case.Sensitive);
        stripped.ShouldNotContain("SamplerState", Case.Sensitive);
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);
        stripped.ShouldContain("tex2D(s0, uv)", Case.Sensitive);
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

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The technique block is gone from the output, replaced by blank lines so
        // the total line count is unchanged.
        stripped.ShouldNotContain("technique", Case.Sensitive);
        stripped.ShouldNotContain("compile", Case.Sensitive);
        var lines = stripped.Replace("\r\n", "\n").Split('\n');
        lines.Length.ShouldBe(source.Replace("\r\n", "\n").Split('\n').Length);
        lines[0].ShouldContain("MyColor", Case.Sensitive);
        for (int i = 2; i <= 10; i++)
            lines[i].Trim().ShouldBeEmpty($"line {i + 1} held stripped technique text");

        // Metadata capture is identical to the default mode.
        result.Value.Techniques.ShouldHaveSingleItem();
        var tech = result.Value.Techniques[0];
        tech.Name.ShouldBe("T");
        tech.Passes.ShouldHaveSingleItem();

        var pass = tech.Passes[0];
        pass.Name.ShouldBe("P1");
        pass.VertexEntryPoint.ShouldBe("VSMain");
        pass.VertexProfile.ShouldBe("vs_3_0");
        pass.PixelEntryPoint.ShouldBe("PSMain");
        pass.PixelProfile.ShouldBe("ps_3_0");
        pass.RenderStates.Where(rs => rs.Key == "CullMode" && rs.Value == "None").ShouldHaveSingleItem();
    }

    // -------------------------------------------------------------------------
    // (g) parameter annotations are still stripped and captured
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_ParameterAnnotation_StillStrippedAndCaptured()
    {
        const string source = "float P < float UIMin = 0; > = 0.5;";

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ParameterAnnotations.ShouldHaveSingleItem();

        var pa = result.Value.ParameterAnnotations[0];
        pa.ParameterName.ShouldBe("P");
        pa.Entries.Where(e => e.Name == "UIMin").ShouldHaveSingleItem();

        // The annotation block is stripped (vkd3d's acceptance of global
        // annotations is unverified); the assignment survives.
        result.Value.StrippedHlsl.ShouldNotContain("<", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("UIMin", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("= 0.5", Case.Sensitive);
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

        preserved.IsSuccess.ShouldBeTrue();
        rewritten.IsSuccess.ShouldBeTrue();

        // PreserveSm3: the declaration stays, verbatim.
        preserved.Value.StrippedHlsl.ShouldContain("sampler2D UnusedSampler = sampler_state", Case.Sensitive);
        preserved.Value.StrippedHlsl.ShouldContain("Texture = <SomeTexture>;", Case.Sensitive);

        // RewriteToSm4 (pre-existing behavior): the unused declaration is erased.
        rewritten.Value.StrippedHlsl.ShouldNotContain("sampler_state", Case.Sensitive);
        rewritten.Value.StrippedHlsl.ShouldNotContain("UnusedSampler", Case.Sensitive);

        // Both modes capture the same sampler metadata.
        preserved.Value.Samplers.ShouldHaveSingleItem();
        preserved.Value.Samplers[0].Name.ShouldBe("UnusedSampler");
        rewritten.Value.Samplers.ShouldHaveSingleItem();
        rewritten.Value.Samplers[0].Name.ShouldBe("UnusedSampler");
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

        twoArg.IsSuccess.ShouldBeTrue();
        threeArg.IsSuccess.ShouldBeTrue();

        twoArg.Value.StrippedHlsl.ShouldBe(threeArg.Value.StrippedHlsl);
        // ShouldBeEquivalentTo, not ShouldBe: these elements are reference types without
        // value equality, so ShouldBe would compare identities and never match.
        twoArg.Value.Techniques.ShouldBeEquivalentTo(threeArg.Value.Techniques);
        twoArg.Value.Samplers.ShouldBeEquivalentTo(threeArg.Value.Samplers);
        twoArg.Value.ParameterAnnotations.ShouldBeEquivalentTo(threeArg.Value.ParameterAnnotations);
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

        brace.IsSuccess.ShouldBeTrue();
        keyword.IsSuccess.ShouldBeTrue();

        // Identical SamplerInfo capture (spans differ — the sources differ in length).
        brace.Value.Samplers.ShouldHaveSingleItem();
        keyword.Value.Samplers.ShouldHaveSingleItem();

        var b = brace.Value.Samplers[0];
        var k = keyword.Value.Samplers[0];
        b.Name.ShouldBe(k.Name);
        b.Name.ShouldBe("s");
        b.SamplerType.ShouldBe(k.SamplerType);
        b.SamplerType.ShouldBe("sampler");
        b.TextureReference.ShouldBe(k.TextureReference);
        b.TextureReference.ShouldBe("t");
        b.StateEntries.Select(e => (e.Key, e.Value)).ShouldBe(
            k.StateEntries.Select(e => (e.Key, e.Value)));
        b.StateEntries.Select(e => (e.Key, e.Value)).ShouldBe(new[] {
            ("MinFilter", "LINEAR"), ("AddressU", "CLAMP")});

        // Same passthrough behavior as the keyword form: the declaration (and the
        // tex2D call) survive verbatim — no erasure, no SamplerState rewrite.
        string stripped = brace.Value.StrippedHlsl;
        stripped.ShouldContain("sampler s { Texture = <t>; MinFilter = LINEAR; AddressU = CLAMP; };", Case.Sensitive);
        stripped.ShouldContain("tex2D(s, uv)", Case.Sensitive);
        stripped.ShouldNotContain("SamplerState", Case.Sensitive);
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);
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

        result.IsSuccess.ShouldBeTrue();

        result.Value.Samplers.ShouldHaveSingleItem();
        var sampler = result.Value.Samplers[0];
        sampler.Name.ShouldBe("s");
        sampler.TextureReference.ShouldBe("t");
        sampler.StateEntries.Where(e => e.Key == "MinFilter" && e.Value == "LINEAR").ShouldHaveSingleItem();

        // The whole declaration — register clause included — passes through verbatim.
        string stripped = result.Value.StrippedHlsl;
        stripped.ShouldContain("sampler s : register(s0)", Case.Sensitive);
        stripped.ShouldContain("Texture = <t>;", Case.Sensitive);
        stripped.ShouldContain("MinFilter = LINEAR;", Case.Sensitive);
        stripped.ShouldNotContain("SamplerState", Case.Sensitive);
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.Samplers.Count().ShouldBe(2);
        result.Value.Samplers[0].Name.ShouldBe("kw");
        result.Value.Samplers[0].TextureReference.ShouldBe("tA");
        result.Value.Samplers[1].Name.ShouldBe("br");
        result.Value.Samplers[1].TextureReference.ShouldBe("tB");

        // Both declarations pass through verbatim, parens intact.
        result.Value.StrippedHlsl.ShouldContain("Texture = (tA);", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("Texture = (tB);", Case.Sensitive);
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

        result.IsSuccess.ShouldBeTrue();

        // The brace-form sampler is captured with its texture binding and all
        // five state entries (TextureSampler is a bare Form 3 decl — no block,
        // so no SamplerInfo, as for every bare sampler).
        result.Value.Samplers.ShouldHaveSingleItem();
        var sampler = result.Value.Samplers[0];
        sampler.Name.ShouldBe("MaskSampler");
        sampler.SamplerType.ShouldBe("sampler");
        sampler.TextureReference.ShouldBe("Mask");
        sampler.StateEntries.Select(e => (e.Key, e.Value)).ShouldBe(new[] {
            ("MagFilter", "LINEAR"),
            ("MinFilter", "LINEAR"),
            ("Mipfilter", "LINEAR"),
            ("AddressU", "CLAMP"),
            ("AddressV", "CLAMP")});

        // The declaration still passes through verbatim for vkd3d, and the
        // technique block is stripped and captured as usual.
        string stripped = result.Value.StrippedHlsl;
        stripped.ShouldContain("sampler MaskSampler", Case.Sensitive);
        stripped.ShouldContain("Texture = (Mask);", Case.Sensitive);
        stripped.ShouldNotContain("technique", Case.Sensitive);
        result.Value.Techniques.ShouldHaveSingleItem();
        result.Value.Techniques[0].Name.ShouldBe("SpriteBatch");
        result.Value.Techniques[0].Passes.ShouldHaveSingleItem();
        result.Value.Techniques[0].Passes[0].PixelEntryPoint.ShouldBe("SpritePixelShader");
    }

    // -------------------------------------------------------------------------
    // (l) Issue #106 in PreserveSm3 (FNA) mode. The discriminator that gates the
    //     global-annotation heuristic lives BEFORE the mode-specific rewrites, so
    //     the fix is mode-independent — but the original fix only added tests in
    //     RewriteToSm4 mode. These prove relational/shift/ternary operators in a
    //     function body are NOT mistaken for an annotation on the FNA path either,
    //     and that the operator survives verbatim for vkd3d.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_TernaryReturnWithRelationalOperator_IssueExactSnippet_Compiles()
    {
        // The exact issue #106 helper, compiled on the FNA path.
        const string source = """
            float TernaryReturn(float value)
            {
                return value <= 0.5f ? 0.0f : 1.0f;
            }
            float4 PSMain() : COLOR { return TernaryReturn(0.25f).xxxx; }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, "Shader.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        // The relational operator survives verbatim so vkd3d sees the original '<='.
        result.Value.StrippedHlsl.ShouldContain("value <= 0.5f", Case.Sensitive);
        result.Value.ParameterAnnotations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("return a < b;", "a < b")]
    [InlineData("return a <= b;", "a <= b")]
    [InlineData("return a > b;", "a > b")]
    [InlineData("return a >= b;", "a >= b")]
    [InlineData("return a < b ? 1 : 0;", "a < b")]
    [InlineData("return a << b;", "a << b")]
    public void Parse_PreserveSm3_RelationalOrShiftOperatorInBody_NotTreatedAsAnnotation(
        string statement, string mustSurvive)
    {
        string source =
            "int Helper(int a, int b)\n" +
            "{\n" +
            $"    {statement}\n" +
            "}\n" +
            "float4 PSMain() : COLOR { return (float)Helper(1, 2); }\n" +
            "technique T\n" +
            "{\n" +
            "    pass P { PixelShader = compile ps_3_0 PSMain(); }\n" +
            "}\n";

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        // The operator must reach vkd3d unchanged (not consumed as an annotation).
        result.Value.StrippedHlsl.ShouldContain(mustSurvive, Case.Sensitive);
        result.Value.ParameterAnnotations.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // (m) a legacy 'texture T < ... >;' annotation block STILL strips correctly in
    //     PreserveSm3. This is the documented PreserveSm3 path (FxPreParser.cs
    //     ~lines 493-495): the legacy 'texture' type passes through verbatim, and
    //     any trailing annotation falls through to the GENERIC annotation strip —
    //     which the issue #106 discriminator now gates. A real annotation
    //     ('Identifier Identifier Equals' after '<') must still be recognized and
    //     removed, leaving the 'texture T;' declaration for vkd3d.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_LegacyTextureWithAnnotation_AnnotationStripped()
    {
        const string source = """
            texture Tex < string foo = "bar"; >;

            float4 PS() : COLOR { return float4(1, 1, 1, 1); }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The legacy 'texture' type passes through verbatim (no Texture2D rewrite on
        // the FNA path), but its FX annotation block is stripped.
        stripped.ShouldContain("texture Tex", Case.Sensitive);
        stripped.ShouldNotContain("Texture2D", Case.Sensitive);
        stripped.ShouldNotContain("<", Case.Sensitive);
        stripped.ShouldNotContain("foo", Case.Sensitive);
        stripped.ShouldNotContain("\"bar\"", Case.Sensitive);

        // The annotation was captured against the texture's name.
        result.Value.ParameterAnnotations.ShouldHaveSingleItem().ParameterName.ShouldBe("Tex");
        result.Value.ParameterAnnotations[0].Entries.ShouldHaveSingleItem().Name.ShouldBe("foo");
    }

    [Fact]
    public void Parse_PreserveSm3_LegacyTextureWithAnnotation_PreservesLineNumbers()
    {
        // The annotation strip must keep the source's total line count so vkd3d
        // diagnostics on later lines still point at the right line.
        const string source =
            "texture Tex < string foo = \"bar\"; >;\n" + // line 1
            "\n" +                                       // line 2
            "float4 PS() : COLOR { return 0; }\n";       // line 3

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        var lines = result.Value.StrippedHlsl.Replace("\r\n", "\n").Split('\n');

        lines.Length.ShouldBe(source.Replace("\r\n", "\n").Split('\n').Length);
        lines[0].ShouldContain("texture Tex", Case.Sensitive);
        lines[0].ShouldNotContain("foo", Case.Sensitive);
        lines[2].ShouldContain("float4 PS", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // (n) Phase 45 B3 — ColorWriteEnable flag masks parse on the FNA path too
    //     (render states are captured identically in both modes; the lexer drops
    //     '|', so the mask flags arrive as adjacent identifiers and must all be
    //     captured rather than failing on the second flag).
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_B3_ColorWriteEnableOredMask_Captured()
    {
        const string source = """
            technique T
            {
                pass P
                {
                    ColorWriteEnable = Red | Green | Blue;
                    PixelShader = compile ps_3_0 PS();
                }
            }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques[0].Passes[0].RenderStates
            .Where(e => e.Key == "ColorWriteEnable" && e.Value == "Red|Green|Blue").ShouldHaveSingleItem();
    }

    // -------------------------------------------------------------------------
    // (o) Phase 45 B8 — 'sampler S : register(s0) = sampler_state { … };' (register
    //     clause before '=') parses on the FNA path and passes through verbatim.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_B8_RegisterBeforeSamplerState_VerbatimAndCaptured()
    {
        const string source = """
            texture t;
            sampler s : register(s0) = sampler_state { Texture = <t>; MinFilter = LINEAR; };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(s, uv);
            }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();

        result.Value.Samplers.ShouldHaveSingleItem();
        var sampler = result.Value.Samplers[0];
        sampler.Name.ShouldBe("s");
        sampler.TextureReference.ShouldBe("t");
        sampler.StateEntries.Where(e => e.Key == "MinFilter" && e.Value == "LINEAR").ShouldHaveSingleItem();

        // Whole declaration (register clause + initializer) passes through verbatim.
        string stripped = result.Value.StrippedHlsl;
        stripped.ShouldContain("sampler s : register(s0) = sampler_state { Texture = <t>; MinFilter = LINEAR; };", Case.Sensitive);
        stripped.ShouldNotContain("SamplerState", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // (p) Phase 45 B9 — a trailing sampler-level FX annotation parses on the FNA
    //     path. The annotation is FX metadata vkd3d cannot parse, so it is erased
    //     (with line numbers preserved) while the rest of the declaration stays
    //     verbatim and the SamplerInfo is unaffected.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_B9_TrailingSamplerAnnotation_StrippedRestVerbatim()
    {
        const string source =
            "texture t;\n" +                                                          // line 1
            "sampler s = sampler_state { Texture = <t>; } < string UIName = \"x\"; >;\n" + // line 2
            "\n" +                                                                    // line 3
            "float4 PS(float2 uv : TEXCOORD0) : COLOR { return tex2D(s, uv); }\n";     // line 4

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The annotation block is gone; the sampler_state body stays verbatim.
        stripped.ShouldNotContain("UIName", Case.Sensitive);
        stripped.ShouldNotContain("\"x\"", Case.Sensitive);
        stripped.ShouldContain("sampler s = sampler_state { Texture = <t>; }", Case.Sensitive);
        stripped.ShouldContain("tex2D(s, uv)", Case.Sensitive);

        // Line count preserved (the annotation erasure keeps newlines).
        var lines = stripped.Replace("\r\n", "\n").Split('\n');
        lines.Length.ShouldBe(source.Replace("\r\n", "\n").Split('\n').Length);

        // SamplerInfo unaffected by the annotation.
        result.Value.Samplers.ShouldHaveSingleItem();
        result.Value.Samplers[0].Name.ShouldBe("s");
        result.Value.Samplers[0].TextureReference.ShouldBe("t");
    }

    // -------------------------------------------------------------------------
    // (m) Phase 45 B4 — a legacy 'texture T < annotation >;' passes through to vkd3d
    //     with the legacy type intact, but the FX annotation block (which vkd3d does
    //     not accept) is stripped by the generic global-annotation strip. The inner
    //     ';' inside the annotation must not truncate the strip early.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_B4_LegacyTextureAnnotation_TypeKeptAnnotationStripped()
    {
        const string source =
            "texture Tex < string Name = \"diffuse\"; >;\n" +              // line 1
            "sampler s = sampler_state { Texture = <Tex>; };\n" +          // line 2
            "\n" +                                                          // line 3
            "float4 PS(float2 uv : TEXCOORD0) : COLOR { return tex2D(s, uv); }\n"; // line 4

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The legacy 'texture' type passes through verbatim (vkd3d accepts it)...
        stripped.ShouldContain("texture Tex", Case.Sensitive);
        // ...but the FX annotation block is stripped, with no leaked contents. (The
        // sampler_state's 'Texture = <Tex>;' binding legitimately keeps its angle
        // brackets, so we check the annotation contents specifically, not all '>'.)
        stripped.ShouldNotContain("Name", Case.Sensitive);
        stripped.ShouldNotContain("\"diffuse\"", Case.Sensitive);
        stripped.ShouldNotContain("string Name", Case.Sensitive);
        // The texture-binding angle brackets survive; the annotation's do not.
        stripped.ShouldContain("Texture = <Tex>;", Case.Sensitive);
        // tex2D survives verbatim on the FNA path.
        stripped.ShouldContain("tex2D(s, uv)", Case.Sensitive);

        // Line count preserved (the annotation erasure keeps newlines).
        var lines = stripped.Replace("\r\n", "\n").Split('\n');
        lines.Length.ShouldBe(source.Replace("\r\n", "\n").Split('\n').Length);
    }

    // -------------------------------------------------------------------------
    // (n) Phase 45 B7 — an array-indexed relational with a ternary-arm assignment in
    //     a function body survives verbatim (no annotation misparse) on the FNA path
    //     too. The brace-depth gate is mode-independent.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreserveSm3_B7_ArrayIndexedRelationalTernaryAssign_SurvivesVerbatim()
    {
        const string source = """
            float arr[4];

            float4 PS() : COLOR
            {
                int i = 0;
                float y = 1, z = 2, w = 3, q = 4;
                float r = arr[i] < y ? z = w : q;
                return float4(r, r, r, 1);
            }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("float r = arr[i] < y ? z = w : q;", Case.Sensitive);
        result.Value.ParameterAnnotations.ShouldBeEmpty();
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
