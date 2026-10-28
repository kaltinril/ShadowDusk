#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Lexer;
using ShadowDusk.HLSL.Preprocessing;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #426: a block comment that spans lines inside a <c>#define</c> (or any directive) is
/// whitespace and does not end the directive. The managed preprocessed view ended the directive at
/// the comment, so the macro lost the rest of its body and that rest spilled into the code in front
/// of the next declaration, where the initializer reader (issue #422) and the OpenGL sampler-register
/// reading (issues #283/#299) both read it. DXC itself always compiled the raw source correctly;
/// only the decisions ShadowDusk makes on the view were wrong. DXC's <c>-P</c> is the oracle for
/// what the view must be.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue426BlockCommentInDirectiveTests
{
    private const string ShaderModels =
        "#if SM6\n#define PS_SHADERMODEL ps_6_0\n#elif SM4\n#define PS_SHADERMODEL ps_4_0\n#else\n" +
        "#define PS_SHADERMODEL ps_3_0\n#endif\n";

    // ---- the view is what DXC's preprocessor makes of the same source -------------------------

    public static TheoryData<string> Shapes => new()
    {
        "#define SCALE /* the scale,\n   in pixels */ 2\nfloat R = SCALE;\nfloat G = 3;\n",
        "#define A 1 /* one\n two\n three */\n#define B /* x\n*/ A + 1\nfloat4 v = B;\n  float4 w;\n",
        "#define MUL(a, b) (a) /* times\n */ * (b)\nfloat R = MUL(2, 3);\n",
        "#if 0 /* off,\n */ || 1\nfloat4 on;\n#endif\n",
        "#define A 1 + \\\n 2 /* c\n */ + 3\nfloat R = A;\n",
        "/* a\n b */ #define A 7\nfloat R = A;\n",
        "float4 a; /* c\n */ float4 b;\nfloat4 c;\n",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheManagedView_MatchesDxcPreprocess_TokenForToken(string source)
    {
        using var dxc = new DxcShaderCompiler();
        var theirs = dxc.Preprocess(new DxcPreprocessRequest { HlslSource = source, SourceFileName = "t.fx", Macros = [] });
        theirs.IsSuccess.ShouldBeTrue(theirs.IsFailure ? theirs.Error.Message : "ok");

        var compilerInput = FxMacroPreprocessor.ProcessForCompiler(source, "t.fx");
        var plain = FxMacroPreprocessor.Process(source, "t.fx");
        compilerInput.IsSuccess.ShouldBeTrue();
        plain.IsSuccess.ShouldBeTrue();

        string[] expected = CodeTokens(theirs.Value);
        CodeTokens(plain.Value).ShouldBe(expected);
        CodeTokens(compilerInput.Value.Text).ShouldBe(expected);
    }

    // DXC (clang) anchors '#line N' at the physical line of its NUMBER: the line after THAT one is
    // N, however many lines the rest of the directive spans. Measured with DXC -P (__LINE__).
    [Theory]
    [InlineData("#line 10 /* x\n */ \"main.fx\"\nfloat4 a = __LINE__;\n", "11")]
    [InlineData("#line /* x\n */ 10 \"main.fx\"\nfloat4 a = __LINE__;\n", "10")]
    [InlineData("#line 10 \\\n \"main.fx\"\nfloat4 a = __LINE__;\n", "11")]
    [InlineData("#line 10 /* x\n y\n */ \"main.fx\"\nfloat4 a = __LINE__;\n", "12")]
    [InlineData("#line 10 \"main.fx\"\nfloat4 a = __LINE__;\n", "10")]
    public void ALineDirectiveSpanningLines_NumbersTheNextLineAsDxcDoes(string source, string expectedLine)
    {
        using var dxc = new DxcShaderCompiler();
        var theirs = dxc.Preprocess(new DxcPreprocessRequest { HlslSource = source, SourceFileName = "t.fx", Macros = [] });
        theirs.IsSuccess.ShouldBeTrue();
        CodeTokens(theirs.Value).ShouldBe(new[] { "float4", "a", "=", expectedLine, ";" }, "DXC's own numbering");

        // The compiler input, compiled, numbers the line the same.
        string ours = FxMacroPreprocessor.ProcessForCompiler(source, "t.fx").Value.Text;
        var reparsed = dxc.Preprocess(new DxcPreprocessRequest { HlslSource = ours, SourceFileName = "t.fx", Macros = [] });
        reparsed.IsSuccess.ShouldBeTrue();
        CodeTokens(reparsed.Value).ShouldBe(new[] { "float4", "a", "=", expectedLine, ";" }, "the compiler input's numbering");

        // And so does the managed pass's own diagnostic line, for a stray #else on that line.
        string stray = source.Replace("float4 a = __LINE__;", "#else", StringComparison.Ordinal);
        FxMacroPreprocessor.Process(stray, "t.fx").Error.Line.ShouldBe(int.Parse(expectedLine, System.Globalization.CultureInfo.InvariantCulture));
        FxMacroPreprocessor.ProcessForCompiler(stray, "t.fx").Error.Line.ShouldBe(int.Parse(expectedLine, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string[] CodeTokens(string text) =>
        new FxLexer(text, "t.fx").Tokenize()
            .Where(t => t.Kind is not (TokenKind.Preprocessor or TokenKind.LineComment or TokenKind.BlockComment or TokenKind.EOF))
            .Select(t => t.Text)
            .ToArray();

    // ---- end to end: the compile no longer loses what the view hid ----------------------------

    private static async Task<CompiledShader> CompileAsync(string source, PlatformTarget target)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            SourceFileName = "Issue426.fx",
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))
            : "must compile");
        return result.Value;
    }

    // The issue's shape: the declaration after the multi-line comment in a #define. Its twin
    // writes the same macro on one line and pads the lost line, so every line keeps its number.
    private static string DefaultsSource(bool commented) =>
        ShaderModels +
        (commented ? "#define SCALE /* the scale,\n   in pixels */ 2\n" : "#define SCALE 2\n\n") +
        "float R = 1;\n" +
        "float S = SCALE;\n" +
        "float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0) : SV_Target0 { return color * R * S; }\n" +
        "technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task TheDeclarationAfterTheDefine_KeepsItsDefault(PlatformTarget target)
    {
        CompiledShader shader = await CompileAsync(DefaultsSource(commented: true), target);

        shader.Parameters.Single(p => p.Name == "R").DefaultValue.ShouldBe(new[] { 1f });
        shader.Parameters.Single(p => p.Name == "S").DefaultValue.ShouldBe(new[] { 2f });

        CompiledShader twin = await CompileAsync(DefaultsSource(commented: false), target);
        shader.Data.ShouldBe(twin.Data, "the commented #define compiles to the bytes of its one-line twin");
    }

    [FnaFact]
    public async Task TheDeclarationAfterTheDefine_KeepsItsDefault_DirectX11()
    {
        CompiledShader shader = await CompileAsync(DefaultsSource(commented: true), PlatformTarget.DirectX);

        shader.Parameters.Single(p => p.Name == "R").DefaultValue.ShouldBe(new[] { 1f });
        shader.Parameters.Single(p => p.Name == "S").DefaultValue.ShouldBe(new[] { 2f });

        CompiledShader twin = await CompileAsync(DefaultsSource(commented: false), PlatformTarget.DirectX);
        shader.Data.ShouldBe(twin.Data);
    }

    // OpenGL: 'SamplerState S : register(s0)' reserves unit 0 (issue #309), whatever spells the
    // register. Through the commented macro the view saw an empty 'register()' and no reservation.
    private static string SamplerSource(bool commented) =>
        ShaderModels +
        (commented ? "#define SLOT /* the first\n   unit */ s0\n" : "#define SLOT s0\n\n") +
        "Texture2D Tex;\n" +
        "SamplerState S : register(SLOT);\n" +
        "float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 { return Tex.Sample(S, uv); }\n" +
        "technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task ARegisterSpelledThroughTheDefine_StillReservesItsOpenGlUnit()
    {
        CompiledShader shader = await CompileAsync(SamplerSource(commented: true), PlatformTarget.OpenGL);
        CompiledShader twin = await CompileAsync(SamplerSource(commented: false), PlatformTarget.OpenGL);

        shader.Data.ShouldBe(twin.Data);
    }
}
