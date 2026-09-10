#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using ShadowDusk.HLSL.Vkd3d;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Vkd3d;

/// <summary>
/// Tests for the cross-platform vkd3d-shader DXBC backend (Phase 18 Track A).
/// The binding is cross-platform; the live-compile tests are gated on the native
/// vkd3d-shader library being present (availability-probed via
/// <see cref="Vkd3dFactAttribute"/> — tools/restore provisions the per-RID binary,
/// Phase 37 C), so they run on every OS in CI. Tagged Integration because they
/// exercise native interop.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Vkd3dShaderCompilerTests
{
    private const string TexturedPixelShader = """
        Texture2D SpriteTexture;
        SamplerState SpriteTextureSampler;
        float4 TintColor;

        struct PSInput { float4 Position : SV_POSITION; float2 Tex : TEXCOORD0; };

        float4 MainPS(PSInput input) : SV_TARGET
        {
            return SpriteTexture.Sample(SpriteTextureSampler, input.Tex) * TintColor;
        }
        """;

    private static byte[] Dxbc4cc => Encoding.ASCII.GetBytes("DXBC");

    [Vkd3dFact]
    public async Task Compile_ProducesDxbcContainer()
    {
        var compiler = new Vkd3dShaderCompiler();

        var result = await compiler.CompileAsync(new D3DCompileRequest
        {
            HlslSource     = TexturedPixelShader,
            SourceFileName = "test.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
            AllowWarnings  = true,
        });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "vkd3d should compile a valid PS");
        result.Value.Kind.ShouldBe(BlobKind.Dxbc);
        result.Value.Bytes.Length.ShouldBeGreaterThan(4);
        // DXBC_TPF is a standard DXBC container — fourcc "DXBC".
        result.Value.Bytes.ToArray().Take(4).ShouldBe(Dxbc4cc);
    }

    [Vkd3dFact]
    public async Task Compile_InvalidSource_SurfacesDiagnosticNotSwallowed()
    {
        var compiler = new Vkd3dShaderCompiler();

        var result = await compiler.CompileAsync(new D3DCompileRequest
        {
            HlslSource     = "float4 MainPS() : SV_TARGET { return undeclared_symbol; }",
            SourceFileName = "bad.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
        });

        result.IsFailure.ShouldBeTrue();
        result.Error.Message.ShouldNotBeNullOrEmpty();
    }

    [Vkd3dFact]
    public async Task DxbcReflectionExtractor_ReflectsVkd3dOutput()
    {
        // Confirms the SAME DxbcReflectionExtractor (the pure-managed RdefReader since
        // Phase 18 Track A — runs on every OS) reflects vkd3d's DXBC_TPF output cleanly
        // — no separate reflector needed.
        var compiler = new Vkd3dShaderCompiler();
        var compileResult = await compiler.CompileAsync(new D3DCompileRequest
        {
            HlslSource     = TexturedPixelShader,
            SourceFileName = "test.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
            AllowWarnings  = true,
        });
        compileResult.IsSuccess.ShouldBeTrue(compileResult.IsFailure ? compileResult.Error.Message : "vkd3d should compile");

        var extractor = new DxbcReflectionExtractor();
        var reflectResult = extractor.Extract(compileResult.Value.Bytes);

        reflectResult.IsSuccess.ShouldBeTrue(reflectResult.IsFailure ? reflectResult.Error.Message : "the managed reader should accept vkd3d DXBC");
        var effect = reflectResult.Value;

        effect.Textures.Select(t => t.Name).ShouldContain("SpriteTexture");
        effect.Samplers.Select(s => s.Name).ShouldContain("SpriteTextureSampler");
        effect.ConstantBuffers.SelectMany(c => c.Variables).Select(v => v.Name)
            .ShouldContain("TintColor");
    }

    // -------------------------------------------------------------------------
    // Issue #202: diagnostics land on the author's line and column, not vkd3d's
    // -------------------------------------------------------------------------

    // Exactly the shape the FNA path hands this backend: the preprocessor's macro prelude
    // closed by a '#line 1 "user.fx"', then the user's file. The user's file carries every
    // measured drift of vkd3d 1.17: a skipped conditional arm (vkd3d drops those lines from
    // its count), template-implemented intrinsics (atan2 +20 each, sincos +4, asin +11 - vkd3d
    // lexes their templates against the user's line counter), and a body whose diagnostic is
    // therefore reported 50-odd lines past a 15-line file. See
    // plan/DONE/ISSUE-202-fna-error-line-numbers.md.
    private const string Issue202Prelude =
        "// ShadowDusk platform macros - DO NOT EDIT (generated)\n" +
        "#define FNA 1\n" +
        "#define HLSL 1\n" +
        "#define SM3 1\n" +
        "#line 1 \"user.fx\"\n";

    private static string Issue202User(string line13) => string.Join('\n', new[]
    {
        "#if OPENGL",                                                   // 1
        "#define VS_SHADERMODEL vs_3_0",                                // 2  skipped: OPENGL is not defined
        "#define PS_SHADERMODEL ps_3_0",                                // 3  skipped
        "#endif",                                                       // 4
        "float2 dir;",                                                  // 5
        "float4 PS(float2 uv : TEXCOORD0) : COLOR",                     // 6
        "{",                                                            // 7
        "    float a = atan2(uv.y, uv.x) + atan2(dir.y, dir.x);",       // 8  +40
        "    float s, c;",                                              // 9
        "    sincos(a, s, c);",                                         // 10 +4
        "    float b = asin(saturate(s));",                             // 11 +11
        "    int i = (int)(b * 4.0);",                                  // 12
        line13,                                                         // 13 the diagnostic
        "    return float4(b, a, s, 1.0);",                             // 14
        "}",                                                            // 15
    }) + "\n";

    private static Result<PlatformBlob, ShaderError> CompileIssue202(string source, string? profile = "ps_3_0") =>
        new Vkd3dShaderCompiler().Compile(new D3DCompileRequest
        {
            HlslSource      = source,
            SourceFileName  = "user.fx",
            EntryPoint      = "PS",
            Stage           = ShaderStage.Pixel,
            ProfileOverride = profile,
        });

    [Vkd3dFact]
    public void Compile_SyntaxError_IsReportedOnTheAuthorsLineAndColumn_Issue202()
    {
        // '    float z=;' : the ';' is source column 13; vkd3d counts it at 11 in its own
        // re-spaced 'float z = ;'.
        var result = CompileIssue202(Issue202Prelude + Issue202User("    float z=;"));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("E5000");
        result.Error.Message.ShouldBe("syntax error, unexpected ';'", customMessage: "vkd3d's own text, verbatim");
        result.Error.File.ShouldBe("user.fx");
        result.Error.Line.ShouldBe(13);
        result.Error.Column.ShouldBe(13);
        result.Error.RawDiagnostics.ShouldNotBeNull();
        result.Error.RawDiagnostics.ShouldContain(":71:11:", Case.Sensitive,
            "the raw text keeps vkd3d's own coordinates (13 + 5 prelude - 2 skipped + 40 + 4 + 11 = 71, column 11 of its re-spaced text), untouched");
    }

    [Vkd3dFact]
    public void Compile_CodegenError_IsReportedOnTheAuthorsLine_Issue202()
    {
        // The reporter's class: an int-typed ternary vkd3d 1.17 cannot lower at SM <= 3
        // (E5017 'SM1 cmp expression of type int'), raised from codegen after the whole
        // file parsed, so every template above it has already inflated the counter.
        var result = CompileIssue202(Issue202Prelude + Issue202User("    clip((uv.x < b) ? -1 : 1);"));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("E5017");
        result.Error.Message.ShouldContain("SM1 cmp expression of type int", Case.Sensitive);
        result.Error.File.ShouldBe("user.fx");
        result.Error.Line.ShouldBe(13);
        result.Error.Column.ShouldBeInRange(5, 30, "a token of the clip/ternary itself");
    }

    [Vkd3dFact]
    public void Compile_ErrorInsideAFlattenedInclude_NamesTheIncludeFile_Issue202()
    {
        // The include flattener plants '#line 1 "<include>"' where the #include stood and
        // '#line N "<user>"' after it; vkd3d never sees either (they are blanked) and names
        // user.fx for everything, so the include's own name and line must come back from
        // the directives.
        string source =
            Issue202Prelude +
            "float2 dir;\n" +                                              // user.fx:1
            "#line 1 \"shared/helpers.fxh\"\n" +                           // user.fx:2 was the #include
            "float Helper(float2 v) { return atan2(v.y, v.x); }\n" +       // helpers.fxh:1  +20
            "float Broken(float2 v) { return v.x + ; }\n" +                // helpers.fxh:2
            "#line 3 \"user.fx\"\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR { return Helper(uv) + Broken(uv); }\n";

        var result = CompileIssue202(source);

        result.IsFailure.ShouldBeTrue();
        result.Error.File.ShouldBe("shared/helpers.fxh");
        result.Error.Line.ShouldBe(2);
        result.Error.Column.ShouldBe(39, customMessage: "the ';' after 'v.x + '");
    }

    [Vkd3dFact]
    public void Compile_Sm5DxbcPath_SharesTheRelocation_Issue202()
    {
        // The same backend serves DirectX 11 (DXBC_TPF at SM5); a vkd3d-only rejection there
        // carried the same drifted coordinates.
        var result = CompileIssue202(
            (Issue202Prelude + Issue202User("    float z=;")).Replace(": COLOR", ": SV_TARGET"),
            profile: null);

        result.IsFailure.ShouldBeTrue();
        result.Error.File.ShouldBe("user.fx");
        result.Error.Line.ShouldBe(13);
        result.Error.Column.ShouldBe(13);
    }
}
