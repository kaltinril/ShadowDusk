#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Integration.Tests;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #323, through real slangc on DirectX and OpenGL: two globals of one name in different
/// namespaces, which slangc v2026.14.1's <c>-no-mangle</c> output silently merges into one
/// (measured: <c>namespace A { Texture2D T; } namespace B { Texture2D T; }</c> comes back as ONE
/// <c>T</c> both reads use), or crashes on (two same-named constant-buffer members, exit
/// <c>0xC0000005</c>, nothing on stderr). ShadowDusk cannot fix slangc; it must refuse the
/// shape by name (<c>SD0643</c>) instead of handing the merged program downstream, and never let
/// the crashing shape reach slangc when the source shows it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangNamespaceCollisionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sd-slang-namespace-{Guid.NewGuid():N}");

    public SlangNamespaceCollisionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory is not a test failure.
        }
    }

    private string WriteModule(string fileName, string text)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, text.Replace("\r\n", "\n"));
        return path.Replace('\\', '/');
    }

    private const string TwoTextures =
        "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.T.Sample(S, uv) + B.T.Sample(S, uv); }\n";

    /// <summary>Compiles through real slangc, counting the slangc processes spawned.</summary>
    private static (Result<CompiledShader, ShaderError[]> Result, int SlangcRuns) Compile(string source, PlatformTarget target)
    {
        int runs = 0;
        var compiler = new SlangCompiler(
            new EffectCompiler(),
            () => SlangToolPath.GetUnsupportedReason() is { } reason
                ? new SlangCompiler.SlangcLocation(reason, null)
                : new SlangCompiler.SlangcLocation(null, SlangToolPath.Resolve()),
            SlangNativeCache.EnsureRunnableSlangc,
            (path, directory, text, arguments) =>
            {
                runs++;
                return SlangCompiler.RunSlangc(path, directory, text, arguments);
            });
        return (compiler.Compile(source, new CompilerOptions { Target = target, SourceFileName = "Names.slang" }), runs);
    }

    private static string Errors(Result<CompiledShader, ShaderError[]> result) =>
        result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "";

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void SameNamedTexturesInTwoNamespaces_FailAsSD0643_NamingBoth_WithoutRunningSlangc(PlatformTarget target)
    {
        const string source = "namespace A { Texture2D T; }\nnamespace B { Texture2D T; }\nSamplerState S;\n" + TwoTextures;

        var (result, runs) = Compile(source, target);

        result.IsFailure.ShouldBeTrue("slangc merges the two into one 'T' (measured); the compile must refuse");
        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Column, error.Code).ShouldBe(("Names.slang", 2, 25, "SD0643"));
        error.Message.ShouldContain("'A.T' (Names.slang:1:25)", Case.Sensitive);
        error.Message.ShouldContain("'B.T' (Names.slang:2:25)", Case.Sensitive);
        runs.ShouldBe(0);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void SameNamedConstantBufferMembers_TheShapeThatCrashesSlangc_FailAsSD0643_WithoutRunningSlangc(PlatformTarget target)
    {
        const string source =
            "namespace A { float4 Tint; }\nnamespace B { float4 Tint; }\n" +
            "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.Tint + B.Tint; }\n";

        var (result, runs) = Compile(source, target);

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.Line, error.Code).ShouldBe((2, "SD0643"));
        error.Message.ShouldContain("'A.Tint' (Names.slang:1:22)", Case.Sensitive);
        error.Message.ShouldContain("'B.Tint' (Names.slang:2:22)", Case.Sensitive);
        runs.ShouldBe(0);
    }

    public static TheoryData<string, string> MergedShapes() => new()
    {
        // Measured, each emitted as one declaration (see SlangcGlobalNameCollisions).
        { "namespace A { Texture2D T; }\nTexture2D T;\nSamplerState S;\n", "T" },
        { "namespace A.X { Texture2D T; }\nnamespace B::Y { Texture2D T; }\nSamplerState S;\n", "T" },
        { "namespace A { namespace X { Texture2D T; } }\nnamespace B { Texture2D T; }\nSamplerState S;\n", "T" },
        { "namespace A { Sampler2D Comb; }\nnamespace B { Sampler2D Comb; }\n", "Comb" },
        { "namespace A { SamplerState S; }\nnamespace B { SamplerState S; }\nTexture2D T;\n", "S" },
        { "struct M { Texture2D t; SamplerState s; };\nnamespace A { M gM; }\nnamespace B { M gM; }\n", "gM" },
        { "struct P { float4 Tint; };\nnamespace A { ParameterBlock<P> Blk; }\nnamespace B { ParameterBlock<P> Blk; }\n", "Blk" },
        { "namespace A { cbuffer C { float4 Tint; } }\nnamespace B { cbuffer C { float4 Fade; } }\n", "C" },
        { "namespace A { static const float4 K = float4(1, 0, 0, 1); }\nnamespace B { static const float4 K = float4(0, 1, 0, 1); }\n", "K" },
    };

    [Theory]
    [MemberData(nameof(MergedShapes))]
    public void EveryMergedShape_FailsAsSD0643(string declarations, string name)
    {
        // The entry point only has to be valid Slang; the check runs before slangc sees it.
        const string entry = "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return float4(uv, 0, 1); }\n";

        var (result, runs) = Compile(declarations + entry, PlatformTarget.OpenGL);

        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0643");
        error.Message.ShouldContain($"'{name}' is declared twice", Case.Sensitive);
        runs.ShouldBe(0);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void PairUnderADirective_IsConfirmedBySlangcsPreprocessor_OneRun(PlatformTarget target)
    {
        // '#define' means the raw text may not be what slangc compiles; one -E run confirms the
        // pair, before the compile that would crash on the constant-buffer shape.
        const string source =
            "#define SLOT register(t1)\nnamespace A { Texture2D T : SLOT; }\nnamespace B { Texture2D T; }\nSamplerState S;\n" + TwoTextures;

        var (result, runs) = Compile(source, target);

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Column, error.Code).ShouldBe(("Names.slang", 3, 25, "SD0643"));
        error.Message.ShouldContain("'A.T' (Names.slang:2:25)", Case.Sensitive);
        runs.ShouldBe(1);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void PairInExclusiveBranches_Compiles_AfterOnePreprocessRun(PlatformTarget target)
    {
        // Only one of the two is ever compiled for a target: the raw text shows a pair it cannot
        // judge, slangc's preprocessor clears it, and the shader compiles.
        const string source =
            "#if OPENGL\nnamespace A { Texture2D T; }\n#else\nnamespace B { Texture2D T; }\n#endif\nSamplerState S;\n" +
            "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target\n{\n#if OPENGL\n    return A.T.Sample(S, uv);\n#else\n    return B.T.Sample(S, uv);\n#endif\n}\n";

        var (result, runs) = Compile(source, target);

        result.IsSuccess.ShouldBeTrue(Errors(result));
        MgfxBlobReader.Parse(result.Value.Data).Parameters.Select(p => p.Name).ShouldContain("T");
        // The -E run and the compile; the register pass has nothing to ask.
        runs.ShouldBe(2);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void PairFormedThroughMacros_IsFoundInTheRegisterPassesText(PlatformTarget target)
    {
        // The raw text shows no pair (the namespaces come out of a macro). The source writes a
        // register, so the register pass reads slangc's -E text, which shows the pair.
        const string source =
            "#define DECL(ns, n) namespace ns { Texture2D n; }\nDECL(A, T)\nDECL(B, T)\nSamplerState S : register(s2);\n" + TwoTextures;

        var (result, runs) = Compile(source, target);

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Code).ShouldBe(("Names.slang", 0, "SD0643"));
        error.Message.ShouldContain("'A.T' (Names.slang)", Case.Sensitive);
        error.Message.ShouldContain("'B.T' (Names.slang)", Case.Sensitive);
        // The compile and the register pass's own -E run: nothing extra.
        runs.ShouldBe(2);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void PairAcrossAModuleAndTheEntry_IsFoundInTheModuleText(PlatformTarget target)
    {
        // Measured: 'namespace A { T }' in the module and 'namespace B { T }' in the entry come
        // back as one T. slangc numbers the module's sampler, so the register pass reads the
        // module, and the pair is found there.
        string module = WriteModule("mN.slang", """
            module mN;
            namespace A { public Texture2D T; public SamplerState MS; public float4 fetchA(float2 uv) { return T.Sample(MS, uv); } }
            """);
        string source = $"import \"{module}\";\nnamespace B {{ Texture2D T; }}\nSamplerState S;\n" +
                        "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.fetchA(uv) + B.T.Sample(S, uv); }\n";

        var (result, _) = Compile(source, target);

        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0643");
        error.Message.ShouldContain($"'A.T' ({module})", Case.Sensitive);
        error.Message.ShouldContain("'B.T' (Names.slang:2:25)", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void PlainGlobalsOfOneNameInTwoModules_AreFoundInTheModuleTexts(PlatformTarget target)
    {
        // Measured: two modules that each declare 'public Texture2D T' and use it only inside
        // themselves come back as ONE T (no namespace involved at all).
        string a = WriteModule("ma.slang", "module ma;\npublic Texture2D T;\npublic SamplerState SA;\npublic float4 fa(float2 uv) { return T.Sample(SA, uv); }\n");
        string b = WriteModule("mb.slang", "module mb;\npublic Texture2D T;\npublic SamplerState SB;\npublic float4 fb(float2 uv) { return T.Sample(SB, uv); }\n");
        string source = $"import \"{a}\";\nimport \"{b}\";\n[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {{ return fa(uv) + fb(uv); }}\n";

        var (result, _) = Compile(source, target);

        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0643");
        error.Message.ShouldContain($"'T' ({a})", Case.Sensitive);
        error.Message.ShouldContain($"'T' ({b})", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void DistinctNamesAcrossNamespaces_AndSameNamedFunctions_Compile_AtOneRun(PlatformTarget target)
    {
        // The control: namespaces are fine, and slangc keeps same-named FUNCTIONS apart on its
        // own (A_f_0 / B_f_0, measured). No run is spent on the question.
        const string source = """
            namespace A { Texture2D TA; float4 f(float2 uv) { return float4(uv, 0, 1); } }
            namespace B { Texture2D TB; float4 f(float2 uv) { return float4(0, uv, 1); } }
            SamplerState S;

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return A.TA.Sample(S, uv) * A.f(uv) + B.TB.Sample(S, uv) * B.f(uv);
            }
            """;

        var (result, runs) = Compile(source, target);

        result.IsSuccess.ShouldBeTrue(Errors(result));
        string[] names = MgfxBlobReader.Parse(result.Value.Data).Parameters.Select(p => p.Name).ToArray();
        names.ShouldContain("TA");
        names.ShouldContain("TB");
        runs.ShouldBe(1);
    }

    private const string PiEntry =
        "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position) : SV_Target {{ return float4({0}, 0, 1); }}\n";

    // Shapes that compile correctly today and must keep compiling, at no extra slangc run (the
    // owner's cost constraint): a future attempt at the issue #337 residue (a merged pair no read
    // text shows) must not refuse them. Verified with real slangc during the PR #383 review.
    public static TheoryData<string> MustCompileShapes() => ["P1", "P2", "P3", "P8", "P12"];

    [Theory]
    [MemberData(nameof(MustCompileShapes))]
    public void SameNamedModuleConstantsThatCompileCorrectly_StillCompile_AtOneRun(string shape)
    {
        string source;
        switch (shape)
        {
            case "P1": // two modules, each with a private 'static const float PI', both used
            case "P2": // the same, one module's function never called
            {
                string a = WriteModule($"{shape}a.slang", "module " + shape + "a;\nstatic const float PI = 3.14159;\npublic float fa() { return PI; }\n");
                string b = WriteModule($"{shape}b.slang", "module " + shape + "b;\nstatic const float PI = 3.14159;\npublic float fb() { return PI * 2; }\n");
                source = $"import \"{a}\";\nimport \"{b}\";\n" + string.Format(PiEntry, shape == "P1" ? "fa(), fb()" : "fa(), 0");
                break;
            }
            case "P3": // two modules that each #include one header holding 'static const float PI'
            {
                WriteModule("common.h", "static const float PI = 3.14159;\n");
                string a = WriteModule("p3a.slang", "module p3a;\n#include \"common.h\"\npublic float fa() { return PI; }\n");
                string b = WriteModule("p3b.slang", "module p3b;\n#include \"common.h\"\npublic float fb() { return PI * 2; }\n");
                source = $"import \"{a}\";\nimport \"{b}\";\n" + string.Format(PiEntry, "fa(), fb()");
                break;
            }
            case "P8": // a link-time constant: 'extern static const' in one module, 'export' in another
            {
                string a = WriteModule("p8a.slang", "module p8a;\nextern static const int kCount;\npublic float countA() { return kCount; }\n");
                string b = WriteModule("p8b.slang", "module p8b;\nexport static const int kCount = 4;\n");
                source = $"import \"{a}\";\nimport \"{b}\";\n" + string.Format(PiEntry, "countA(), 0");
                break;
            }
            default: // P12: both modules with 'namespace Detail { static const float EPS = 1e-5; }'
            {
                string a = WriteModule("p12a.slang", "module p12a;\nnamespace Detail { static const float EPS = 1e-5; }\npublic float fa() { return Detail::EPS; }\n");
                string b = WriteModule("p12b.slang", "module p12b;\nnamespace Detail { static const float EPS = 1e-5; }\npublic float fb() { return Detail::EPS * 2; }\n");
                source = $"import \"{a}\";\nimport \"{b}\";\n" + string.Format(PiEntry, "fa(), fb()");
                break;
            }
        }

        var (result, runs) = Compile(source, PlatformTarget.OpenGL);

        result.IsSuccess.ShouldBeTrue(Errors(result));
        runs.ShouldBe(1);
    }

    [Fact]
    public void PairNoTextShows_StillFailsLoudly_AsSlangcsCrashWithTheKnownTrigger()
    {
        // The residue: a constant-buffer pair formed by a macro, in a source that writes no
        // register (so no -E text is ever read). slangc crashes on it (measured, 0xC0000005 on
        // Windows); the failure names the crash, the exit code and this trigger, never a bare
        // "no diagnostic output".
        const string source =
            "#define DECL(ns, n) namespace ns { float4 n; }\nDECL(A, Tint)\nDECL(B, Tint)\n" +
            "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.Tint + B.Tint; }\n";

        var (result, _) = Compile(source, PlatformTarget.DirectX);

        result.IsFailure.ShouldBeTrue("a silently merged or crashed shape must not compile");
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0622");
        error.Message.ShouldContain("terminated abnormally", Case.Sensitive);
        error.Message.ShouldContain("SD0643", Case.Sensitive);
        // slangc's own words before it died (its two 'implicit global shader parameter'
        // warnings, measured) are kept verbatim.
        error.RawDiagnostics.ShouldNotBeNull().ShouldContain("warning[E39019]", Case.Sensitive);
    }
}
