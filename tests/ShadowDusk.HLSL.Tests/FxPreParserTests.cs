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
}
