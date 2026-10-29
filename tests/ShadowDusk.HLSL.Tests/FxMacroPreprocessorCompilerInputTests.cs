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
    [InlineData("#ifndef _MY_HEADER_FXH\n#define _MY_HEADER_FXH\nfloat4 a;\n#endif\n")]
    [InlineData("#define __HLSL_VERSION 2018\n#if __HLSL_VERSION >= 2018\nfloat4 a;\n#endif\n")]
    [InlineData("#if OPENGL\nfloat4 a;\n#endif\n")]
    // A reserved name neither compiler defines is undefined to both, as it is to 0.22.1 and mgfxc
    // (the review of 2026-10-29: treating every such name as unknown broke real effects that test
    // _XBOX, _DEBUG or __PSSL__ on every target).
    [InlineData("#ifdef _XBOX\nfloat4 a;\n#endif\n")]
    [InlineData("#ifdef _DEBUG\nfloat4 a;\n#endif\n")]
    [InlineData("#if defined(__PSSL__) || defined(_PS4)\nfloat4 a;\n#endif\n")]
    [InlineData("#ifdef _WIN32\nfloat4 a;\n#endif\n")]
    [InlineData("#if defined(__x86_64__)\nfloat4 a;\n#endif\n")]
    [InlineData("#if __KNIFX__\nfloat4 a;\n#endif\n")]
    [InlineData("float4 a = __LINE__;\n")]
    public void OrdinaryConditionals_AndAPredefinedMacroInPlainText_AreNotReported(string src)
    {
        var result = FxMacroPreprocessor.ProcessForCompiler(src, "t.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.PredefinedMacroUse.ShouldBeNull();
    }

    // Measured on the pinned DXC (2026-10-29): it defines these too. Only the Vulkan register
    // pairing (SkippedLines) must know every line DXC may compile, so only its view reports them.
    // The compiler-input view (technique list, legacy-sampler recovery, initializer defaults) reads
    // them as undefined on every target, as mgfxc's preprocessor does and as 0.22.1 did.
    public static TheoryData<string, string> DxcOnlyConditionals() => new()
    {
        { "#ifdef __GNUC__\nfloat4 a;\n#endif\n", "__GNUC__" },
        { "#if __GNUC_MINOR__ > 1\nfloat4 a;\n#endif\n", "__GNUC_MINOR__" },
        { "#ifdef __GNUC_PATCHLEVEL__\nfloat4 a;\n#endif\n", "__GNUC_PATCHLEVEL__" },
        { "#ifdef __GXX_ABI_VERSION\nfloat4 a;\n#endif\n", "__GXX_ABI_VERSION" },
        { "#if __llvm__\nfloat4 a;\n#endif\n", "__llvm__" },
        { "#if __BYTE_ORDER__ == 1234\nfloat4 a;\n#endif\n", "__BYTE_ORDER__" },
        { "#ifdef __ORDER_LITTLE_ENDIAN__\nfloat4 a;\n#endif\n", "__ORDER_LITTLE_ENDIAN__" },
        { "#ifdef __LITTLE_ENDIAN__\nfloat4 a;\n#endif\n", "__LITTLE_ENDIAN__" },
        { "#ifdef __FLT_MAX__\nfloat4 a;\n#endif\n", "__FLT_MAX__" },
        { "#ifdef __DBL_MAX__\nfloat4 a;\n#endif\n", "__DBL_MAX__" },
        { "#ifdef __DECIMAL_DIG__\nfloat4 a;\n#endif\n", "__DECIMAL_DIG__" },
        { "#ifdef __is_identifier\nfloat4 a;\n#endif\n", "__is_identifier" },
        // A name DXC defines is reported even where the source defines it later.
        { "#ifndef __GNUC__\n#define __GNUC__ 0\n#endif\n", "__GNUC__" },
    };

    [Theory]
    [MemberData(nameof(DxcOnlyConditionals))]
    public void AMacroOnlyDxcDefines_IsReportedOnlyByTheVulkanPairingsView(string src, string macro)
    {
        var compilerInput = FxMacroPreprocessor.ProcessForCompiler(src, "t.fx");
        compilerInput.IsSuccess.ShouldBeTrue();
        compilerInput.Value.PredefinedMacroUse.ShouldBeNull();

        var skipped = FxMacroPreprocessor.SkippedLines(src, "t.fx");
        skipped.IsSuccess.ShouldBeTrue();
        skipped.Value.PredefinedMacroUse.ShouldNotBeNull();
        skipped.Value.PredefinedMacroUse!.Name.ShouldBe(macro);
        skipped.Value.PredefinedMacroUse.Line.ShouldBe(1);
    }

    [Theory]
    [InlineData("#if __HLSL_VERSION >= 2021\nfloat4 a;\n#endif\n", "__HLSL_VERSION")]
    [InlineData("#if __spirv__\nfloat4 a;\n#endif\n", "__spirv__")]
    [InlineData("#ifdef __clang__\nfloat4 a;\n#endif\n", "__clang__")]
    public void The0221List_IsReportedByBothViews(string src, string macro)
    {
        FxMacroPreprocessor.ProcessForCompiler(src, "t.fx").Value.PredefinedMacroUse!.Name.ShouldBe(macro);
        FxMacroPreprocessor.SkippedLines(src, "t.fx").Value.PredefinedMacroUse!.Name.ShouldBe(macro);
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
    public void SkippedLines_AreTheTextLinesOfUntakenConditionalGroups()
    {
        // The Vulkan binding rewrite reads these so a Sample call in '#if 0' (or the branch the
        // macros do not take) does not decide a texture's sampler.
        const string src =
            "#define VULKAN 1\n" +          // 1
            "Texture2D A;\n" +              // 2
            "#if 0\n" +                     // 3
            "return A.Sample(U, uv);\n" +   // 4  skipped
            "#endif\n" +                    // 5
            "#if VULKAN\n" +                // 6
            "return A.Sample(S, uv);\n" +   // 7
            "#else\n" +                     // 8
            "return A.Sample(T, \\\n" +     // 9  skipped (continued)
            "    uv);\n" +                  // 10 skipped
            "#endif\n" +                    // 11
            "#define M A.Sample(W, uv)\n";  // 12 a macro body is not skipped

        var result = FxMacroPreprocessor.SkippedLines(src, "t.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        result.Value.Lines.Order().ShouldBe([4, 9, 10]);
        result.Value.PredefinedMacroUse.ShouldBeNull();
    }

    [Fact]
    public void SkippedLines_ReturnTheExpandedViewLineForLine()
    {
        // The Vulkan binding rewrite reads what each function calls from this view: a call that
        // comes out of a macro is literal here, on the line the macro was used on.
        const string src =
            "#define DO_SAMPLE(uv) Helper(uv)\n" +                   // 1
            "float4 Helper(float2 uv) { return 1; }\n" +           // 2
            "float4 PS(float2 uv) { return DO_SAMPLE(uv); }\n";    // 3

        var result = FxMacroPreprocessor.SkippedLines(src, "t.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        string[] lines = result.Value.View.Split('\n');
        lines[0].Trim().ShouldBeEmpty();
        lines[2].ShouldContain("Helper(uv)", Case.Sensitive);
        lines[2].ShouldNotContain("DO_SAMPLE", Case.Sensitive);
    }

    [Fact]
    public void SkippedLines_ReportAConditionalOnACompilerPredefinedMacro()
    {
        var result = FxMacroPreprocessor.SkippedLines("#if __HLSL_VERSION >= 2021\nint a;\n#endif\n", "t.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.PredefinedMacroUse.ShouldNotBeNull().Name.ShouldBe("__HLSL_VERSION");
    }

    [Fact]
    public void AnUnterminatedConditional_StillFailsWithSd0009()
    {
        var result = FxMacroPreprocessor.ProcessForCompiler("#if 1\nfloat4 a;\n", "t.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0009");
    }
}
