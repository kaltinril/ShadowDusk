#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;
using Transport = ShadowDusk.Slang.Tests.SlangcTransport;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #323: the wiring that refuses two same-named globals slangc's <c>-no-mangle</c> output
/// merges, driven through a FAKE slangc on both transports (the in-process seam the browser uses
/// and the process seam), so the browser route is pinned to the desktop's behaviour on every host
/// with no disk and no process, and so is what each shape COSTS in slangc runs. The emissions and
/// <c>-E</c> texts are slangc v2026.14.1's, verbatim in shape. The detector itself is
/// <see cref="SlangcGlobalNameCollisionsTests"/>; the real-slangc proof is
/// <see cref="SlangNamespaceCollisionTests"/>.
/// </summary>
public sealed class SlangNamespaceCollisionPassTests
{
    private const string PixelShader =
        "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.T.Sample(S, uv) + B.T.Sample(S, uv); }\n";

    private const string PixelShaderPreprocessed =
        "[ shader ( \"fragment\" ) ] float4 MainPS ( float4 pos : SV_Position , float2 uv : TEXCOORD0 ) : SV_Target { return A . T . Sample ( S , uv ) + B . T . Sample ( S , uv ) ; } \n";

    // What slangc emits for the merged pair (measured): ONE T, located at the first declaration.
    private const string MergedEmission = """
        #pragma pack_matrix(column_major)

        #line 1 "<stdin>"
        Texture2D<float4 > T : register(t1);

        SamplerState S : register(s0);

        float4 MainPS(float4 pos_0 : SV_Position, float2 uv_0 : TEXCOORD0) : SV_TARGET
        {

        #line 6
            return T.Sample(S, uv_0) + T.Sample(S, uv_0);
        }

        """;

    private static CompilerOptions Options(PlatformTarget target, params UserDefine[] defines) =>
        new() { Target = target, SourceFileName = "Entry.slang", Defines = defines };

    private static string Errors(Result<CompiledShader, ShaderError[]> result) =>
        result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "";

    [Theory]
    [InlineData(Transport.InProcess, PlatformTarget.DirectX)]
    [InlineData(Transport.InProcess, PlatformTarget.OpenGL)]
    [InlineData(Transport.Process, PlatformTarget.DirectX)]
    [InlineData(Transport.Process, PlatformTarget.OpenGL)]
    public void SameNamedGlobalsInTwoNamespaces_FailAsSD0643_BeforeSlangcRuns(Transport transport, PlatformTarget target)
    {
        const string source = "namespace A { Texture2D T; }\nnamespace B { Texture2D T; }\nSamplerState S;\n" + PixelShader;
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(MergedEmission, "");

        var result = slangc.Compiler(transport, downstream).Compile(source, Options(target));

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Column, error.Code).ShouldBe(("Entry.slang", 2, 25, "SD0643"));
        error.Message.ShouldContain("'A.T' (Entry.slang:1:25)", Case.Sensitive);
        error.Message.ShouldContain("'B.T' (Entry.slang:2:25)", Case.Sensitive);
        // The raw text is what slangc would compile (no directive, no -D), so it decides alone.
        slangc.Calls.ShouldBeEmpty();
        downstream.Captured.ShouldBeNull();
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void SameNamedConstantBufferMembers_TheShapeThatCrashesSlangc_NeverReachIt(Transport transport)
    {
        const string source =
            "namespace A { float4 Tint; }\nnamespace B { float4 Tint; }\n" +
            "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.Tint + B.Tint; }\n";
        // Were slangc run, it would crash: 0xC0000005 and nothing on stderr (measured).
        var slangc = new ScriptedSlangc("", "", compileExitCode: -1073741819);

        var result = slangc.Compiler(transport, new ScriptedSlangc.CapturingCompiler()).Compile(source, Options(PlatformTarget.OpenGL));

        result.Error.ShouldHaveSingleItem().Code.ShouldBe("SD0643");
        slangc.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void PairTheRawTextShows_UnderADirective_IsConfirmedBySlangcsPreprocessor_Once(Transport transport)
    {
        // A '#define' means the raw text may not be what slangc compiles: one -E run decides, and
        // the declarations are still located from the raw text.
        const string source = "#define SLOT register(t1)\nnamespace A { Texture2D T : SLOT; }\nnamespace B { Texture2D T; }\nSamplerState S;\n" + PixelShader;
        var slangc = new ScriptedSlangc(MergedEmission,
            "namespace A { Texture2D T : register ( t1 ) ; } namespace B { Texture2D T ; } SamplerState S ; " + PixelShaderPreprocessed);

        var result = slangc.Compiler(transport, new ScriptedSlangc.CapturingCompiler()).Compile(source, Options(PlatformTarget.DirectX));

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Column, error.Code).ShouldBe(("Entry.slang", 3, 25, "SD0643"));
        error.Message.ShouldContain("'A.T' (Entry.slang:2:25)", Case.Sensitive);
        slangc.Calls.Count.ShouldBe(1);
        slangc.PreprocessInputs.ShouldBe([["-"]]);
    }

    [Theory]
    [InlineData(Transport.InProcess, PlatformTarget.DirectX)]
    [InlineData(Transport.Process, PlatformTarget.OpenGL)]
    public void PairInExclusiveBranches_IsClearedByThePreprocessor_AndCompiles(Transport transport, PlatformTarget target)
    {
        const string source =
            "#if OPENGL\nnamespace A { Texture2D T; }\n#else\nnamespace B { Texture2D T; }\n#endif\nSamplerState S;\n" +
            "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return T.Sample(S, uv); }\n";
        const string emission = "#line 2 \"<stdin>\"\nTexture2D<float4 > T : register(t0);\n#line 6\nSamplerState S : register(s0);\n#line 8\nfloat4 MainPS(float4 pos_0 : SV_Position, float2 uv_0 : TEXCOORD0) : SV_TARGET\n{\n    return T.Sample(S, uv_0);\n}\n";
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(emission,
            "namespace A { Texture2D T ; } SamplerState S ; [ shader ( \"fragment\" ) ] float4 MainPS ( float4 pos : SV_Position , float2 uv : TEXCOORD0 ) : SV_Target { return T . Sample ( S , uv ) ; } \n");

        var result = slangc.Compiler(transport, downstream).Compile(source, Options(target));

        result.IsSuccess.ShouldBeTrue(Errors(result));
        downstream.Captured.ShouldNotBeNull().ShouldContain("Texture2D<float4 > T;", Case.Sensitive);
        // The -E run, then the compile: the register pass has nothing to ask (no register is written).
        slangc.Calls.Count.ShouldBe(2);
        slangc.Calls[0].ShouldContain("-E");
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void PreprocessorErrorInTheConfirmationRun_IsLeftToTheCompile_NeverJudged(Transport transport)
    {
        // -E exits 0 even when the preprocessor reports an error (measured); its text is then
        // not trusted, and the compile that follows reports slangc's own error verbatim.
        const string source = "#define SLOT register(t1)\n#error no\nnamespace A { Texture2D T : SLOT; }\nnamespace B { Texture2D T; }\nSamplerState S;\n" + PixelShader;
        var slangc = new ErrorPreprocessSlangc(
            "error[E10001]: user-defined error: no\n --> <stdin>:2:8\n",
            "namespace A { Texture2D T : register ( t1 ) ; } namespace B { Texture2D T ; } SamplerState S ; " + PixelShaderPreprocessed);

        var result = slangc.Compiler(transport).Compile(source, Options(PlatformTarget.DirectX));

        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("E10001");
        error.Message.ShouldContain("user-defined error: no", Case.Sensitive);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void PairFormedThroughMacros_IsFoundInTheRegisterPassesText_AtNoExtraRun(Transport transport)
    {
        // The raw text shows no pair (the namespaces come out of a macro), so slangc compiles;
        // the register pass then reads the entry's -E text for a register the source writes,
        // and that text shows the pair. No run beyond the one the register pass already paid.
        const string source =
            "#define DECL(ns, n) namespace ns { Texture2D n; }\nDECL(A, T)\nDECL(B, T)\nSamplerState S : register(s2);\n" + PixelShader;
        const string emission = "#line 2 \"<stdin>\"\nTexture2D<float4 > T : register(t1);\n#line 4\nSamplerState S : register(s2);\n";
        var slangc = new ScriptedSlangc(emission,
            "namespace A { Texture2D T ; } namespace B { Texture2D T ; } SamplerState S : register ( s2 ) ; " + PixelShaderPreprocessed);

        var result = slangc.Compiler(transport, new ScriptedSlangc.CapturingCompiler()).Compile(source, Options(PlatformTarget.OpenGL));

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Code).ShouldBe(("Entry.slang", 0, "SD0643"));
        error.Message.ShouldContain("'A.T' (Entry.slang)", Case.Sensitive);
        slangc.PreprocessInputs.ShouldBe([["-"]]);
        slangc.Calls.Count.ShouldBe(2);
    }

    private const string ModulePath = "C:/shaders/mN.slang";

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void PairAcrossAModuleAndTheEntry_IsFoundInTheModuleTextTheRegisterPassRead(Transport transport)
    {
        // namespace A in the module, namespace B in the entry: slangc merges them (measured). The
        // module's sampler is numbered, so the register pass reads the module, and the pair is
        // found in that text with the module named.
        string source = $"import \"{ModulePath}\";\nnamespace B {{ Texture2D T; }}\nSamplerState S;\n" +
                        "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.fetchA(uv) + B.T.Sample(S, uv); }\n";
        string emission = $"#line 2 \"<stdin>\"\nTexture2D<float4 > T : register(t1);\n#line 2 \"{ModulePath}\"\nSamplerState MS : register(s1);\n#line 3 \"<stdin>\"\nSamplerState S : register(s0);\n";
        var slangc = new ScriptedSlangc(emission,
            $"import \"{ModulePath}\" ; namespace B {{ Texture2D T ; }} SamplerState S ; [ shader ( \"fragment\" ) ] float4 MainPS ( float4 pos : SV_Position , float2 uv : TEXCOORD0 ) : SV_Target {{ return A . fetchA ( uv ) + B . T . Sample ( S , uv ) ; }} \n",
            new Dictionary<string, string>
            {
                [ModulePath] = "module mN ; namespace A { public Texture2D T ; public SamplerState MS ; public float4 fetchA ( float2 uv ) { return T . Sample ( MS , uv ) ; } } \n",
            });

        var result = slangc.Compiler(transport, new ScriptedSlangc.CapturingCompiler()).Compile(source, Options(PlatformTarget.DirectX));

        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0643");
        error.Message.ShouldContain($"'A.T' ({ModulePath})", Case.Sensitive);
        error.Message.ShouldContain("'B.T' (Entry.slang:2:25)", Case.Sensitive);
        (error.File, error.Line, error.Column).ShouldBe(("Entry.slang", 2, 25));
        slangc.PreprocessInputs.ShouldBe([["-", ModulePath]]);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void PlainGlobalsOfOneNameInTwoModules_AreFoundInTheModuleTexts(Transport transport)
    {
        const string a = "C:/shaders/ma.slang";
        const string b = "C:/shaders/mb.slang";
        string source = $"import \"{a}\";\nimport \"{b}\";\n[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {{ return fa(uv) + fb(uv); }}\n";
        string emission = $"#line 2 \"{a}\"\nTexture2D<float4 > T : register(t1);\n#line 3\nSamplerState SA : register(s0);\n#line 3 \"{b}\"\nSamplerState SB : register(s1);\n";
        var slangc = new ScriptedSlangc(emission,
            $"import \"{a}\" ; import \"{b}\" ; [ shader ( \"fragment\" ) ] float4 MainPS ( float4 pos : SV_Position , float2 uv : TEXCOORD0 ) : SV_Target {{ return fa ( uv ) + fb ( uv ) ; }} \n",
            new Dictionary<string, string>
            {
                [a] = "module ma ; public Texture2D T ; public SamplerState SA ; public float4 fa ( float2 uv ) { return T . Sample ( SA , uv ) ; } \n",
                [b] = "module mb ; public Texture2D T ; public SamplerState SB ; public float4 fb ( float2 uv ) { return T . Sample ( SB , uv ) ; } \n",
            });

        var result = slangc.Compiler(transport, new ScriptedSlangc.CapturingCompiler()).Compile(source, Options(PlatformTarget.OpenGL));

        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0643");
        error.Message.ShouldContain($"'T' ({a})", Case.Sensitive);
        error.Message.ShouldContain($"'T' ({b})", Case.Sensitive);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void SlangcCrashWithNoStderr_IsSD0622_NamingTheCrashAndTheKnownTrigger(Transport transport)
    {
        // The residue: a pair the raw text cannot show (formed by a macro) in a source the
        // register pass never preprocesses. slangc crashes; the failure is still loud.
        const string source =
            "#define DECL(ns, n) namespace ns { float4 n; }\nDECL(A, Tint)\nDECL(B, Tint)\n" +
            "[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.Tint + B.Tint; }\n";
        var slangc = new ScriptedSlangc("", "", compileExitCode: -1073741819);

        var result = slangc.Compiler(transport, new ScriptedSlangc.CapturingCompiler()).Compile(source, Options(PlatformTarget.DirectX));

        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0622");
        error.Message.ShouldContain("terminated abnormally (exit code -1073741819, 0xC0000005", Case.Sensitive);
        error.Message.ShouldContain("SD0643", Case.Sensitive);
        slangc.Calls.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void SlangcCrashWithStderr_KeepsItsWordsVerbatim(Transport transport)
    {
        const string source = "Texture2D T;\nSamplerState S;\n[shader(\"fragment\")]\nfloat4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return T.Sample(S, uv); }\n";
        var slangc = new ScriptedSlangc("", "", compileExitCode: 139, compileStderr: "Segmentation fault (core dumped)\n");

        var result = slangc.Compiler(transport, new ScriptedSlangc.CapturingCompiler()).Compile(source, Options(PlatformTarget.DirectX));

        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0622");
        error.Message.ShouldEndWith("\nSegmentation fault (core dumped)", Case.Sensitive);
        error.RawDiagnostics.ShouldBe("Segmentation fault (core dumped)");
    }

    /// <summary>A fake whose <c>-E</c> run reports a preprocessor error on stderr yet exits 0, as
    /// slangc does (measured), and whose compile then fails with the same error.</summary>
    private sealed class ErrorPreprocessSlangc(string stderr, string preprocessed)
    {
        public (int ExitCode, string Stdout, string Stderr) Run(string source, IReadOnlyList<string> arguments) =>
            arguments.Contains("-E") ? (0, preprocessed, stderr) : (1, "", stderr);

        public SlangCompiler Compiler(Transport transport) =>
            transport == Transport.InProcess
                ? new SlangCompiler(new ScriptedSlangc.CapturingCompiler(), Run)
                : new SlangCompiler(
                    new ScriptedSlangc.CapturingCompiler(),
                    () => new SlangCompiler.SlangcLocation(null, ScriptedSlangc.FakePath),
                    prepareSlangc: path => path,
                    runSlangc: (_, _, source, arguments) => Run(source, arguments));
    }
}
