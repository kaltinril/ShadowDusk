#nullable enable
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.Core;
using FluentAssertions;
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Techniques.Should().BeEmpty();
        result.Value.Samplers.Should().BeEmpty();
        result.Value.StrippedHlsl.Should().Be("");
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

        result.IsSuccess.Should().BeTrue();
        var pass = result.Value.Techniques[0].Passes[0];
        pass.VertexEntryPoint.Should().BeNull();
        pass.PixelEntryPoint.Should().Be("PSMain");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Samplers.Should().ContainSingle()
            .Which.TextureReference.Should().BeNull();
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Techniques.Should().ContainSingle()
            .Which.Name.Should().Be("Render");
        result.Value.Techniques[0].IsEffect11.Should().BeTrue();
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

        result.IsSuccess.Should().BeTrue();

        var techniques = result.Value.Techniques;
        techniques.Should().HaveCount(1);

        var tech = techniques[0];
        tech.Name.Should().Be("MyTechnique");
        tech.Passes.Should().HaveCount(1);

        var pass = tech.Passes[0];
        pass.Name.Should().Be("Pass1");
        pass.VertexEntryPoint.Should().Be("VSMain");
        pass.VertexProfile.Should().Be("vs_3_0");
        pass.PixelEntryPoint.Should().Be("PSMain");
        pass.PixelProfile.Should().Be("ps_3_0");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Techniques.Should().HaveCount(1);

        var passes = result.Value.Techniques[0].Passes;
        passes.Should().HaveCount(2);
        passes[0].Name.Should().Be("A");
        passes[1].Name.Should().Be("B");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Techniques.Should().HaveCount(2);
        result.Value.Techniques[0].Name.Should().Be("TechOne");
        result.Value.Techniques[1].Name.Should().Be("TechTwo");
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

        result.IsSuccess.Should().BeTrue();

        var renderStates = result.Value.Techniques[0].Passes[0].RenderStates;
        renderStates.Should().HaveCount(2);
        renderStates.Should().Contain(rs => rs.Key == "CullMode" && rs.Value == "None");
        renderStates.Should().Contain(rs => rs.Key == "AlphaBlendEnable" && rs.Value == "True");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Samplers.Should().HaveCount(1);

        var sampler = result.Value.Samplers[0];
        sampler.Name.Should().Be("MySampler");
        sampler.SamplerType.Should().Be("sampler2D");
        sampler.TextureReference.Should().Be("MyTexture");
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

        result.IsSuccess.Should().BeTrue();

        var tech = result.Value.Techniques[0];
        tech.Annotations.Should().HaveCount(1);

        var annotation = tech.Annotations[0];
        annotation.Name.Should().Be("UIName");
        annotation.Type.Should().Be("string");
        annotation.Value.Should().Be("\"X\"");
    }

    // -------------------------------------------------------------------------
    // T08 — global parameter annotation extracted and stripped
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_GlobalParameterAnnotation_ExtractedAndStripped()
    {
        const string source = "float P < float UIMin = 0; > = 0.5;";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.Should().BeTrue();
        result.Value.ParameterAnnotations.Should().HaveCount(1);

        var pa = result.Value.ParameterAnnotations[0];
        pa.ParameterName.Should().Be("P");
        pa.Entries.Should().HaveCount(1);
        pa.Entries[0].Name.Should().Be("UIMin");

        // The annotation block (angle brackets and contents) must be stripped
        // so DXC never sees it; the assignment "= 0.5;" must survive.
        result.Value.StrippedHlsl.Should().NotContain("<");
        result.Value.StrippedHlsl.Should().Contain("= 0.5");
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

        result.IsSuccess.Should().BeTrue();

        var stripped = result.Value.StrippedHlsl;
        var lines = stripped.Split('\n');

        // The HLSL declaration must remain on line 1 (index 0)
        lines[0].Should().Contain("MyColor");
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(FxParseErrorCode.UnexpectedEof);
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(FxParseErrorCode.MalformedCompileExpression);
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Techniques[0].Passes[0].VertexProfile.Should().Be("vs_99_0");
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(FxParseErrorCode.DuplicateTechniqueName);
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(FxParseErrorCode.DuplicatePassName);
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(FxParseErrorCode.UnclosedAnnotationBlock);
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(FxParseErrorCode.MissingSemicolon);
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain("#if SM4");
        result.Value.StrippedHlsl.Should().Contain("#endif");
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

        result.IsSuccess.Should().BeTrue();

        var pass = result.Value.Techniques[0].Passes[0];
        // The commented-out VS line must NOT be parsed as a real entry-point
        pass.VertexEntryPoint.Should().BeNull();
        pass.PixelEntryPoint.Should().Be("PSMain");
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

        result.IsSuccess.Should().BeTrue();

        var pass = result.Value.Techniques[0].Passes[0];
        pass.VertexProfile.Should().Be("vs_3_0");
        pass.PixelProfile.Should().Be("ps_3_0");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Techniques.Should().HaveCount(32);
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Samplers.Should().HaveCount(1);
        result.Value.Samplers[0].TextureReference.Should().Be("MyTex");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Samplers.Should().HaveCount(1);
        result.Value.Samplers[0].TextureReference.Should().Be("MyTex");
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

        result.IsFailure.Should().BeTrue();
        result.Error.Line.Should().BeGreaterThan(0);
        result.Error.Column.Should().BeGreaterThan(0);
        result.Error.SourceFile.Should().Be("test.fx");
        result.Error.Message.Should().NotBeNullOrEmpty();
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain(": SV_Target");
        result.Value.StrippedHlsl.Should().NotContain(": COLOR");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain(": SV_Target3");
        result.Value.StrippedHlsl.Should().NotContain("COLOR3");
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

        result.IsSuccess.Should().BeTrue();

        // The struct member's ': COLOR0;' must survive verbatim.
        result.Value.StrippedHlsl.Should().Contain("float4 Color    : COLOR0;");

        // The function return ': COLOR0' must be rewritten to ': SV_Target0'.
        result.Value.StrippedHlsl.Should().Contain(": SV_Target0");
        result.Value.StrippedHlsl.Should().NotContain("input) : COLOR0");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        // Declaration rewritten: legacy form gone, modern SamplerState left behind.
        stripped.Should().Contain("SamplerState SpriteTextureSampler;");
        stripped.Should().NotContain("sampler_state");
        stripped.Should().NotContain("sampler2D");

        // No synthesized texture — the sampler_state bound an existing Texture2D.
        stripped.Should().NotContain("_SDTexture");

        // tex2D rewritten to a Sample call on the bound texture; args preserved.
        stripped.Should().Contain("SpriteTexture.Sample(SpriteTextureSampler, uv)");
        stripped.Should().NotContain("tex2D");

        // Metadata still extracted as before.
        result.Value.Samplers.Should().ContainSingle();
        result.Value.Samplers[0].TextureReference.Should().Be("SpriteTexture");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        // A Texture2D is synthesized and paired with a modern SamplerState.
        stripped.Should().Contain("Texture2D s0_SDTexture;");
        stripped.Should().Contain("SamplerState s0;");

        // tex2D rewritten to sample the synthesized texture.
        stripped.Should().Contain("s0_SDTexture.Sample(s0, uv)");
        stripped.Should().NotContain("tex2D");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("Texture2D TextureSampler_SDTexture;");
        stripped.Should().Contain("SamplerState TextureSampler;");
        stripped.Should().Contain("TextureSampler_SDTexture.Sample(TextureSampler, uv)");
        stripped.Should().NotContain("tex2D");
        stripped.Should().NotContain("register");
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

        result.IsSuccess.Should().BeTrue();
        var lines = result.Value.StrippedHlsl.Replace("\r\n", "\n").Split('\n');

        // Same number of lines as the source.
        lines.Length.Should().Be(source.Replace("\r\n", "\n").Split('\n').Length);

        // The rewritten declaration sits on the original first line (line 3 → index 2).
        lines[2].Should().Contain("SamplerState SpriteTextureSampler;");

        // The MainPS signature and body stay on their original lines.
        lines[7].Should().Contain("float4 MainPS");
        lines[9].Should().Contain(".Sample(SpriteTextureSampler, uv)");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().NotContain("sampler_state");
        stripped.Should().NotContain("SamplerState UnusedSampler;");
        stripped.Should().NotContain("_SDTexture");

        // Metadata is still extracted regardless of rewriting.
        result.Value.Samplers.Should().ContainSingle();
        result.Value.Samplers[0].Name.Should().Be("UnusedSampler");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("sampler unusedS;");
        stripped.Should().NotContain("_SDTexture");
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

        result.IsSuccess.Should().BeTrue();

        // Byte-identical: nothing in this source matches any rewrite rule.
        result.Value.StrippedHlsl.Should().Be(source);
        result.Value.Samplers.Should().BeEmpty();
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("Texture2D s0_SDTexture;");
        stripped.Should().Contain("SamplerState s0;");
        stripped.Should().Contain("SamplerState _secondTextureSampler;");

        stripped.Should().Contain("s0_SDTexture.Sample(s0, uv)");
        stripped.Should().Contain("_secondTexture.Sample(_secondTextureSampler, uv)");
        stripped.Should().NotContain("tex2D");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("sampler s0;");
        stripped.Should().NotContain("_SDTexture");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("Texture2D _dissolveTex;");
        // The bare legacy 'texture ' keyword must be gone (Texture2D is fine).
        stripped.Should().NotContain("texture _dissolveTex");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("Texture2D _dissolveTex;");
        stripped.Should().Contain("SamplerState _dissolveTexSampler;");
        stripped.Should().Contain("_dissolveTex.Sample(_dissolveTexSampler, uv)");
        stripped.Should().NotContain("sampler_state");
        stripped.Should().NotContain("tex2D");
        // No synthesized texture — the sampler_state bound the explicit texture.
        stripped.Should().NotContain("_SDTexture");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("Texture2D Diffuse;");
        stripped.Should().NotContain("<");
        stripped.Should().NotContain("ResourceName");
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

        result.IsSuccess.Should().BeTrue();
        var lines = result.Value.StrippedHlsl.Replace("\r\n", "\n").Split('\n');

        lines.Length.Should().Be(source.Replace("\r\n", "\n").Split('\n').Length);
        lines[0].Should().Contain("Texture2D _dissolveTex;");
        lines[2].Should().Contain("float4 PS");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Be(source);
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Samplers.Should().HaveCount(1);
        result.Value.Samplers[0].TextureReference.Should().Be("MyTex");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        // Same rewrite the keyword form gets: modern SamplerState, legacy block gone.
        stripped.Should().Contain("SamplerState MaskSampler;");
        stripped.Should().NotContain("MinFilter");

        // No synthesized texture — the block bound the existing Texture2D, and
        // tex2D resolves to a Sample call on it.
        stripped.Should().NotContain("_SDTexture");
        stripped.Should().Contain("Mask.Sample(MaskSampler, uv)");
        stripped.Should().NotContain("tex2D");

        // Metadata capture is identical to the keyword form.
        result.Value.Samplers.Should().ContainSingle();
        result.Value.Samplers[0].Name.Should().Be("MaskSampler");
        result.Value.Samplers[0].TextureReference.Should().Be("Mask");
        result.Value.Samplers[0].StateEntries.Should().ContainSingle(
            e => e.Key == "MinFilter" && e.Value == "LINEAR");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("Texture2D t;");
        stripped.Should().Contain("SamplerState s;");
        stripped.Should().Contain("t.Sample(s, uv)");
        stripped.Should().NotContain("tex2D");
        stripped.Should().NotContain("register");
        stripped.Should().NotContain("AddressU");

        result.Value.Samplers.Should().ContainSingle();
        result.Value.Samplers[0].TextureReference.Should().Be("t");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().NotContain("UnusedSampler");
        stripped.Should().NotContain("SomeTexture");
        stripped.Should().NotContain("_SDTexture");

        result.Value.Samplers.Should().ContainSingle();
        result.Value.Samplers[0].Name.Should().Be("UnusedSampler");
        result.Value.Samplers[0].TextureReference.Should().Be("SomeTexture");
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

        result.IsSuccess.Should().BeTrue();

        // No sampler declaration was (mis)captured…
        result.Value.Samplers.Should().BeEmpty();

        // …and the bodies survive: struct fields intact, both sampler-typed
        // parameters untouched (only the function return ': COLOR0' is rewritten,
        // which is the pre-existing SV_Target treatment, not sampler handling).
        string stripped = result.Value.StrippedHlsl;
        stripped.Should().Contain("float4 Color : COLOR0;");
        stripped.Should().Contain("(sampler2D s, float2 uv)");
        stripped.Should().Contain("(float2 uv, sampler2D s)");
        stripped.Should().Contain("return float4(uv, 0, 1);");
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

        result.IsSuccess.Should().BeTrue();
        var states = result.Value.Techniques[0].Passes[0].RenderStates;
        states.Should().ContainSingle(s => s.Key == "DepthBias").Which.Value.Should().Be("-0.5");
        states.Should().ContainSingle(s => s.Key == "SlopeScaleDepthBias").Which.Value.Should().Be("-2");
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

        result.IsSuccess.Should().BeTrue();
        var sampler = result.Value.Samplers.Should().ContainSingle().Subject;
        sampler.StateEntries.Should().ContainSingle(e => e.Key == "MipMapLodBias")
            .Which.Value.Should().Be("-2");
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

        result.IsSuccess.Should().BeTrue();
        var annotation = result.Value.ParameterAnnotations.Should().ContainSingle().Subject;
        annotation.Entries.Should().ContainSingle(e => e.Name == "UIMin")
            .Which.Value.Should().Be("-1.0");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain("float a = 1.0 - 0.25;");
        result.Value.StrippedHlsl.Should().Contain("return float4(-a, a - 1.0, 0, 1);");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Techniques[0].Passes[0].RenderStates
            .Should().ContainSingle(s => s.Key == "BlendFactor")
            .Which.Value.Should().Be("0x80FF8080");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Techniques[0].Passes[0].RenderStates
            .Should().ContainSingle(s => s.Key == "DepthBias")
            .Which.Value.Should().Be("1e-4");
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(FxParseErrorCode.UnknownCharacter);
        result.Error.Message.Should().Contain("'@'");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain("int mask = (3 & 1) | (4 ^ 2);");
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

        result.IsSuccess.Should().BeTrue();
        // The relational operator must survive verbatim so DXC/vkd3d see the original '<='.
        result.Value.StrippedHlsl.Should().Contain("value <= 0.5f");
        result.Value.ParameterAnnotations.Should().BeEmpty();
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

        result.IsSuccess.Should().BeTrue();
        // The operator must reach DXC/vkd3d unchanged (not consumed as an annotation).
        result.Value.StrippedHlsl.Should().Contain(mustSurvive);
        result.Value.ParameterAnnotations.Should().BeEmpty();
    }

    [Fact]
    public void Parse_GenuineGlobalAnnotation_StillStrippedAfterRelationalFix()
    {
        // Guard: the discriminator must NOT regress genuine annotation parsing.
        const string source = "float P < float UIMin = 0; > = 0.5;";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.Should().BeTrue();
        result.Value.ParameterAnnotations.Should().HaveCount(1);
        result.Value.ParameterAnnotations[0].ParameterName.Should().Be("P");
        result.Value.ParameterAnnotations[0].Entries.Should().ContainSingle()
            .Which.Name.Should().Be("UIMin");

        // The '< ... >' block is stripped; the assignment survives.
        result.Value.StrippedHlsl.Should().NotContain("<");
        result.Value.StrippedHlsl.Should().Contain("= 0.5");
    }

    [Fact]
    public void Parse_EmptyGlobalAnnotationBlock_StillStripped()
    {
        // An empty annotation block ('< >') is accepted by ParseAnnotationBlock, so the
        // discriminator's RAngle branch must keep it on the annotation path.
        const string source = "float P < > = 0.5;";

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.Should().BeTrue();
        result.Value.ParameterAnnotations.Should().ContainSingle()
            .Which.ParameterName.Should().Be("P");
        result.Value.ParameterAnnotations[0].Entries.Should().BeEmpty();
        result.Value.StrippedHlsl.Should().NotContain("<");
        result.Value.StrippedHlsl.Should().Contain("= 0.5");
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

        result.IsSuccess.Should().BeTrue();
        // The operator/expression must reach DXC unchanged (not consumed as annotation).
        result.Value.StrippedHlsl.Should().Contain(mustSurvive);
        result.Value.ParameterAnnotations.Should().BeEmpty();
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain("length(v) < radius ? 1.0f : 0.0f");
        result.Value.ParameterAnnotations.Should().BeEmpty();
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain("return a > 0.5f;");
        result.Value.ParameterAnnotations.Should().BeEmpty();
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain("SomeDefine < 4;");
        result.Value.ParameterAnnotations.Should().BeEmpty();
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

        result.IsSuccess.Should().BeTrue();
        // The generic template survives verbatim; nothing was captured as annotation.
        result.Value.StrippedHlsl.Should().Contain("Texture2D<float4> Tex;");
        result.Value.ParameterAnnotations.Should().BeEmpty();
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

        result.IsSuccess.Should().BeTrue();
        var pa = result.Value.ParameterAnnotations.Should().ContainSingle().Subject;
        pa.ParameterName.Should().Be("P");
        pa.Entries.Select(e => e.Name).Should().Equal("UIName", "UIMin", "UIMax");

        // The whole '< ... >' block is stripped; only the assignment survives.
        result.Value.StrippedHlsl.Should().NotContain("<");
        result.Value.StrippedHlsl.Should().NotContain("UIName");
        result.Value.StrippedHlsl.Should().Contain("= 0.5");
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

        result.IsSuccess.Should().BeTrue();
        var pa = result.Value.ParameterAnnotations.Should().ContainSingle().Subject;
        pa.ParameterName.Should().Be("Intensity");
        pa.Entries.Should().ContainSingle(e => e.Name == "UIMin").Which.Value.Should().Be("-1.0");

        result.Value.StrippedHlsl.Should().NotContain("<");
        result.Value.StrippedHlsl.Should().Contain("= 0.5");
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

        result.IsSuccess.Should().BeTrue();

        // The genuine annotation was captured and its '<...>' stripped...
        var pa = result.Value.ParameterAnnotations.Should().ContainSingle().Subject;
        pa.ParameterName.Should().Be("Threshold");
        pa.Entries.Select(e => e.Name).Should().Equal("UIMin", "UIMax");

        // ...while the body's relational operator survived verbatim.
        string stripped = result.Value.StrippedHlsl;
        stripped.Should().Contain("return a <= Threshold ? 0.0f : 1.0f;");
        // Only the annotation '<...>' was removed; the body '<=' remains, so exactly
        // one '<' (from '<=') is left in the output.
        stripped.Should().Contain("<=");
        stripped.Should().NotContain("UIMin");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain("SpriteTexture.SampleGrad(TexSampler, uv, ddx(uv), ddy(uv))");
        result.Value.StrippedHlsl.Should().NotContain("tex2Dgrad");
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(FxParseErrorCode.UnsupportedLegacyIntrinsic);
        result.Error.Message.Should().Contain($"'{intrinsic}'");
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

        result.IsSuccess.Should().BeTrue();
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The legacy initializer is gone, but the declaration survives as a
        // passthrough SamplerState (NOT erased) so the .Sample call resolves.
        stripped.Should().Contain("SamplerState SpriteTextureSampler;");
        stripped.Should().NotContain("sampler_state");
        // No Texture2D is synthesized — the shader declares its own and uses it.
        stripped.Should().NotContain("_SDTexture");
        // The modern call is untouched (it is not a tex2D rewrite).
        stripped.Should().Contain("SpriteTexture.Sample(SpriteTextureSampler, uv)");

        // Metadata is still captured.
        result.Value.Samplers.Should().ContainSingle();
        result.Value.Samplers[0].Name.Should().Be("SpriteTextureSampler");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("SamplerState S;");
        stripped.Should().NotContain("Filter");
        stripped.Should().NotContain("_SDTexture");
        stripped.Should().Contain("Tex.SampleLevel(S, uv, 0)");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().NotContain("sampler_state");
        stripped.Should().NotContain("SamplerState UnusedSampler;");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        // Sampler is tex2D-referenced, so the whole decl (register clause + state
        // block) is replaced by a SamplerState; nothing of the block leaks.
        stripped.Should().Contain("SamplerState SpriteTextureSampler;");
        stripped.Should().NotContain("sampler_state");
        stripped.Should().NotContain("register");
        stripped.Should().Contain("SpriteTexture.Sample(SpriteTextureSampler, uv)");

        // Metadata captured (including the texture binding).
        result.Value.Samplers.Should().ContainSingle();
        result.Value.Samplers[0].Name.Should().Be("SpriteTextureSampler");
        result.Value.Samplers[0].TextureReference.Should().Be("SpriteTexture");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The annotation block is metadata: it must not reach DXC.
        stripped.Should().NotContain("UIName");
        stripped.Should().NotContain("UIOrder");
        // The declaration still rewrites normally (tex2D-referenced).
        stripped.Should().Contain("SamplerState SpriteTextureSampler;");
        stripped.Should().Contain("SpriteTexture.Sample(SpriteTextureSampler, uv)");

        // SamplerInfo is unaffected by the annotation.
        result.Value.Samplers.Should().ContainSingle();
        result.Value.Samplers[0].Name.Should().Be("SpriteTextureSampler");
        result.Value.Samplers[0].TextureReference.Should().Be("SpriteTexture");
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

        result.IsSuccess.Should().BeTrue();
        var rs = result.Value.Techniques[0].Passes[0].RenderStates;
        rs.Should().ContainSingle(e => e.Key == "ColorWriteEnable" && e.Value == "Red|Green|Blue");
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

        result.IsSuccess.Should().BeTrue();
        var rs = result.Value.Techniques[0].Passes[0].RenderStates;
        rs.Should().ContainSingle(e => e.Key == "ColorWriteEnable" && e.Value == "Red");
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

        result.IsSuccess.Should().BeTrue();
        var rs = result.Value.Techniques[0].Passes[0].RenderStates;
        rs.Should().ContainSingle(e => e.Key == "ColorWriteEnable1" && e.Value == "Red|Alpha");
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

        result.IsSuccess.Should().BeTrue();
        var rs = result.Value.Techniques[0].Passes[0].RenderStates;
        rs.Should().ContainSingle(e => e.Key == "CullMode" && e.Value == "None");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("Texture2D Tex;");
        // The whole legacy declaration (incl. annotation) is gone — no stray '>'
        // or ';'-leftover, no annotation contents.
        stripped.Should().NotContain(">");
        stripped.Should().NotContain("Name");
        stripped.Should().NotContain("texture Tex");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("Texture2D Tex;");
        stripped.Should().NotContain(">");
        stripped.Should().NotContain("Dim");
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
            .Should().Contain("Texture2D Tex;");

        const string registerSource = """
            texture Tex : register(t0);

            float4 PS() : COLOR { return 0; }
            """;
        string registerStripped = FxPreParser.Parse(registerSource, sourceFile: "test.fx").Value.StrippedHlsl;
        registerStripped.Should().Contain("Texture2D Tex;");
        registerStripped.Should().NotContain("register");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        stripped.Should().Contain("Texture2D Texture : register(t0);");
        stripped.Should().NotContain("Texture2D Texture2D");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain("Texture2D Texture : TEXCOORD0;");
        result.Value.StrippedHlsl.Should().NotContain("Texture2D Texture2D");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain("Texture2D Foo;");
        result.Value.StrippedHlsl.Should().NotContain("texture Foo");
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

        result.IsSuccess.Should().BeTrue();
        string stripped = result.Value.StrippedHlsl;

        // The VS return semantic stays ': COLOR0' (NOT rewritten).
        stripped.Should().Contain("out float4 outPos : SV_POSITION) : COLOR0");
        // The PS return semantic IS still rewritten to ': SV_Target0'.
        stripped.Should().Contain("float4 color : COLOR0) : SV_Target0");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.StrippedHlsl.Should().Contain(": SV_Target");
        result.Value.StrippedHlsl.Should().NotContain(": COLOR");
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

        result.IsSuccess.Should().BeTrue();
        // The struct field 'COLOR0' is an output-struct member, not a function-return
        // semantic, so it is untouched.
        result.Value.StrippedHlsl.Should().Contain("float4 Col : COLOR0;");
        result.Value.StrippedHlsl.Should().NotContain("SV_Target");
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

        result.IsSuccess.Should().BeTrue();
        // The in-body expression survives verbatim (no annotation stripping).
        result.Value.StrippedHlsl.Should().Contain("float r = arr[i] < y ? z = w : q;");
        // It was never captured as a parameter annotation.
        result.Value.ParameterAnnotations.Should().BeEmpty();
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

        result.IsSuccess.Should().BeTrue();
        result.Value.ParameterAnnotations.Should().ContainSingle(a => a.ParameterName == "P");
        // The '< … >' block is stripped from the global declaration.
        result.Value.StrippedHlsl.Should().NotContain("UIMin");
        result.Value.StrippedHlsl.Should().Contain("float P");
        result.Value.StrippedHlsl.Should().Contain("= 0.5;");
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

        result.IsSuccess.Should().BeTrue();
        result.Value.ParameterAnnotations.Should().ContainSingle(a => a.ParameterName == "P");
        result.Value.StrippedHlsl.Should().NotContain("UIMin");
        result.Value.StrippedHlsl.Should().Contain("float3 Tint = {1, 1, 1};");
    }
}
