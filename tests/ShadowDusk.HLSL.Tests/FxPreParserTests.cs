#nullable enable
using System.Linq;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

public sealed class FxPreParserTests
{
    // -------------------------------------------------------------------------
    // T01 — empty source
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_EmptySource_ReturnsEmptyResult()
    {
        var result = FxPreParser.Parse("", sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques.ShouldBeEmpty();
        result.Value.Samplers.ShouldBeEmpty();
        result.Value.StrippedHlsl.ShouldBe("");
    }

    // -------------------------------------------------------------------------
    // Bug-hunt 2026-07-27 M14 — fxc-parity shapes
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_VertexShaderNull_IsTreatedAsAbsentStage()
    {
        // fxc parity: `VertexShader = NULL;` is the D3D9 idiom for "no shader bound
        // to this stage in this pass"; it used to fail FX0001 ("Expected 'compile'").
        const string source = """
            technique T
            {
                pass P
                {
                    VertexShader = NULL;
                    PixelShader  = compile ps_3_0 PSMain();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var pass = result.Value.Techniques[0].Passes[0];
        pass.VertexEntryPoint.ShouldBeNull();
        pass.PixelEntryPoint.ShouldBe("PSMain");
    }

    [Fact]
    public void Parse_SamplerTextureNull_LeavesTextureReferenceUnset()
    {
        // fxc parity: `Texture = NULL;` means "the app binds the texture at runtime".
        // The literal identifier used to become the reference, producing
        // `NULL.Sample(...)` in the rewritten HLSL — an undeclared-identifier DXC error.
        const string source = """
            sampler2D DiffuseSampler = sampler_state
            {
                Texture = NULL;
                MinFilter = Linear;
            };

            technique T { pass P { PixelShader = compile ps_3_0 PSMain(); } }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Samplers.ShouldHaveSingleItem().TextureReference.ShouldBeNull();
    }

    [Fact]
    public void Parse_Technique10_IsRecognized()
    {
        // fxc accepts technique10 (fx_4_0); the block used to pass through unrecognized
        // and leak its pass body into the DXC input.
        const string source = """
            technique10 Render
            {
                pass P0
                {
                    VertexShader = compile vs_4_0 VSMain();
                    PixelShader  = compile ps_4_0 PSMain();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques.ShouldHaveSingleItem().Name.ShouldBe("Render");
        result.Value.Techniques[0].IsEffect11.ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // T02 — single technique, one pass — Snippet A
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_SingleTechniqueOnePass_ExtractsTechniqueAndPass()
    {
        const string source = """
            technique MyTechnique
            {
                pass Pass1
                {
                    VertexShader = compile vs_3_0 VSMain();
                    PixelShader  = compile ps_3_0 PSMain();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();

        var techniques = result.Value.Techniques;
        techniques.Count().ShouldBe(1);

        var tech = techniques[0];
        tech.Name.ShouldBe("MyTechnique");
        tech.Passes.Count().ShouldBe(1);

        var pass = tech.Passes[0];
        pass.Name.ShouldBe("Pass1");
        pass.VertexEntryPoint.ShouldBe("VSMain");
        pass.VertexProfile.ShouldBe("vs_3_0");
        pass.PixelEntryPoint.ShouldBe("PSMain");
        pass.PixelProfile.ShouldBe("ps_3_0");
    }

    // -------------------------------------------------------------------------
    // T03 — multi-pass technique — Snippet B
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MultiPassTechnique_AllPassesExtracted()
    {
        const string source = """
            technique Multi
            {
                pass A { VertexShader = compile vs_3_0 VS1(); PixelShader = compile ps_3_0 PS1(); }
                pass B { VertexShader = compile vs_3_0 VS2(); PixelShader = compile ps_3_0 PS2(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques.Count().ShouldBe(1);

        var passes = result.Value.Techniques[0].Passes;
        passes.Count().ShouldBe(2);
        passes[0].Name.ShouldBe("A");
        passes[1].Name.ShouldBe("B");
    }

    // -------------------------------------------------------------------------
    // T04 — two technique blocks
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MultiTechnique_AllExtracted()
    {
        const string source = """
            technique TechOne
            {
                pass P1 { VertexShader = compile vs_3_0 VS1(); PixelShader = compile ps_3_0 PS1(); }
            }
            technique TechTwo
            {
                pass P1 { VertexShader = compile vs_3_0 VS2(); PixelShader = compile ps_3_0 PS2(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques.Count().ShouldBe(2);
        result.Value.Techniques[0].Name.ShouldBe("TechOne");
        result.Value.Techniques[1].Name.ShouldBe("TechTwo");
    }

    // -------------------------------------------------------------------------
    // T05 — render states extracted per pass
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_RenderStates_ExtractedPerPass()
    {
        const string source = """
            technique T
            {
                pass P1
                {
                    CullMode        = None;
                    AlphaBlendEnable = True;
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();

        var renderStates = result.Value.Techniques[0].Passes[0].RenderStates;
        renderStates.Count().ShouldBe(2);
        renderStates.ShouldContain(rs => rs.Key == "CullMode" && rs.Value == "None");
        renderStates.ShouldContain(rs => rs.Key == "AlphaBlendEnable" && rs.Value == "True");
    }

    // -------------------------------------------------------------------------
    // T06 — sampler state block — Snippet C
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_SamplerState_Extracted()
    {
        const string source = """
            sampler2D MySampler = sampler_state {
                Texture   = <MyTexture>;
                MinFilter = Linear;
                MagFilter = Linear;
                AddressU  = Wrap;
            };
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Samplers.Count().ShouldBe(1);

        var sampler = result.Value.Samplers[0];
        sampler.Name.ShouldBe("MySampler");
        sampler.SamplerType.ShouldBe("sampler2D");
        sampler.TextureReference.ShouldBe("MyTexture");
    }

    // -------------------------------------------------------------------------
    // T07 — annotations extracted on technique
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_Annotations_ExtractedOnTechnique()
    {
        const string source = """
            technique T < string UIName = "X"; >
            {
                pass P1 { }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();

        var tech = result.Value.Techniques[0];
        tech.Annotations.Count().ShouldBe(1);

        var annotation = tech.Annotations[0];
        annotation.Name.ShouldBe("UIName");
        annotation.Type.ShouldBe("string");
        annotation.Value.ShouldBe("\"X\"");
    }

    // -------------------------------------------------------------------------
    // T08 — global parameter annotation extracted and stripped
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_GlobalParameterAnnotation_ExtractedAndStripped()
    {
        const string source = "float P < float UIMin = 0; > = 0.5;";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ParameterAnnotations.Count().ShouldBe(1);

        var pa = result.Value.ParameterAnnotations[0];
        pa.ParameterName.ShouldBe("P");
        pa.Entries.Count().ShouldBe(1);
        pa.Entries[0].Name.ShouldBe("UIMin");

        // The annotation block (angle brackets and contents) must be stripped
        // so DXC never sees it; the assignment "= 0.5;" must survive.
        result.Value.StrippedHlsl.ShouldNotContain("<", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("= 0.5", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // T09 — stripped output preserves line numbers
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_StrippedOutputPreservesLineNumbers()
    {
        // Line 1: HLSL declaration that must remain
        // Line 2: blank
        // Lines 3-8: technique block that must be stripped
        const string source =
            "float4 MyColor;\n" +
            "\n" +
            "technique T\n" +
            "{\n" +
            "    pass P1 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); }\n" +
            "}\n";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();

        var stripped = result.Value.StrippedHlsl;
        var lines = stripped.Split('\n');

        // The HLSL declaration must remain on line 1 (index 0)
        lines[0].ShouldContain("MyColor", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // T10 — missing closing brace → FX0002 UnexpectedEof
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MissingClosingBrace_ReturnsFX0002()
    {
        // Technique block opened but never closed
        const string source = """
            technique T
            {
                pass P1
                {
                    VertexShader = compile vs_3_0 VS();
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.UnexpectedEof);
    }

    // -------------------------------------------------------------------------
    // T11 — malformed compile expression → FX0003
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MalformedCompile_ReturnsFX0003()
    {
        const string source = """
            technique T
            {
                pass P1
                {
                    VertexShader = compile ;
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.MalformedCompileExpression);
    }

    // -------------------------------------------------------------------------
    // T12 — unrecognized shader profile: stored raw, not a pre-parse failure
    // Per spec: "Store the raw string in the PassInfo regardless; fail compilation
    // later at the DXC invocation stage." Pre-parser must NOT fail here.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_UnrecognizedProfile_StoredRawNotFailed()
    {
        const string source = """
            technique T
            {
                pass P1
                {
                    VertexShader = compile vs_99_0 VSMain();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques[0].Passes[0].VertexProfile.ShouldBe("vs_99_0");
    }

    // -------------------------------------------------------------------------
    // T13 — duplicate technique name → FX0005
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_DuplicateTechniqueName_ReturnsFX0005()
    {
        const string source = """
            technique Foo
            {
                pass P1 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); }
            }
            technique Foo
            {
                pass P1 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.DuplicateTechniqueName);
    }

    // -------------------------------------------------------------------------
    // T14 — duplicate pass name inside one technique → FX0006
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_DuplicatePassName_ReturnsFX0006()
    {
        const string source = """
            technique T
            {
                pass Pass1 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); }
                pass Pass1 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.DuplicatePassName);
    }

    // -------------------------------------------------------------------------
    // T15 — unclosed annotation block → FX0007
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_UnclosedAnnotation_ReturnsFX0007()
    {
        // The closing '>' for the annotation block is intentionally missing
        const string source = """
            technique T < string UIName = "X"
            {
                pass P1 { }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.UnclosedAnnotationBlock);
    }

    // -------------------------------------------------------------------------
    // T16 — missing semicolon in render state → FX0008
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MissingSemicolon_ReturnsFX0008()
    {
        // CullMode line has no trailing semicolon
        const string source = """
            technique T
            {
                pass P1
                {
                    CullMode = None
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.MissingSemicolon);
    }

    // -------------------------------------------------------------------------
    // T17 — preprocessor directives preserved verbatim in stripped output
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PreprocessorDirectivesPreserved()
    {
        const string source =
            "#if SM4\n" +
            "// some code\n" +
            "#endif\n" +
            "technique T\n" +
            "{\n" +
            "    pass P1 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); }\n" +
            "}\n";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("#if SM4", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("#endif", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // T18 — line comment inside pass body does not set entry points
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_LineCommentInsidePass_DoesNotConfuseParser()
    {
        const string source = """
            technique T
            {
                pass P1
                {
                    // VertexShader = compile vs_3_0 CommentedOut();
                    PixelShader = compile ps_3_0 PSMain();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();

        var pass = result.Value.Techniques[0].Passes[0];
        // The commented-out VS line must NOT be parsed as a real entry-point
        pass.VertexEntryPoint.ShouldBeNull();
        pass.PixelEntryPoint.ShouldBe("PSMain");
    }

    // -------------------------------------------------------------------------
    // T19 — uppercase profile identifier is normalized to lowercase
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ShaderProfileCasing_Accepted()
    {
        const string source = """
            technique T
            {
                pass P1
                {
                    VertexShader = compile VS_3_0 VSMain();
                    PixelShader  = compile PS_3_0 PSMain();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();

        var pass = result.Value.Techniques[0].Passes[0];
        pass.VertexProfile.ShouldBe("vs_3_0");
        pass.PixelProfile.ShouldBe("ps_3_0");
    }

    // -------------------------------------------------------------------------
    // T20 — 32 technique declarations all parsed successfully (stress / BasicEffect pattern)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_BasicEffectLikePattern_32Techniques_Succeeds()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 32; i++)
        {
            sb.AppendLine($"technique Tech{i}");
            sb.AppendLine("{");
            sb.AppendLine($"    pass P1 {{ VertexShader = compile vs_3_0 VS{i}(); PixelShader = compile ps_3_0 PS{i}(); }}");
            sb.AppendLine("}");
        }

        var result = FxPreParser.Parse(sb.ToString(), sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques.Count().ShouldBe(32);
    }

    // -------------------------------------------------------------------------
    // T23 — sampler Texture = <MyTex>; (angle-bracket form)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_SamplerTextureAngleBracket_Extracted()
    {
        const string source = """
            sampler2D MySampler = sampler_state {
                Texture = <MyTex>;
            };
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Samplers.Count().ShouldBe(1);
        result.Value.Samplers[0].TextureReference.ShouldBe("MyTex");
    }

    // -------------------------------------------------------------------------
    // T24 — sampler Texture = MyTex; (bare identifier form)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_SamplerTextureBareIdentifier_Extracted()
    {
        const string source = """
            sampler2D MySampler = sampler_state {
                Texture = MyTex;
            };
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Samplers.Count().ShouldBe(1);
        result.Value.Samplers[0].TextureReference.ShouldBe("MyTex");
    }

    // -------------------------------------------------------------------------
    // T25 — parse error carries a valid (> 0) line and column
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ErrorContainsLineAndColumn()
    {
        // Three lines of valid-looking preamble so the bad token is not on line 1;
        // the unclosed technique forces an UnexpectedEof with a meaningful position.
        const string source =
            "float4 MyColor;\n" +
            "float4 MyOther;\n" +
            "technique T\n" +
            "{\n" +
            "    pass P1\n" +
            "    {\n";
        // No closing braces — parser reaches EOF inside nested blocks.

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Line.ShouldBeGreaterThan(0);
        result.Error.Column.ShouldBeGreaterThan(0);
        result.Error.SourceFile.ShouldBe("test.fx");
        result.Error.Message.ShouldNotBeNullOrEmpty();
    }

    // -------------------------------------------------------------------------
    // Color return-semantic rewrite — DXC ps_6_0 rejects ': COLOR' so the pre-parser
    // must rewrite it to ': SV_Target' (with the digit suffix preserved). Struct-field
    // input semantics must NOT be rewritten because they remain valid HLSL identifiers
    // for DXC.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_FunctionReturnSemantic_ColorWithoutDigit_RewrittenToSvTarget()
    {
        const string source = """
            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return float4(1, 0, 0, 1);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain(": SV_Target", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain(": COLOR", Case.Sensitive);
    }

    [Fact]
    public void Parse_FunctionReturnSemantic_Color3_DigitPreservedAsSvTarget3()
    {
        const string source = """
            float4 PS(float2 uv : TEXCOORD0) : COLOR3
            {
                return float4(0, 1, 0, 1);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain(": SV_Target3", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("COLOR3", Case.Sensitive);
    }

    [Fact]
    public void Parse_StructFieldInputSemantic_ColorPreserved_NotRewritten()
    {
        // Both forms must coexist correctly: the struct field 'COLOR0' is an
        // input semantic and stays as-is for DXC; the function return 'COLOR0'
        // is the SM 3.0 output semantic and must be rewritten.
        const string source = """
            struct VsOut
            {
                float4 Position : SV_POSITION;
                float4 Color    : COLOR0;
                float2 TexCoord : TEXCOORD0;
            };

            float4 PS(VsOut input) : COLOR0
            {
                return input.Color;
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();

        // The struct member's ': COLOR0;' must survive verbatim.
        result.Value.StrippedHlsl.ShouldContain("float4 Color    : COLOR0;", Case.Sensitive);

        // The function return ': COLOR0' must be rewritten to ': SV_Target0'.
        result.Value.StrippedHlsl.ShouldContain(": SV_Target0", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("input) : COLOR0", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Sampler-declaration rewriting (gap #2) + tex2D rewriting (gap #4)
    //
    // DXC 6.x rejects the legacy 'sampler2D X = sampler_state {...}' declaration
    // form and the 'tex2D' intrinsic. The pre-parser rewrites a declaration into
    // the modern 'Texture2D' + 'SamplerState' pair and 'tex2D(s, uv)' into
    // '<texture>.Sample(s, uv)' — but only for samplers a tex2D call references,
    // so already-modern shaders are untouched.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_SamplerStateForm_UsedByTex2D_RewrittenToSamplerStateAndSample()
    {
        // Form 1: sampler2D bound to an explicitly-declared Texture2D.
        const string source = """
            Texture2D SpriteTexture;

            sampler2D SpriteTextureSampler = sampler_state
            {
                Texture = <SpriteTexture>;
            };

            float4 MainPS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(SpriteTextureSampler, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // Declaration rewritten: legacy form gone, modern SamplerState left behind.
        stripped.ShouldContain("SamplerState SpriteTextureSampler;", Case.Sensitive);
        stripped.ShouldNotContain("sampler_state", Case.Sensitive);
        stripped.ShouldNotContain("sampler2D", Case.Sensitive);

        // No synthesized texture — the sampler_state bound an existing Texture2D.
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);

        // tex2D rewritten to a Sample call on the bound texture; args preserved.
        stripped.ShouldContain("SpriteTexture.Sample(SpriteTextureSampler, uv)", Case.Sensitive);
        stripped.ShouldNotContain("tex2D", Case.Sensitive);

        // Metadata still extracted as before.
        result.Value.Samplers.ShouldHaveSingleItem();
        result.Value.Samplers[0].TextureReference.ShouldBe("SpriteTexture");
    }

    [Fact]
    public void Parse_BareSampler_UsedByTex2D_SynthesizesTextureAndRewritesSample()
    {
        // Form 2: bare 'sampler s0;' with no associated texture in source.
        const string source = """
            sampler s0;

            float4 PixelShaderFunction(float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(s0, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // A Texture2D is synthesized and paired with a modern SamplerState.
        stripped.ShouldContain("Texture2D s0_SDTexture;", Case.Sensitive);
        stripped.ShouldContain("SamplerState s0;", Case.Sensitive);

        // tex2D rewritten to sample the synthesized texture.
        stripped.ShouldContain("s0_SDTexture.Sample(s0, uv)", Case.Sensitive);
        stripped.ShouldNotContain("tex2D", Case.Sensitive);
    }

    [Fact]
    public void Parse_BareSamplerWithRegister_UsedByTex2D_SynthesizesTextureAndRewritesSample()
    {
        // Form 3: bare sampler with an explicit register binding (':' is dropped
        // by the lexer, so at the token level this is 'sampler X register ( s0 ) ;').
        const string source = """
            sampler TextureSampler : register(s0);

            float4 BloomPass(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(TextureSampler, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("Texture2D TextureSampler_SDTexture;", Case.Sensitive);
        stripped.ShouldContain("SamplerState TextureSampler;", Case.Sensitive);
        stripped.ShouldContain("TextureSampler_SDTexture.Sample(TextureSampler, uv)", Case.Sensitive);
        stripped.ShouldNotContain("tex2D", Case.Sensitive);
        stripped.ShouldNotContain("register", Case.Sensitive);
    }

    [Fact]
    public void Parse_SamplerRewrite_PreservesLineNumbers()
    {
        // The multi-line sampler_state block (lines 3-6) must collapse to a single
        // declaration on its first line while keeping the source's total line count,
        // so the MainPS body stays on its original line for DXC diagnostics.
        const string source =
            "Texture2D SpriteTexture;\n" +              // line 1
            "\n" +                                       // line 2
            "sampler2D SpriteTextureSampler = sampler_state\n" + // line 3
            "{\n" +                                      // line 4
            "    Texture = <SpriteTexture>;\n" +         // line 5
            "};\n" +                                     // line 6
            "\n" +                                       // line 7
            "float4 MainPS(float2 uv : TEXCOORD0) : COLOR\n" + // line 8
            "{\n" +                                      // line 9
            "    return tex2D(SpriteTextureSampler, uv);\n" +  // line 10
            "}\n";                                        // line 11

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var lines = result.Value.StrippedHlsl.Replace("\r\n", "\n").Split('\n');

        // Same number of lines as the source.
        lines.Length.ShouldBe(source.Replace("\r\n", "\n").Split('\n').Length);

        // The rewritten declaration sits on the original first line (line 3 → index 2).
        lines[2].ShouldContain("SamplerState SpriteTextureSampler;", Case.Sensitive);

        // The MainPS signature and body stay on their original lines.
        lines[7].ShouldContain("float4 MainPS", Case.Sensitive);
        lines[9].ShouldContain(".Sample(SpriteTextureSampler, uv)", Case.Sensitive);
    }

    [Fact]
    public void Parse_UnusedSamplerStateForm_StillErased_NotRewritten()
    {
        // Regression guard: a Form 1 sampler never referenced by tex2D keeps the
        // pre-existing behavior (erased entirely, not turned into a SamplerState).
        const string source = """
            sampler2D UnusedSampler = sampler_state
            {
                Texture = <SomeTexture>;
            };

            float4 PS() : COLOR { return float4(1, 1, 1, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldNotContain("sampler_state", Case.Sensitive);
        stripped.ShouldNotContain("SamplerState UnusedSampler;", Case.Sensitive);
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);

        // Metadata is still extracted regardless of rewriting.
        result.Value.Samplers.ShouldHaveSingleItem();
        result.Value.Samplers[0].Name.ShouldBe("UnusedSampler");
    }

    [Fact]
    public void Parse_UnusedBareSampler_PassedThroughVerbatim()
    {
        // Regression guard: a bare sampler no tex2D references is left untouched.
        const string source = """
            sampler unusedS;

            float4 PS() : COLOR { return float4(0, 0, 0, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("sampler unusedS;", Case.Sensitive);
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);
    }

    [Fact]
    public void Parse_ModernSamplerStateAndSample_LeftUntouched()
    {
        // Regression guard: a shader already using the modern Texture2D +
        // SamplerState + .Sample() pattern must pass through unchanged — no
        // synthesized texture, no declaration rewrite.
        const string source = """
            Texture2D Texture;
            SamplerState TextureSampler;

            float4 PS(float2 uv : TEXCOORD0) : SV_TARGET
            {
                return Texture.Sample(TextureSampler, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();

        // Byte-identical: nothing in this source matches any rewrite rule.
        result.Value.StrippedHlsl.ShouldBe(source);
        result.Value.Samplers.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_MultipleSamplers_EachBoundToItsOwnTexture()
    {
        // A mix of a textured Form 1 sampler and a bare sampler, both used by tex2D.
        // Each tex2D call must resolve to the correct per-sampler texture.
        const string source = """
            Texture2D _secondTexture;
            sampler2D _secondTextureSampler = sampler_state { Texture = <_secondTexture>; };
            sampler s0;

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                float4 a = tex2D(s0, uv);
                float4 b = tex2D(_secondTextureSampler, uv);
                return a * b;
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("Texture2D s0_SDTexture;", Case.Sensitive);
        stripped.ShouldContain("SamplerState s0;", Case.Sensitive);
        stripped.ShouldContain("SamplerState _secondTextureSampler;", Case.Sensitive);

        stripped.ShouldContain("s0_SDTexture.Sample(s0, uv)", Case.Sensitive);
        stripped.ShouldContain("_secondTexture.Sample(_secondTextureSampler, uv)", Case.Sensitive);
        stripped.ShouldNotContain("tex2D", Case.Sensitive);
    }

    [Fact]
    public void Parse_Tex2DInsideComment_NotTreatedAsIntrinsic()
    {
        // A bare sampler mentioned only inside a comment's "tex2D(...)" text must
        // NOT be rewritten — the lexer emits the comment as one token, so the scan
        // never sees an intrinsic, and the sampler stays a pass-through bare decl.
        const string source = """
            sampler s0;

            // historical note: this used to call tex2D(s0, uv) directly
            float4 PS() : COLOR { return float4(1, 0, 0, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("sampler s0;", Case.Sensitive);
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Legacy effect-framework 'texture' object declarations (gap #3 / Dissolve).
    // DXC rejects the FX 'texture T;' type under -Weffects-syntax; the pre-parser
    // rewrites it to the modern 'Texture2D T;' so the resource a sampler_state
    // form references actually exists. Modern 'Texture2D'/'Texture3D'/… are
    // matched case-sensitively and never rewritten.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_LegacyTextureDecl_RewrittenToTexture2D()
    {
        const string source = """
            texture _dissolveTex;

            float4 PS() : COLOR { return float4(1, 1, 1, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("Texture2D _dissolveTex;", Case.Sensitive);
        // The bare legacy 'texture ' keyword must be gone (Texture2D is fine).
        stripped.ShouldNotContain("texture _dissolveTex", Case.Sensitive);
    }

    [Fact]
    public void Parse_LegacyTextureBoundToSamplerState_BothRewritten()
    {
        // The Dissolve pattern: a legacy 'texture' bound through a sampler_state
        // form and sampled with tex2D. The texture becomes a Texture2D, the
        // sampler a SamplerState, and tex2D a Sample call on the bound texture.
        const string source = """
            texture _dissolveTex;
            sampler _dissolveTexSampler = sampler_state { Texture = <_dissolveTex>; };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(_dissolveTexSampler, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("Texture2D _dissolveTex;", Case.Sensitive);
        stripped.ShouldContain("SamplerState _dissolveTexSampler;", Case.Sensitive);
        stripped.ShouldContain("_dissolveTex.Sample(_dissolveTexSampler, uv)", Case.Sensitive);
        stripped.ShouldNotContain("sampler_state", Case.Sensitive);
        stripped.ShouldNotContain("tex2D", Case.Sensitive);
        // No synthesized texture — the sampler_state bound the explicit texture.
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);
    }

    [Fact]
    public void Parse_LegacyTextureWithAnnotation_AnnotationDropped()
    {
        // FX annotations on a texture have no modern equivalent and must be
        // dropped, leaving a clean 'Texture2D T;'.
        const string source = """
            texture Diffuse < string ResourceName = "wall.png"; >;

            float4 PS() : COLOR { return float4(1, 1, 1, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("Texture2D Diffuse;", Case.Sensitive);
        stripped.ShouldNotContain("<", Case.Sensitive);
        stripped.ShouldNotContain("ResourceName", Case.Sensitive);
    }

    [Fact]
    public void Parse_LegacyTextureDecl_PreservesLineNumbers()
    {
        // The single-line rewrite must not change the source's total line count
        // so DXC diagnostics on later lines still point at the right line.
        const string source =
            "texture _dissolveTex;\n" +                  // line 1
            "\n" +                                        // line 2
            "float4 PS() : COLOR { return 0; }\n";        // line 3

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var lines = result.Value.StrippedHlsl.Replace("\r\n", "\n").Split('\n');

        lines.Length.ShouldBe(source.Replace("\r\n", "\n").Split('\n').Length);
        lines[0].ShouldContain("Texture2D _dissolveTex;", Case.Sensitive);
        lines[2].ShouldContain("float4 PS", Case.Sensitive);
    }

    [Fact]
    public void Parse_ModernTexture2DDecl_LeftUntouched()
    {
        // Regression guard: modern 'Texture2D' (capital T, dimension suffix) must
        // never be rewritten — case-sensitive matching distinguishes it from the
        // legacy lowercase 'texture'/'texture2D' forms.
        const string source = """
            Texture2D Diffuse;
            Texture3D Volume;
            TextureCube Sky;

            float4 PS() : SV_TARGET { return float4(0, 0, 0, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldBe(source);
    }

    // -------------------------------------------------------------------------
    // Brace-form sampler declarations (finding F1) — fxc treats
    // 'sampler S { ... };' exactly like 'sampler S = sampler_state { ... };',
    // so the default mode gives it the same SM4 rewrite treatment. The paren
    // texture reference 'Texture = (X);' is legacy XNA syntax fxc also accepts.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_SamplerTextureParenForm_Extracted()
    {
        // 'Texture = (MyTex);' — sibling of T23 (<MyTex>) and T24 (bare MyTex).
        const string source = """
            sampler2D MySampler = sampler_state {
                Texture = (MyTex);
            };
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Samplers.Count().ShouldBe(1);
        result.Value.Samplers[0].TextureReference.ShouldBe("MyTex");
    }

    [Fact]
    public void Parse_BraceFormSampler_UsedByTex2D_RewrittenLikeKeywordForm()
    {
        // The ClipShaderNew.fx pattern: brace form (no '= sampler_state') with a
        // paren texture reference, bound to an explicitly-declared Texture2D.
        const string source = """
            Texture2D Mask;
            sampler MaskSampler
            {
                Texture = (Mask);
                MinFilter = LINEAR;
            };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(MaskSampler, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // Same rewrite the keyword form gets: modern SamplerState, legacy block gone.
        stripped.ShouldContain("SamplerState MaskSampler;", Case.Sensitive);
        stripped.ShouldNotContain("MinFilter", Case.Sensitive);

        // No synthesized texture — the block bound the existing Texture2D, and
        // tex2D resolves to a Sample call on it.
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);
        stripped.ShouldContain("Mask.Sample(MaskSampler, uv)", Case.Sensitive);
        stripped.ShouldNotContain("tex2D", Case.Sensitive);

        // Metadata capture is identical to the keyword form.
        result.Value.Samplers.ShouldHaveSingleItem();
        result.Value.Samplers[0].Name.ShouldBe("MaskSampler");
        result.Value.Samplers[0].TextureReference.ShouldBe("Mask");
        result.Value.Samplers[0].StateEntries.Where(
            e => e.Key == "MinFilter" && e.Value == "LINEAR").ShouldHaveSingleItem();
    }

    [Fact]
    public void Parse_BraceFormSamplerWithRegister_UsedByTex2D_RewrittenLikeKeywordForm()
    {
        // Brace form with a register clause between the name and the '{' (the ':'
        // is dropped by the lexer). Must NOT be mistaken for the bare Form 3,
        // whose consumer would swallow only up to the first ';' inside the block.
        const string source = """
            texture t;
            sampler s : register(s0)
            {
                Texture = <t>;
                AddressU = CLAMP;
            };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(s, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("Texture2D t;", Case.Sensitive);
        stripped.ShouldContain("SamplerState s;", Case.Sensitive);
        stripped.ShouldContain("t.Sample(s, uv)", Case.Sensitive);
        stripped.ShouldNotContain("tex2D", Case.Sensitive);
        stripped.ShouldNotContain("register", Case.Sensitive);
        stripped.ShouldNotContain("AddressU", Case.Sensitive);

        result.Value.Samplers.ShouldHaveSingleItem();
        result.Value.Samplers[0].TextureReference.ShouldBe("t");
    }

    [Fact]
    public void Parse_UnusedBraceFormSampler_ErasedLikeKeywordForm()
    {
        // A brace-form sampler no tex2D references gets the keyword form's
        // pre-existing unused handling: erased entirely, metadata still captured.
        const string source = """
            sampler2D UnusedSampler
            {
                Texture = <SomeTexture>;
            };

            float4 PS() : COLOR { return float4(1, 1, 1, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldNotContain("UnusedSampler", Case.Sensitive);
        stripped.ShouldNotContain("SomeTexture", Case.Sensitive);
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);

        result.Value.Samplers.ShouldHaveSingleItem();
        result.Value.Samplers[0].Name.ShouldBe("UnusedSampler");
        result.Value.Samplers[0].TextureReference.ShouldBe("SomeTexture");
    }

    [Fact]
    public void Parse_NonSamplerBraces_NeverMatchBraceForm()
    {
        // False-positive guard: every other '{'-bearing construct the parser walks
        // (struct bodies, function bodies, sampler-typed parameters — including a
        // sampler parameter in last position, where ')' precedes the '{') must not
        // be parsed as a brace-form sampler declaration.
        const string source = """
            struct PixelInput
            {
                float4 Position : SV_Position0;
                float4 Color : COLOR0;
            };

            float4 WithSamplerParam(sampler2D s, float2 uv) : COLOR0
            {
                return float4(uv, 0, 1);
            }

            float4 LastParamSampler(float2 uv, sampler2D s) : COLOR0
            {
                return float4(uv, 0, 1);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();

        // No sampler declaration was (mis)captured…
        result.Value.Samplers.ShouldBeEmpty();

        // …and the bodies survive: struct fields intact, both sampler-typed
        // parameters untouched (only the function return ': COLOR0' is rewritten,
        // which is the pre-existing SV_Target treatment, not sampler handling).
        string stripped = result.Value.StrippedHlsl;
        stripped.ShouldContain("float4 Color : COLOR0;", Case.Sensitive);
        stripped.ShouldContain("(sampler2D s, float2 uv)", Case.Sensitive);
        stripped.ShouldContain("(float2 uv, sampler2D s)", Case.Sensitive);
        stripped.ShouldContain("return float4(uv, 0, 1);", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Negative numeric values — the lexer historically SWALLOWED '-' (no Minus
    // token), so 'DepthBias = -0.5;' was captured as '0.5': silently-wrong output.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_NegativeRenderStateValue_CapturesTheSign()
    {
        const string source = """
            technique T
            {
                pass P
                {
                    PixelShader = compile ps_3_0 PSMain();
                    DepthBias = -0.5;
                    SlopeScaleDepthBias = -2;
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var states = result.Value.Techniques[0].Passes[0].RenderStates;
        states.Where(s => s.Key == "DepthBias").ShouldHaveSingleItem().Value.ShouldBe("-0.5");
        states.Where(s => s.Key == "SlopeScaleDepthBias").ShouldHaveSingleItem().Value.ShouldBe("-2");
    }

    [Fact]
    public void Parse_NegativeSamplerStateValue_CapturesTheSign()
    {
        const string source = """
            sampler MySampler = sampler_state
            {
                Texture = <SomeTexture>;
                MipMapLodBias = -2;
            };
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var sampler = result.Value.Samplers.ShouldHaveSingleItem();
        sampler.StateEntries.Where(e => e.Key == "MipMapLodBias").ShouldHaveSingleItem().Value.ShouldBe("-2");
    }

    [Fact]
    public void Parse_NegativeAnnotationValue_CapturesTheSign()
    {
        const string source = """
            float Intensity < float UIMin = -1.0; > ;
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var annotation = result.Value.ParameterAnnotations.ShouldHaveSingleItem();
        annotation.Entries.Where(e => e.Name == "UIMin").ShouldHaveSingleItem().Value.ShouldBe("-1.0");
    }

    [Fact]
    public void Parse_MinusInFunctionBody_PassesThroughVerbatim()
    {
        const string source = """
            float4 PSMain() : SV_Target
            {
                float a = 1.0 - 0.25;
                return float4(-a, a - 1.0, 0, 1);
            }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("float a = 1.0 - 0.25;", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("return float4(-a, a - 1.0, 0, 1);", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Hex / exponent numeric literals (lexer ReadNumber upgrade)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_HexRenderStateValue_CapturedAsOneToken()
    {
        const string source = """
            technique T
            {
                pass P
                {
                    PixelShader = compile ps_3_0 PSMain();
                    BlendFactor = 0x80FF8080;
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques[0].Passes[0].RenderStates
            .Where(s => s.Key == "BlendFactor").ShouldHaveSingleItem().Value.ShouldBe("0x80FF8080");
    }

    [Fact]
    public void Parse_ExponentRenderStateValue_CapturedAsOneToken()
    {
        const string source = """
            technique T
            {
                pass P
                {
                    PixelShader = compile ps_3_0 PSMain();
                    DepthBias = 1e-4;
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques[0].Passes[0].RenderStates
            .Where(s => s.Key == "DepthBias").ShouldHaveSingleItem().Value.ShouldBe("1e-4");
    }

    // -------------------------------------------------------------------------
    // Unknown characters fail loudly (FX0011) instead of being silently skipped
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_UnknownCharacter_FailsWithFX0011()
    {
        const string source = """
            float x @ 1;
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.UnknownCharacter);
        result.Error.Message.ShouldContain("'@'", Case.Sensitive);
    }

    [Fact]
    public void Parse_HlslOperatorCharacters_DoNotTriggerUnknownCharacter()
    {
        // The operator characters the lexer deliberately tokenizes-through must keep
        // working in code bodies (the ':' drop is load-bearing for semantics).
        const string source = """
            float4 PSMain(float2 uv : TEXCOORD0) : SV_Target
            {
                int mask = (3 & 1) | (4 ^ 2);
                bool flag = !(mask % 2 == 1) ? true : false;
                float arr[2] = { 0.5, ~0 };
                return float4(uv, arr[0], flag ? 1.0 : 0.0);
            }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("int mask = (3 & 1) | (4 ^ 2);", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Issue #106 — a relational/shift/ternary operator in a function body must NOT be
    // mistaken for a global-parameter annotation block. The flat token scanner sees the
    // 'Identifier Identifier LAngle' shape ('return value <'), but a genuine annotation
    // block is gated on the 'Identifier Identifier Equals' (or empty '<>') start, so these
    // expressions fall through verbatim instead of failing with FX0001.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_TernaryReturnWithRelationalOperator_IssueExactSnippet_Compiles()
    {
        // The exact snippet from issue #106.
        const string source = """
            float TernaryReturn(float value)
            {
                return value <= 0.5f ? 0.0f : 1.0f;
            }
            float4 PSMain() : SV_Target { return TernaryReturn(0.25f).xxxx; }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "Shader.fx");

        result.IsSuccess.ShouldBeTrue();
        // The relational operator must survive verbatim so DXC/vkd3d see the original '<='.
        result.Value.StrippedHlsl.ShouldContain("value <= 0.5f", Case.Sensitive);
        result.Value.ParameterAnnotations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("return a < b;", "a < b")]
    [InlineData("return a <= b;", "a <= b")]
    [InlineData("return a > b;", "a > b")]
    [InlineData("return a >= b;", "a >= b")]
    [InlineData("return a < b ? 1.0f : 0.0f;", "a < b")]
    [InlineData("return a << b;", "a << b")]
    public void Parse_RelationalOrShiftOperatorInBody_NotTreatedAsAnnotation(
        string statement, string mustSurvive)
    {
        string source =
            "int Helper(int a, int b)\n" +
            "{\n" +
            $"    {statement}\n" +
            "}\n" +
            "float4 PSMain() : SV_Target { return (float)Helper(1, 2); }\n" +
            "technique T\n" +
            "{\n" +
            "    pass P { PixelShader = compile ps_3_0 PSMain(); }\n" +
            "}\n";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        // The operator must reach DXC/vkd3d unchanged (not consumed as an annotation).
        result.Value.StrippedHlsl.ShouldContain(mustSurvive, Case.Sensitive);
        result.Value.ParameterAnnotations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_GenuineGlobalAnnotation_StillStrippedAfterRelationalFix()
    {
        // Guard: the discriminator must NOT regress genuine annotation parsing.
        const string source = "float P < float UIMin = 0; > = 0.5;";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ParameterAnnotations.Count().ShouldBe(1);
        result.Value.ParameterAnnotations[0].ParameterName.ShouldBe("P");
        result.Value.ParameterAnnotations[0].Entries.ShouldHaveSingleItem().Name.ShouldBe("UIMin");

        // The '< ... >' block is stripped; the assignment survives.
        result.Value.StrippedHlsl.ShouldNotContain("<", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("= 0.5", Case.Sensitive);
    }

    [Fact]
    public void Parse_EmptyGlobalAnnotationBlock_StillStripped()
    {
        // An empty annotation block ('< >') is accepted by ParseAnnotationBlock, so the
        // discriminator's RAngle branch must keep it on the annotation path.
        const string source = "float P < > = 0.5;";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ParameterAnnotations.ShouldHaveSingleItem().ParameterName.ShouldBe("P");
        result.Value.ParameterAnnotations[0].Entries.ShouldBeEmpty();
        result.Value.StrippedHlsl.ShouldNotContain("<", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("= 0.5", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Issue #106 (broader coverage) — relational/shift/ternary operators in many
    // more syntactic contexts (if/for headers, initializers, chained/nested
    // comparisons, call operands, comment-interleaved operands) must all fall
    // through verbatim, never tripping the global-annotation heuristic. These
    // extend the 6-row [Theory] above with shapes that exercise the discriminator
    // and the comment-skipping NextCodeOffset path more thoroughly.
    // -------------------------------------------------------------------------

    [Theory]
    // relational inside an `if` condition
    [InlineData("if (a < b) { return a; }", "if (a < b)")]
    [InlineData("if (x >= y) { return x; }", "if (x >= y)")]
    // relational inside a `for` header
    [InlineData("for (int i = 0; i < n; i++) { a += i; } return a;", "i < n")]
    // relational in a local initializer / assignment producing a ternary
    [InlineData("float r = a < b ? 1.0f : 0.0f; return (int)r;", "a < b ? 1.0f : 0.0f")]
    // chained comparisons joined by &&
    [InlineData("return a < b && c > d;", "a < b && c > d")]
    // nested ternary with parenthesized comparisons
    [InlineData("return (a <= b) ? ((c > d) ? 1 : 2) : 3;", "(a <= b) ?")]
    // a comment interleaved BETWEEN the operands (proves the comment-skipping
    // NextCodeOffset in IsAnnotationBlockStart and the la/la2 lookahead loops)
    [InlineData("return a /*c*/ < /*c*/ b;", "a /*c*/ < /*c*/ b")]
    public void Parse_RelationalInBroaderContexts_NotTreatedAsAnnotation(
        string statement, string mustSurvive)
    {
        string source =
            "int Helper(int a, int b, int c, int d, int n, int x, int y)\n" +
            "{\n" +
            $"    {statement}\n" +
            "}\n" +
            "float4 PSMain() : SV_Target { return (float)Helper(1, 2, 3, 4, 5, 6, 7); }\n" +
            "technique T\n" +
            "{\n" +
            "    pass P { PixelShader = compile ps_3_0 PSMain(); }\n" +
            "}\n";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        // The operator/expression must reach DXC unchanged (not consumed as annotation).
        result.Value.StrippedHlsl.ShouldContain(mustSurvive, Case.Sensitive);
        result.Value.ParameterAnnotations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_RelationalWithFunctionCallOperand_NotTreatedAsAnnotation()
    {
        // The left operand is a function call: 'length(v) < radius' — the ')' before
        // '<' means this never even reaches the 'Identifier Identifier LAngle' shape,
        // so it falls through verbatim. Pin it so the call form can't regress.
        const string source = """
            float Helper(float3 v, float radius)
            {
                return length(v) < radius ? 1.0f : 0.0f;
            }
            float4 PSMain() : SV_Target { return Helper(float3(0,0,0), 1.0f).xxxx; }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("length(v) < radius ? 1.0f : 0.0f", Case.Sensitive);
        result.Value.ParameterAnnotations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_GreaterThanStandaloneInBody_NotTreatedAsAnnotation()
    {
        // The bug was keyed on '<' (LAngle). A '>' is RAngle, so the heuristic's
        // 'Peek(la2).Kind == LAngle' check never fires — prove the '>'/'>=' family
        // standalone in a body is fine too (the [Theory] above only had them inside
        // helpers with two int params; here they stand alone as the whole return).
        const string source = """
            bool Helper(float a)
            {
                return a > 0.5f;
            }
            float4 PSMain() : SV_Target { return Helper(0.25f) ? 1.0f.xxxx : 0.0f.xxxx; }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("return a > 0.5f;", Case.Sensitive);
        result.Value.ParameterAnnotations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_GlobalScopeRelationalInitializer_DoesNotTripHeuristic()
    {
        // A global-scope (non-body) relational initializer. Tokenizes to
        // '... Identifier("Flag") Equals Identifier("SomeDefine") LAngle Number',
        // so the token immediately before '<' is the initializer identifier preceded
        // by '=', NOT a second declaration identifier — the 'Identifier Identifier
        // LAngle' shape never matches and the '<' survives. No annotation captured.
        const string source = """
            static const bool Flag = SomeDefine < 4;
            float4 PSMain() : SV_Target { return Flag ? 1.0f.xxxx : 0.0f.xxxx; }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("SomeDefine < 4;", Case.Sensitive);
        result.Value.ParameterAnnotations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_GenericTextureTemplate_NotMistakenForAnnotation()
    {
        // 'Texture2D<float4> Tex;' is 'Identifier LAngle ...' — only ONE identifier
        // precedes the '<', so it never enters the 'Identifier Identifier LAngle'
        // heuristic at all. Pin this so a future change to the lookahead can't
        // regress a generic template into a (mis-stripped) annotation block.
        const string source = """
            Texture2D<float4> Tex;
            sampler2D S = sampler_state { Texture = (Tex); };
            float4 PSMain() : SV_Target { return Tex.Load(int3(0, 0, 0)); }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        // The generic template survives verbatim; nothing was captured as annotation.
        result.Value.StrippedHlsl.ShouldContain("Texture2D<float4> Tex;", Case.Sensitive);
        result.Value.ParameterAnnotations.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Issue #106 (genuine-annotation regression guards) — prove the new
    // IsAnnotationBlockStart discriminator did NOT over-reject real annotations:
    // a multi-entry block, a negative-valued block, and a real annotation that
    // coexists in the same file as a body relational operator.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MultiEntryGlobalAnnotation_StillStrippedAfterRelationalFix()
    {
        // Three entries — the discriminator only inspects the FIRST entry's
        // 'Identifier Identifier Equals' shape, so a multi-entry block must still
        // be recognized and fully captured, and the '<...>' stripped.
        const string source =
            "float P < string UIName = \"P\"; float UIMin = 0; float UIMax = 1; > = 0.5;";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var pa = result.Value.ParameterAnnotations.ShouldHaveSingleItem();
        pa.ParameterName.ShouldBe("P");
        pa.Entries.Select(e => e.Name).ShouldBe(new[] {"UIName", "UIMin", "UIMax"});

        // The whole '< ... >' block is stripped; only the assignment survives.
        result.Value.StrippedHlsl.ShouldNotContain("<", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("UIName", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("= 0.5", Case.Sensitive);
    }

    [Fact]
    public void Parse_NegativeValuedGlobalAnnotation_StillStrippedAfterRelationalFix()
    {
        // Mirror of the existing negative-value annotation test, but asserting the
        // strip survives the relational fix: the first entry is still
        // 'Identifier Identifier Equals' so the discriminator admits it.
        const string source = """
            float Intensity < float UIMin = -1.0; float UIMax = 2.0; > = 0.5;
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var pa = result.Value.ParameterAnnotations.ShouldHaveSingleItem();
        pa.ParameterName.ShouldBe("Intensity");
        pa.Entries.Where(e => e.Name == "UIMin").ShouldHaveSingleItem().Value.ShouldBe("-1.0");

        result.Value.StrippedHlsl.ShouldNotContain("<", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("= 0.5", Case.Sensitive);
    }

    [Fact]
    public void Parse_RealAnnotationAndBodyRelational_CoexistInOneFile()
    {
        // Prove a genuine annotation and a body relational operator coexist: the
        // annotation block is recognized and stripped, while the later '<=' in the
        // function body survives verbatim. (The annotation precedes the helper that
        // immediately follows it on the next line.)
        const string source = """
            float Threshold < float UIMin = 0; float UIMax = 1; > = 0.5;
            float Helper(float a)
            {
                return a <= Threshold ? 0.0f : 1.0f;
            }
            float4 PSMain() : SV_Target { return Helper(0.25f).xxxx; }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();

        // The genuine annotation was captured and its '<...>' stripped...
        var pa = result.Value.ParameterAnnotations.ShouldHaveSingleItem();
        pa.ParameterName.ShouldBe("Threshold");
        pa.Entries.Select(e => e.Name).ShouldBe(new[] {"UIMin", "UIMax"});

        // ...while the body's relational operator survived verbatim.
        string stripped = result.Value.StrippedHlsl;
        stripped.ShouldContain("return a <= Threshold ? 0.0f : 1.0f;", Case.Sensitive);
        // Only the annotation '<...>' was removed; the body '<=' remains, so exactly
        // one '<' (from '<=') is left in the output.
        stripped.ShouldContain("<=", Case.Sensitive);
        stripped.ShouldNotContain("UIMin", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Legacy sampling intrinsics: tex2Dgrad forwards (1:1 args); tex2Dlod and the
    // other restructuring variants fail loudly with FX0012 (naming the intrinsic)
    // instead of dying inside DXC with a misleading diagnostic.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_Tex2Dgrad_RewritesToSampleGrad()
    {
        const string source = """
            sampler TexSampler = sampler_state { Texture = <SpriteTexture>; };

            float4 PSMain(float2 uv : TEXCOORD0) : SV_Target
            {
                return tex2Dgrad(TexSampler, uv, ddx(uv), ddy(uv));
            }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("SpriteTexture.SampleGrad(TexSampler, uv, ddx(uv), ddy(uv))", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("tex2Dgrad", Case.Sensitive);
    }

    [Theory]
    [InlineData("tex2Dlod")]
    [InlineData("tex2Dproj")]
    [InlineData("tex2Dbias")]
    [InlineData("texCUBE")]
    [InlineData("tex3D")]
    public void Parse_UnsupportedLegacyIntrinsic_FailsWithFX0012_NamingTheIntrinsic(string intrinsic)
    {
        string source = $$"""
            sampler TexSampler = sampler_state { Texture = <SpriteTexture>; };

            float4 PSMain(float4 t : TEXCOORD0) : SV_Target
            {
                return {{intrinsic}}(TexSampler, t);
            }
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.UnsupportedLegacyIntrinsic);
        result.Error.Message.ShouldContain($"'{intrinsic}'", Case.Sensitive);
    }

    [Fact]
    public void Parse_IdentifierNamedLikeIntrinsic_ButNotACall_DoesNotFail()
    {
        // Only a CALL trips FX0012; a variable merely named tex2Dlod does not.
        const string source = """
            float tex2Dlod;
            technique T
            {
                pass P { PixelShader = compile ps_3_0 PSMain(); }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
    }

    // =========================================================================
    // Phase 45 — FX pre-parser robustness (dropped-operator bug class), B2/B8/B9.
    // (B3 is render-state-only; covered in the render-state region below.)
    // =========================================================================

    // -------------------------------------------------------------------------
    // B2 — a 'sampler S = sampler_state { … }' USED through the modern
    // 'T.Sample(S, uv)' method (not tex2D) must NOT be erased; the declaration is
    // rewritten to a passthrough 'SamplerState S;' so '.Sample(S, …)' resolves.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_B2_SamplerStateUsedByModernSample_RewrittenToPassthroughSamplerState()
    {
        const string source = """
            Texture2D SpriteTexture;
            sampler2D SpriteTextureSampler = sampler_state
            {
                Texture = <SpriteTexture>;
            };

            float4 PS(float2 uv : TEXCOORD0) : SV_TARGET
            {
                return SpriteTexture.Sample(SpriteTextureSampler, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The legacy initializer is gone, but the declaration survives as a
        // passthrough SamplerState (NOT erased) so the .Sample call resolves.
        stripped.ShouldContain("SamplerState SpriteTextureSampler;", Case.Sensitive);
        stripped.ShouldNotContain("sampler_state", Case.Sensitive);
        // No Texture2D is synthesized — the shader declares its own and uses it.
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);
        // The modern call is untouched (it is not a tex2D rewrite).
        stripped.ShouldContain("SpriteTexture.Sample(SpriteTextureSampler, uv)", Case.Sensitive);

        // Metadata is still captured.
        result.Value.Samplers.ShouldHaveSingleItem();
        result.Value.Samplers[0].Name.ShouldBe("SpriteTextureSampler");
    }

    [Fact]
    public void Parse_B2_SamplerStateBraceFormUsedByModernSample_RewrittenToPassthrough()
    {
        // The brace form ('SamplerState S { … }', no '= sampler_state') used via
        // a modern method must rewrite the same way.
        const string source = """
            Texture2D Tex;
            SamplerState S
            {
                Texture = <Tex>;
                Filter = MIN_MAG_MIP_LINEAR;
            };

            float4 PS(float2 uv : TEXCOORD0) : SV_TARGET
            {
                return Tex.SampleLevel(S, uv, 0);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("SamplerState S;", Case.Sensitive);
        stripped.ShouldNotContain("Filter", Case.Sensitive);
        stripped.ShouldNotContain("_SDTexture", Case.Sensitive);
        stripped.ShouldContain("Tex.SampleLevel(S, uv, 0)", Case.Sensitive);
    }

    [Fact]
    public void Parse_B2_UnusedSamplerState_StillErased_NotPassthrough()
    {
        // Regression guard: a sampler_state referenced by NEITHER tex2D nor a
        // modern .Sample call is genuinely unused and stays erased (pre-existing
        // behavior; DXC would drop a passthrough SamplerState as unused anyway).
        const string source = """
            Texture2D SpriteTexture;
            sampler2D UnusedSampler = sampler_state
            {
                Texture = <SpriteTexture>;
            };

            float4 PS() : SV_TARGET { return float4(1, 1, 1, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldNotContain("sampler_state", Case.Sensitive);
        stripped.ShouldNotContain("SamplerState UnusedSampler;", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // B8 — 'sampler S : register(s0) = sampler_state { … };' (register clause
    // BEFORE the '='): routes to ParseSamplerDecl, no leaked state block.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_B8_RegisterClauseBeforeSamplerState_RoutesToSamplerDecl()
    {
        const string source = """
            Texture2D SpriteTexture;
            sampler2D SpriteTextureSampler : register(s0) = sampler_state
            {
                Texture = <SpriteTexture>;
            };

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(SpriteTextureSampler, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // Sampler is tex2D-referenced, so the whole decl (register clause + state
        // block) is replaced by a SamplerState; nothing of the block leaks.
        stripped.ShouldContain("SamplerState SpriteTextureSampler;", Case.Sensitive);
        stripped.ShouldNotContain("sampler_state", Case.Sensitive);
        stripped.ShouldNotContain("register", Case.Sensitive);
        stripped.ShouldContain("SpriteTexture.Sample(SpriteTextureSampler, uv)", Case.Sensitive);

        // Metadata captured (including the texture binding).
        result.Value.Samplers.ShouldHaveSingleItem();
        result.Value.Samplers[0].Name.ShouldBe("SpriteTextureSampler");
        result.Value.Samplers[0].TextureReference.ShouldBe("SpriteTexture");
    }

    // -------------------------------------------------------------------------
    // B9 — sampler-level FX annotation after the state block:
    // 'sampler2D S = sampler_state { … } < string UIName = "x"; >;'.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_B9_TrailingSamplerAnnotation_ConsumedAndStripped()
    {
        const string source = """
            Texture2D SpriteTexture;
            sampler2D SpriteTextureSampler = sampler_state
            {
                Texture = <SpriteTexture>;
            } < string UIName = "Diffuse"; int UIOrder = 1; >;

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(SpriteTextureSampler, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The annotation block is metadata: it must not reach DXC.
        stripped.ShouldNotContain("UIName", Case.Sensitive);
        stripped.ShouldNotContain("UIOrder", Case.Sensitive);
        // The declaration still rewrites normally (tex2D-referenced).
        stripped.ShouldContain("SamplerState SpriteTextureSampler;", Case.Sensitive);
        stripped.ShouldContain("SpriteTexture.Sample(SpriteTextureSampler, uv)", Case.Sensitive);

        // SamplerInfo is unaffected by the annotation.
        result.Value.Samplers.ShouldHaveSingleItem();
        result.Value.Samplers[0].Name.ShouldBe("SpriteTextureSampler");
        result.Value.Samplers[0].TextureReference.ShouldBe("SpriteTexture");
    }

    // -------------------------------------------------------------------------
    // B3 — ColorWriteEnable flag masks. The lexer drops '|', so 'Red | Green |
    // Blue' arrives as adjacent identifiers; the pass parser must capture them
    // all (joined with '|') instead of stopping at the first and demanding ';'.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_B3_ColorWriteEnableOredMask_CapturesFullValue()
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

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var rs = result.Value.Techniques[0].Passes[0].RenderStates;
        rs.Where(e => e.Key == "ColorWriteEnable" && e.Value == "Red|Green|Blue").ShouldHaveSingleItem();
    }

    [Fact]
    public void Parse_B3_ColorWriteEnableSingleFlag_StillCaptured()
    {
        const string source = """
            technique T
            {
                pass P
                {
                    ColorWriteEnable = Red;
                    PixelShader = compile ps_3_0 PS();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var rs = result.Value.Techniques[0].Passes[0].RenderStates;
        rs.Where(e => e.Key == "ColorWriteEnable" && e.Value == "Red").ShouldHaveSingleItem();
    }

    [Fact]
    public void Parse_B3_NumberedColorWriteEnableOredMask_CapturesFullValue()
    {
        const string source = """
            technique T
            {
                pass P
                {
                    ColorWriteEnable1 = Red | Alpha;
                    PixelShader = compile ps_3_0 PS();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var rs = result.Value.Techniques[0].Passes[0].RenderStates;
        rs.Where(e => e.Key == "ColorWriteEnable1" && e.Value == "Red|Alpha").ShouldHaveSingleItem();
    }

    [Fact]
    public void Parse_B3_OrdinaryRenderState_UnaffectedBySingleTokenPath()
    {
        // A non-mask render state with a single identifier value must capture
        // exactly that one token (the accumulation loop is scoped to mask keys).
        const string source = """
            technique T
            {
                pass P
                {
                    CullMode = None;
                    PixelShader = compile ps_3_0 PS();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        var rs = result.Value.Techniques[0].Passes[0].RenderStates;
        rs.Where(e => e.Key == "CullMode" && e.Value == "None").ShouldHaveSingleItem();
    }

    // -------------------------------------------------------------------------
    // Phase 45 — FX pre-parser robustness (dropped-operator bug class), B4/B5/B6/B7.
    // -------------------------------------------------------------------------

    // B4 — a legacy 'texture T < …annotation… >;' has its own inner ';' separators
    // inside the annotation block; ConsumeLegacyTextureDecl must stop at the ';' at
    // angle-bracket depth 0, not the first inner one, or the rewrite leaks '>;'.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_B4_LegacyTextureWithStringAnnotation_NoLeakedAngleBracket()
    {
        // The annotation value is a STRING, so there is a ';' right after it INSIDE
        // the '< … >'. The old consume stopped there and leaked the trailing '>;'.
        const string source = """
            texture Tex < string Name = "diffuse"; >;

            float4 PS() : COLOR { return float4(1, 1, 1, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("Texture2D Tex;", Case.Sensitive);
        // The whole legacy declaration (incl. annotation) is gone — no stray '>'
        // or ';'-leftover, no annotation contents.
        stripped.ShouldNotContain(">", Case.Sensitive);
        stripped.ShouldNotContain("Name", Case.Sensitive);
        stripped.ShouldNotContain("texture Tex", Case.Sensitive);
    }

    [Fact]
    public void Parse_B4_LegacyTextureWithMultiEntryAnnotation_FullyConsumed()
    {
        // Several annotation entries, including an initializer-list value '{1,1}',
        // each ending in an inner ';'. Only the depth-0 ';' ends the declaration.
        const string source = """
            texture Tex < string N = "x"; float2 Dim = {1,1}; >;

            float4 PS() : COLOR { return float4(1, 1, 1, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("Texture2D Tex;", Case.Sensitive);
        stripped.ShouldNotContain(">", Case.Sensitive);
        stripped.ShouldNotContain("Dim", Case.Sensitive);
    }

    [Fact]
    public void Parse_B4_LegacyTextureBareAndRegister_StillRewrite()
    {
        // The pre-existing forms must keep rewriting after the depth-tracking change.
        const string bareSource = """
            texture Tex;

            float4 PS() : COLOR { return 0; }
            """;
        FxPreParser.Parse(bareSource, sourceFile: "test.fx").Value.StrippedHlsl
            .ShouldContain("Texture2D Tex;", Case.Sensitive);

        const string registerSource = """
            texture Tex : register(t0);

            float4 PS() : COLOR { return 0; }
            """;
        string registerStripped = FxPreParser.Parse(registerSource, sourceFile: "test.fx").Value.StrippedHlsl;
        registerStripped.ShouldContain("Texture2D Tex;", Case.Sensitive);
        registerStripped.ShouldNotContain("register", Case.Sensitive);
    }

    // B5 — a modern resource whose VARIABLE NAME is a legacy texture keyword
    // ('Texture2D Texture : register(t0);'). The keyword is in name position
    // (preceded by an Identifier / '>'), so the legacy-texture rewrite must NOT fire.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_B5_ResourceNamedTexture_LeftIntact()
    {
        // 'Texture2D Texture : register(t0);' — the variable is literally named
        // 'Texture'. The old guard only excluded the templated form, so this became
        // the broken 'Texture2D Texture2D register;'. It must now pass through.
        const string source = """
            Texture2D Texture : register(t0);
            SamplerState Sampler : register(s0);

            float4 PS(float2 uv : TEXCOORD0) : COLOR0
            {
                return Texture.Sample(Sampler, uv);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.ShouldContain("Texture2D Texture : register(t0);", Case.Sensitive);
        stripped.ShouldNotContain("Texture2D Texture2D", Case.Sensitive);
    }

    [Fact]
    public void Parse_B5_ResourceNamedTextureWithSemanticOnly_LeftIntact()
    {
        // Same B5 class but with a plain ': SEMANTIC' instead of register(...).
        const string source = """
            Texture2D Texture : TEXCOORD0;

            float4 PS() : SV_Target { return 0; }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("Texture2D Texture : TEXCOORD0;", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("Texture2D Texture2D", Case.Sensitive);
    }

    [Fact]
    public void Parse_B5_GenuineLegacyTextureAfterAnotherDecl_StillRewrites()
    {
        // A genuine legacy 'texture' type keyword at statement start (preceded by
        // ';' from the previous decl) must still rewrite — the guard only declines
        // the NAME position.
        const string source = """
            float Cutoff;
            texture Foo;

            float4 PS() : COLOR { return 0; }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("Texture2D Foo;", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("texture Foo", Case.Sensitive);
    }

    // B6 — a VERTEX shader whose function-return semantic is ': COLOR' (e.g. it
    // writes POSITION via an 'out' param). The COLOR->SV_Target rewrite is deferred
    // and must SKIP vertex-shader entries, while still rewriting PS entries/helpers.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_B6_VertexShaderColorReturn_NotRewritten()
    {
        // The VS writes POSITION via an out-param and returns ': COLOR'. fxc/mgfxc
        // accept this; rewriting the VS return to ': SV_Target' would make it invalid.
        const string source = """
            float4 MainVS(float4 pos : POSITION0, out float4 outPos : SV_POSITION) : COLOR0
            {
                outPos = pos;
                return float4(1, 0, 0, 1);
            }

            float4 MainPS(float4 color : COLOR0) : COLOR0
            {
                return color;
            }

            technique T
            {
                pass P
                {
                    VertexShader = compile vs_4_0_level_9_1 MainVS();
                    PixelShader  = compile ps_4_0_level_9_1 MainPS();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The VS return semantic stays ': COLOR0' (NOT rewritten).
        stripped.ShouldContain("out float4 outPos : SV_POSITION) : COLOR0", Case.Sensitive);
        // The PS return semantic IS still rewritten to ': SV_Target0'.
        stripped.ShouldContain("float4 color : COLOR0) : SV_Target0", Case.Sensitive);
    }

    [Fact]
    public void Parse_B6_PixelShaderColorReturn_StillRewrittenWithoutTechnique()
    {
        // Regression guard: with NO technique (so no VS entry is registered), a PS
        // function-return ': COLOR' must still be rewritten — the deferral only
        // skips VS entries, it does not disable the rewrite.
        const string source = """
            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return float4(uv, 0, 1);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain(": SV_Target", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain(": COLOR", Case.Sensitive);
    }

    [Fact]
    public void Parse_B6_StructReturningVertexShader_Unaffected()
    {
        // The canonical VS returns a STRUCT (with a POSITION field), which never
        // matches the '): COLOR {' rewrite shape — pinned as unaffected.
        const string source = """
            struct VOut { float4 Pos : SV_POSITION; float4 Col : COLOR0; };

            VOut MainVS(float4 pos : POSITION0)
            {
                VOut o;
                o.Pos = pos;
                o.Col = float4(1, 1, 1, 1);
                return o;
            }

            technique T { pass P { VertexShader = compile vs_4_0_level_9_1 MainVS(); } }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        // The struct field 'COLOR0' is an output-struct member, not a function-return
        // semantic, so it is untouched.
        result.Value.StrippedHlsl.ShouldContain("float4 Col : COLOR0;", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("SV_Target", Case.Sensitive);
    }

    // B7 — an array-indexed relational with an assignment in a ternary arm inside a
    // FUNCTION BODY mimics the 'Ident Ident <' annotation shape once '?'/':'/'['/']'
    // are dropped. The global annotation strip is gated on brace depth 0, so an
    // in-body expression can never be misread as an annotation.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_B7_ArrayIndexedRelationalTernaryAssign_SurvivesVerbatim()
    {
        // 'arr[i] < y ? z = w : q;' -> tokens 'arr i < y z = w q' whose 'y z =' tail
        // satisfies IsAnnotationBlockStart. Gating on brace depth 0 stops the misparse.
        const string source = """
            float arr[4];

            float4 PS() : COLOR
            {
                int i = 0;
                float y = 1, z = 2, w = 3, q = 4;
                float r = arr[i] < y ? z = w : q;
                return float4(r, r, r, 1);
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        // The in-body expression survives verbatim (no annotation stripping).
        result.Value.StrippedHlsl.ShouldContain("float r = arr[i] < y ? z = w : q;", Case.Sensitive);
        // It was never captured as a parameter annotation.
        result.Value.ParameterAnnotations.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_B7_GenuineGlobalAnnotationAtDepthZero_StillStripped()
    {
        // The brace-depth gate must not break a REAL global-parameter annotation,
        // which is always at depth 0.
        const string source = """
            float P < float UIMin = 0; float UIMax = 1; > = 0.5;

            float4 PS() : COLOR { return P; }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ParameterAnnotations.Where(a => a.ParameterName == "P").ShouldHaveSingleItem();
        // The '< … >' block is stripped from the global declaration.
        result.Value.StrippedHlsl.ShouldNotContain("UIMin", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("float P", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("= 0.5;", Case.Sensitive);
    }

    [Fact]
    public void Parse_B7_GlobalAnnotationAfterInitializerList_StillStripped()
    {
        // A global initializer list '{...}' balances brace depth back to 0, so a
        // following global annotation must still be stripped.
        const string source = """
            float3 Tint = {1, 1, 1};
            float P < float UIMin = 0; > = 0.5;

            float4 PS() : COLOR { return float4(Tint * P, 1); }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ParameterAnnotations.Where(a => a.ParameterName == "P").ShouldHaveSingleItem();
        result.Value.StrippedHlsl.ShouldNotContain("UIMin", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("float3 Tint = {1, 1, 1};", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Explicit register(sN) capture for the OpenGL sampler slot (issue #189)
    //
    // The SM4 rewrite turns `sampler X : register(s2);` into
    // `Texture2D X_SDTexture; SamplerState X;`, dropping the clause before DXC ever
    // sees it. These pin that the index is RECORDED on the way past, keyed on the
    // TEXTURE name because that is what the GL sampler table joins on.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_BareSamplerWithRegister_RecordsExplicitGlSlotKeyedOnSynthesizedTexture()
    {
        const string src = """
            sampler MaskA : register(s2);
            sampler MaskB : register(s3);
            float4 PS(float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(MaskA, uv) + tex2D(MaskB, uv);
            }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(src, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ExplicitGlSamplerSlots["MaskA_SDTexture"].ShouldBe(2);
        result.Value.ExplicitGlSamplerSlots["MaskB_SDTexture"].ShouldBe(3);
    }

    [Fact]
    public void Parse_SamplerStateFormWithRegister_RecordsExplicitGlSlotKeyedOnReferencedTexture()
    {
        // Form 1: `sampler2D S : register(sN) = sampler_state { Texture = <T>; };`
        // Here the sampler binds an EXISTING texture, so the slot must key on that
        // texture's name rather than on a synthesized one.
        const string src = """
            Texture2D SpriteTexture;
            sampler2D SpriteTextureSampler : register(s3) = sampler_state
            {
                Texture = <SpriteTexture>;
            };
            float4 PS(float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(SpriteTextureSampler, uv);
            }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(src, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ExplicitGlSamplerSlots["SpriteTexture"].ShouldBe(3);
    }

    [Fact]
    public void Parse_SamplerWithoutRegister_RecordsNoExplicitGlSlot()
    {
        // The map must stay EMPTY for an unannotated shader, because a present-but-wrong
        // entry would silently move a texture unit, whereas an absent one falls back to
        // declaration-index allocation (the behaviour that shipped before #189).
        const string src = """
            sampler MaskA;
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(MaskA, uv); }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(src, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ExplicitGlSamplerSlots.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_ModernTextureAndSamplerRegisters_RecordNoExplicitGlSlot()
    {
        // MEASURED, not a limitation: mgfxc's OpenGL build IGNORES the annotations on the
        // modern spelling and allocates by texture declaration order. Recording them here
        // would make ShadowDusk diverge, so the map must stay empty for this shape.
        const string src = """
            Texture2D TexA : register(t3);
            SamplerState SampA : register(s2);
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return TexA.Sample(SampA, uv); }
            technique T { pass P { PixelShader = compile ps_4_0 PS(); } }
            """;

        var result = FxPreParser.Parse(src, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ExplicitGlSamplerSlots.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_MixedAnnotatedAndUnannotatedSamplers_RecordsOnlyTheAnnotatedOnes()
    {
        // The real Apos.Shapes shape: an explicit s0, an unannotated sampler, and an
        // explicit s2. Each resolves independently; the unannotated one must not acquire
        // an entry, so it can fall through to its declaration index.
        const string src = """
            sampler TextureSampler : register(s0);
            sampler FontSampler;
            sampler BlueNoiseSampler : register(s2);
            float4 PS(float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(TextureSampler, uv) + tex2D(FontSampler, uv) + tex2D(BlueNoiseSampler, uv);
            }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(src, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ExplicitGlSamplerSlots["TextureSampler_SDTexture"].ShouldBe(0);
        result.Value.ExplicitGlSamplerSlots["BlueNoiseSampler_SDTexture"].ShouldBe(2);
        result.Value.ExplicitGlSamplerSlots.ContainsKey("FontSampler_SDTexture").ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // Modern `SamplerState : register(sN)` is RESERVED, not assigned (issue #189)
    //
    // At ps_3_0 a texture and a sampler are ONE object in ONE register namespace.
    // A modern SamplerState still occupies its declared register, so the combined
    // samplers fxc synthesizes are allocated AROUND it. Measured: one texture plus
    // `SamplerState S : register(s0)` makes mgfxc emit ps_s1, not ps_s0.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ModernSamplerStateWithRegister_ReservesThatSlot()
    {
        const string src = """
            Texture2D A;
            SamplerState S : register(s1);
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return A.Sample(S, uv); }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(src, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ReservedGlSamplerSlots.ShouldBe(new[] { 1 });
        // A reservation is NOT an assignment: nothing is pinned to a texture.
        result.Value.ExplicitGlSamplerSlots.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_SeveralModernSamplerStates_ReserveAllOfTheirSlots()
    {
        const string src = """
            Texture2D A;
            Texture2D B;
            SamplerState P : register(s0);
            SamplerState Q : register(s3);
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return A.Sample(P, uv) + B.Sample(Q, uv); }
            technique T { pass P0 { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(src, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ReservedGlSamplerSlots.OrderBy(x => x).ShouldBe(new[] { 0, 3 });
    }

    [Fact]
    public void Parse_ModernSamplerStateWithoutRegister_ReservesNothing()
    {
        const string src = """
            Texture2D A;
            SamplerState S;
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return A.Sample(S, uv); }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(src, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ReservedGlSamplerSlots.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Phase 58 Area C - unloadable pass shader stages -> FX0014, never FX0008
    // -------------------------------------------------------------------------

    // The four stages, in BOTH forms mgfxc was measured to reject (2026-08-05, pinned
    // mgfxc 3.8.2.1105 /Profile:DirectX_11): 'compile <profile> Entry();' and 'NULL;'.
    // The NULL arm matters because 'VertexShader = NULL;' IS accepted (fxc parity, bug-hunt
    // M14), so the NULL path is a real branch that could have let these through silently.
    public static TheoryData<string, string, string> UnloadableStageAssignments() => new()
    {
        { "HullShader",     "compile hs_5_0 Entry()", "hull" },
        { "DomainShader",   "compile ds_5_0 Entry()", "domain" },
        { "GeometryShader", "compile gs_4_0 Entry()", "geometry" },
        { "ComputeShader",  "compile cs_5_0 Entry()", "compute" },
        { "HullShader",     "NULL",                   "hull" },
        { "DomainShader",   "NULL",                   "domain" },
        { "GeometryShader", "NULL",                   "geometry" },
        { "ComputeShader",  "NULL",                   "compute" },
    };

    [Theory]
    [MemberData(nameof(UnloadableStageAssignments))]
    public void Parse_UnloadableShaderStage_ReturnsFX0014NamingTheStage(
        string stageKey, string assignment, string stageName)
    {
        string source = $$"""
            float4 MainPS() : COLOR0 { return 1; }
            technique T
            {
                pass P
                {
                    PixelShader = compile ps_3_0 MainPS();
                    {{stageKey}} = {{assignment}};
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.UnsupportedShaderStage);

        // The whole point of the fix: the message must name the stage and the permanent
        // reason, not blame punctuation on a file whose punctuation is correct.
        result.Error.Message.ShouldContain(stageKey, Case.Sensitive);
        result.Error.Message.ShouldContain(stageName, Case.Sensitive);
        result.Error.Message.ShouldContain("vertex and pixel", Case.Sensitive);
        result.Error.Message.ShouldNotContain("';'", Case.Sensitive);
    }

    [Theory]
    [MemberData(nameof(UnloadableStageAssignments))]
    public void Parse_UnloadableShaderStage_PointsAtTheStageKeywordNotThePunctuation(
        string stageKey, string assignment, string stageName)
    {
        _ = stageName;

        // The stage keyword is the 6th line, indented by 8 spaces => column 9 (1-based).
        // Before the fix the caret landed further right, on whatever token the render-state
        // path choked on, which is what sent users hunting for a syntax error.
        string source = $$"""
            float4 MainPS() : COLOR0 { return 1; }
            technique T
            {
                pass P
                {
                    PixelShader = compile ps_3_0 MainPS();
                    {{stageKey}} = {{assignment}};
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Line.ShouldBe(7);
        result.Error.Column.ShouldBe(9);
    }

    [Fact]
    public void Parse_UnloadableShaderStage_IsCaseInsensitiveLikeEveryOtherPassKey()
    {
        // Pass keys are matched OrdinalIgnoreCase throughout the pre-parser; a lowercased
        // spelling must not slip past into render-state parsing and resurrect FX0008.
        const string source = """
            float4 MainPS() : COLOR0 { return 1; }
            technique T
            {
                pass P
                {
                    PixelShader = compile ps_3_0 MainPS();
                    computeshader = compile cs_5_0 Entry();
                }
            }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.UnsupportedShaderStage);
    }

    [Fact]
    public void Parse_ParameterNamedLikeAStage_StillCompiles()
    {
        // The guard is scoped to PASS keys only. A global variable or function named
        // 'GeometryShader' is ordinary HLSL and must be untouched - the pre-parser has a
        // history of firing heuristics outside their scope (Phase 45's whole defect class).
        const string source = """
            float4 GeometryShader;
            float4 MainPS() : COLOR0 { return GeometryShader; }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques.Count.ShouldBe(1);
    }

    [Fact]
    public void Parse_RealRenderStateStillReachesRenderStateParsing()
    {
        // Guards the insertion point: the new check sits before the '=' is consumed, so it
        // must not disturb the ordinary render-state path it now precedes.
        const string source = """
            float4 MainPS() : COLOR0 { return 1; }
            technique T { pass P { CullMode = None; PixelShader = compile ps_3_0 MainPS(); } }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Techniques[0].Passes[0].RenderStates
            .ShouldContain(rs => rs.Key == "CullMode" && rs.Value == "None");
    }

    [Fact]
    public void Parse_SamplerStateAsFunctionParameter_ReservesNothing()
    {
        // The reservation scan requires the exact `SamplerState IDENT register ( sN )` shape.
        // A function parameter cannot match it, because no register clause follows - this pins
        // that the scan does not fire on one.
        const string src = """
            Texture2D A;
            SamplerState S;
            float4 Fetch(Texture2D t, SamplerState s, float2 uv) { return t.Sample(s, uv); }
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return Fetch(A, S, uv); }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(src, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ReservedGlSamplerSlots.ShouldBeEmpty();
    }
}
