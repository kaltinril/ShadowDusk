#nullable enable

using ShadowDusk.HLSL.Preprocessing;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// Issue #308: the preprocessed view in its COMPILER-INPUT form (<see
/// cref="FxMacroPreprocessor.ProcessForCompiler"/>), the text the legacy-sampler recovery hands
/// to DXC. The contract is layout: one output line per input line, so every token keeps its line,
/// the flattener's <c>#line</c> directives keep mapping it to the author's file, and a line no
/// macro touched keeps its columns. Pure tests: the input is a hand-written flattened source.
/// </summary>
public sealed class FxMacroPreprocessorCompilerInputTests
{
    private static string View(string flattened)
    {
        var result = FxMacroPreprocessor.ProcessForCompiler(flattened, "t.fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        return result.Value.Text;
    }

    private static string[] Lines(string text) => text.Split('\n');

    /// <summary>Whitespace runs collapsed: an expansion is padded with a space on each side.</summary>
    private static string Spaced(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ").Trim();

    [Fact]
    public void KeepsOneOutputLinePerInputLine()
    {
        const string src = """
            #define OPENGL 1
            #line 1 "main.fx"
            #if OPENGL
            sampler S : register(s1);
            #else
            Texture2D T;
            #endif
            float4 PS() : COLOR0 { return tex2D(S, 0); }
            """;

        string[] lines = Lines(View(src));

        lines.Length.ShouldBe(8);
        lines[0].ShouldBe("");                                   // #define
        lines[1].ShouldBe("#line 1 \"main.fx\"");                  // passed through verbatim
        lines[2].ShouldBe("");                                   // #if
        lines[3].Trim().ShouldBe("sampler S : register(s1);");
        lines[4].ShouldBe("");                                   // #else
        lines[5].ShouldBe("");                                   // the inactive branch
        lines[6].ShouldBe("");                                   // #endif
        lines[7].Trim().ShouldBe("float4 PS() : COLOR0 { return tex2D(S, 0); }");
    }

    [Fact]
    public void AMacroExpansionStaysOnTheLineItWasInvokedOn()
    {
        const string src = """
            #define DECLARE_TEXTURE(Name, index) sampler2D Name : register(s##index)
            #define SAMPLE_TEXTURE(Name, uv) tex2D(Name, uv)
            DECLARE_TEXTURE(S, 1);
            float4 PS(float2 uv : TEXCOORD0) : COLOR0
            {
                return SAMPLE_TEXTURE(S, uv);
            }
            """;

        string[] lines = Lines(View(src));

        lines.Length.ShouldBe(7);
        Spaced(lines[2]).ShouldBe("sampler2D S : register(s1) ;");
        Spaced(lines[5]).ShouldBe("return tex2D(S, uv) ;");
        lines[6].ShouldBe("}");
    }

    [Fact]
    public void AMultiLineDefineAndAMultiLineInvocation_LeaveLaterLinesWhereTheyWere()
    {
        const string src = """
            #define DECL(Name, index) \
                sampler2D Name : register(s##index);
            DECL(A,
                 2)
            float4 after;
            """;

        string[] lines = Lines(View(src));

        lines.Length.ShouldBe(5);
        lines[0].ShouldBe("");
        lines[1].ShouldBe("");                                   // the continuation line of the #define
        lines[2].Trim().ShouldStartWith("sampler2D A");
        lines[4].Trim().ShouldBe("float4 after;", customMessage: "the line after a two-line invocation must not move");
    }

    [Fact]
    public void WhitespaceAndCommentWidthArePreserved_SoColumnsHold()
    {
        const string src = "    float4 c; /* comment */ float4 d; // trailing\n\tfloat4 e;\n";

        string[] lines = Lines(View(src));

        lines[0].ShouldStartWith("    float4 c;", customMessage: "leading whitespace is kept as written");
        lines[0].IndexOf("float4 d;", StringComparison.Ordinal).ShouldBe(
            src.IndexOf("float4 d;", StringComparison.Ordinal),
            customMessage: "a block comment becomes spaces of its own width");
        lines[1].ShouldStartWith("\tfloat4 e;");
    }

    [Fact]
    public void PragmaErrorAndWarningDirectives_PassThroughVerbatim_DefinesDoNot()
    {
        const string src = """
            #pragma pack_matrix(row_major)
            #define X 1
            #if X
            #error active
            #else
            #error inactive
            #endif
            #warning w
            """;

        string[] lines = Lines(View(src));

        lines[0].ShouldBe("#pragma pack_matrix(row_major)");
        lines[1].ShouldBe("");
        lines[3].ShouldBe("#error active");
        lines[5].ShouldBe("", customMessage: "a directive inside an inactive group is not the compiler's to see");
        lines[7].ShouldBe("#warning w");
    }

    [Fact]
    public void LineDirectiveInsideAnInactiveGroup_IsNotEmitted()
    {
        // The flattener inlines an #include even inside a dead branch; DXC skips its #line
        // directives there, and so must the compiler input.
        const string src = """
            #if 0
            #line 1 "dead.fxh"
            float4 dead;
            #line 4 "main.fx"
            #endif
            float4 alive;
            """;

        string[] lines = Lines(View(src));

        lines[1].ShouldBe("");
        lines[3].ShouldBe("");
        lines[5].ShouldBe("float4 alive;");
    }

    [Theory]
    [InlineData("#if __HLSL_VERSION >= 2021\nfloat4 a;\n#endif\n", "__HLSL_VERSION")]
    [InlineData("#ifdef __hlsl_dx_compiler\nfloat4 a;\n#endif\n", "__hlsl_dx_compiler")]
    [InlineData("#if defined(__SHADER_TARGET_MAJOR) && __SHADER_TARGET_MAJOR > 5\nfloat4 a;\n#endif\n", "__SHADER_TARGET_MAJOR")]
    [InlineData("#if __spirv__\nfloat4 a;\n#endif\n", "__spirv__")]
    public void AConditionalOnACompilerPredefinedMacro_IsReported(string src, string macro)
    {
        var result = FxMacroPreprocessor.ProcessForCompiler(src, "t.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.PredefinedMacroUse.ShouldNotBeNull();
        result.Value.PredefinedMacroUse!.Name.ShouldBe(macro);
        result.Value.PredefinedMacroUse.Line.ShouldBe(1);
        result.Value.PredefinedMacroUse.File.ShouldBe("t.fx");
    }

    [Theory]
    [InlineData("#ifndef __MY_HEADER_FXH__\n#define __MY_HEADER_FXH__\nfloat4 a;\n#endif\n")]
    [InlineData("#define __HLSL_VERSION 2018\n#if __HLSL_VERSION >= 2018\nfloat4 a;\n#endif\n")]
    [InlineData("#if OPENGL\nfloat4 a;\n#endif\n")]
    [InlineData("float4 a = __LINE__;\n")]
    public void OrdinaryConditionals_AndAPredefinedMacroInPlainText_AreNotReported(string src)
    {
        var result = FxMacroPreprocessor.ProcessForCompiler(src, "t.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.PredefinedMacroUse.ShouldBeNull();
    }

    [Fact]
    public void ThePlainView_IsUnchangedByTheCompilerInputMode()
    {
        // The slot-deciding view (issues #283/#299) keeps its exact text: no padding lines, no
        // directives, whitespace runs collapsed, comments gone.
        const string src = "#define X(a) a a\n/* c */  X(1); // t\n#pragma once\n#if 0\nno\n#endif\n";

        var plain = FxMacroPreprocessor.Process(src, "t.fx");

        plain.IsSuccess.ShouldBeTrue();
        plain.Value.Trim().ShouldBe("1 1 ;");
        plain.Value.ShouldNotContain("#pragma", Case.Sensitive);
    }

    [Fact]
    public void AnUnterminatedConditional_StillFailsWithSd0009()
    {
        var result = FxMacroPreprocessor.ProcessForCompiler("#if 1\nfloat4 a;\n", "t.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0009");
    }
}
