#nullable enable

using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// GitHub issue #407: techniques are read off the PREPROCESSED translation unit, the way
/// <c>mgfxc</c> reads them, so <c>#if</c> around a technique, a technique in an <c>#include</c>d
/// file and a macro used as the <c>compile</c> target all mean what they mean to the reference
/// compiler. The inputs here are what the compile hands the parser: the raw source with its
/// <c>#include</c>s inlined (between the flattener's <c>#line</c> directives) and its macros
/// prepended as <c>#define</c>s.
/// </summary>
public sealed class FxPreParserPreprocessedTechniquesTests
{
    private const string Functions = """
        float4 P(float4 c : COLOR0) : SV_TARGET { return c; }
        float4 Q(float4 c : COLOR0) : SV_TARGET { return c * 2; }

        """;

    private static FxPreprocessedTechniques Parse(string flattened)
    {
        var result = FxPreParser.ParseTechniquesPreprocessed(flattened, "main.fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "");
        return result.Value;
    }

    private static string[] Names(FxPreprocessedTechniques techniques) =>
        techniques.Techniques.Select(t => t.Name).ToArray();

    [Fact]
    public void TechniqueInsideIfZero_DoesNotExist()
    {
        var techniques = Parse(Functions + """
            #if 0
            technique A { pass { PixelShader = compile ps_4_0 P(); } }
            #endif
            technique B { pass { PixelShader = compile ps_4_0 Q(); } }
            """);

        Names(techniques).ShouldBe(["B"]);
    }

    [Theory]
    [InlineData("#define TECHNIQUE 1\n", new[] { "A", "B" })]
    [InlineData("#define TECHNIQUE 2\n", new[] { "B" })]
    [InlineData("", new[] { "B" })]
    public void TechniqueBehindAUserDefine_FollowsTheDefine(string defines, string[] expected)
    {
        var techniques = Parse(defines + Functions + """
            #if TECHNIQUE == 1
            technique A { pass { PixelShader = compile ps_4_0 P(); } }
            #endif
            technique B { pass { PixelShader = compile ps_4_0 Q(); } }
            """);

        Names(techniques).ShouldBe(expected);
    }

    [Fact]
    public void TechniqueInAnInlinedInclude_IsFound_AndCountsAsWritten()
    {
        // What Preprocessor.Flatten leaves for `#include "tech.fxh"` followed by a technique.
        const string flattened = """
            #line 1 "tech.fxh"
            float4 P(float4 c : COLOR0) : SV_TARGET { return c; }
            technique Inc { pass { PixelShader = compile ps_4_0 P(); } }
            #line 2 "main.fx"
            float4 Q(float4 c : COLOR0) : SV_TARGET { return c * 2; }
            technique Own { pass { PixelShader = compile ps_4_0 Q(); } }
            """;

        var techniques = Parse(flattened);

        Names(techniques).ShouldBe(["Inc", "Own"]);
        techniques.HasMacroProducedTechniques.ShouldBeFalse();
        techniques.Techniques[0].Span.StartLine.ShouldBe(2, "the span is the author's line in tech.fxh");
        techniques.Techniques[1].Span.StartLine.ShouldBe(3, "the span is the author's line in main.fx");
    }

    [Fact]
    public void MacroCompileTarget_IsRecordedExpanded_AndMarkedAsFromAMacro()
    {
        var techniques = Parse("""
            #define VS_SM vs_4_0
            #define PS_SM ps_4_0
            float4 V(float4 p : POSITION0) : SV_POSITION { return p; }
            float4 P(float4 c : COLOR0) : SV_TARGET { return c; }
            technique T { pass { VertexShader = compile VS_SM V(); PixelShader = compile PS_SM P(); } }
            """);

        PassInfo pass = techniques.Techniques.ShouldHaveSingleItem().Passes.ShouldHaveSingleItem();
        pass.VertexProfile.ShouldBe("vs_4_0");
        pass.PixelProfile.ShouldBe("ps_4_0");
        pass.VertexProfileFromMacro.ShouldBeTrue();
        pass.PixelProfileFromMacro.ShouldBeTrue();

        // Diagnostics keep pointing at what the author wrote: the macro name's column.
        const string line = "technique T { pass { VertexShader = compile VS_SM V(); PixelShader = compile PS_SM P(); } }";
        pass.VertexProfileSpan!.Value.StartColumn.ShouldBe(line.IndexOf("VS_SM", StringComparison.Ordinal) + 1);
        pass.PixelProfileSpan!.Value.StartColumn.ShouldBe(line.IndexOf("PS_SM", StringComparison.Ordinal) + 1);
    }

    [Fact]
    public void LiteralCompileTarget_IsNotMarkedAsFromAMacro()
    {
        var techniques = Parse(Functions + "technique T { pass { PixelShader = compile ps_4_0 P(); } }");

        PassInfo pass = techniques.Techniques.ShouldHaveSingleItem().Passes.ShouldHaveSingleItem();
        pass.PixelProfile.ShouldBe("ps_4_0");
        pass.PixelProfileFromMacro.ShouldBeFalse();
    }

    [Fact]
    public void MacroProducedTechnique_IsListed_ButNotAsWritten()
    {
        var techniques = Parse(Functions + """
            #define TECHNIQUE(name, ps) technique name { pass { PixelShader = compile ps_4_0 ps(); } }
            TECHNIQUE(Made, P)
            technique Written { pass { PixelShader = compile ps_4_0 Q(); } }
            """);

        Names(techniques).ShouldBe(["Made", "Written"]);
        techniques.LiteralTechniques.Select(t => t.Name).ShouldBe(["Written"]);
        techniques.HasMacroProducedTechniques.ShouldBeTrue();
    }

    [Fact]
    public void SameNameTwiceAfterPreprocessing_IsFX0005()
    {
        var result = FxPreParser.ParseTechniquesPreprocessed(Functions + """
            technique T { pass { PixelShader = compile ps_4_0 P(); } }
            technique T { pass { PixelShader = compile ps_4_0 Q(); } }
            """, "main.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("FX0005");
    }

    [Fact]
    public void UnbuildableView_IsSD0009()
    {
        var result = FxPreParser.ParseTechniquesPreprocessed("#if\ntechnique T { pass { PixelShader = compile ps_4_0 P(); } }\n#endif\n", "main.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0009");
    }

    // -------------------------------------------------------------------------
    // StripTechniqueBlocks: what keeps an #include'd technique out of the compiler's input
    // -------------------------------------------------------------------------

    [Fact]
    public void StripTechniqueBlocks_BlanksEveryWrittenBlock_KeepingLines()
    {
        const string source = """
            float4 P(float4 c : COLOR0) : SV_TARGET { return c; }
            technique Inc <string a = "x";>
            {
                pass { PixelShader = compile ps_4_0 P(); }
            }
            technique11 { pass { PixelShader = compile ps_4_0 P(); } }
            float4 Q(float4 c : COLOR0) : SV_TARGET { return c; }
            """;

        string stripped = FxPreParser.StripTechniqueBlocks(source, "main.fx");

        stripped.Split('\n').Length.ShouldBe(source.Split('\n').Length);
        stripped.ShouldNotContain("technique", Case.Insensitive);
        stripped.ShouldNotContain("pass", Case.Sensitive);
        stripped.ShouldContain("float4 P(float4 c : COLOR0) : SV_TARGET { return c; }", Case.Sensitive);
        stripped.ShouldContain("float4 Q(float4 c : COLOR0) : SV_TARGET { return c; }", Case.Sensitive);
    }

    [Fact]
    public void StripTechniqueBlocks_LeavesAMacroCallAndASourceWithoutBlocksAlone()
    {
        const string source = "#define TECHNIQUE(n) technique n { }\nTECHNIQUE(T)\nfloat4 P() : SV_TARGET { return 0; }\n";

        FxPreParser.StripTechniqueBlocks(source, "main.fx").ShouldBeSameAs(source);
    }
}
