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
    // T21 — RenderStateMapper: CullMode/None maps to expected MonoGame target
    // -------------------------------------------------------------------------

    [Fact]
    public void RenderStateMapper_CullModeNone_MapsCorrectly()
    {
        var mapped = RenderStateMapper.TryMap("CullMode", "None");

        mapped.Should().NotBeNull();
        mapped!.MonoGameTarget.Should().Be("RasterizerState.CullMode");
        mapped.NormalizedValue.Should().NotBeNullOrEmpty();
    }

    // -------------------------------------------------------------------------
    // T22 — RenderStateMapper: unrecognized key returns null
    // -------------------------------------------------------------------------

    [Fact]
    public void RenderStateMapper_UnrecognizedKey_ReturnsNull()
    {
        var mapped = RenderStateMapper.TryMap("UnknownXyz", "SomeValue");

        mapped.Should().BeNull();
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
}
