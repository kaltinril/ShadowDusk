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
    // measured drift of vkd3d: a skipped conditional arm (vkd3d drops those lines from
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
        // vkd3d wrote "user.fx:71:11: ..." (13 + 5 prelude - 2 skipped + 40 + 4 + 11 = 71,
        // column 11 of its re-spaced text). The raw blob is printed under the summary, so its
        // location moves with the summary's; the code and text after it stay verbatim.
        result.Error.RawDiagnostics.ShouldBe("user.fx:13:13: E5000: syntax error, unexpected ';'");
    }

    [Vkd3dFact]
    public void Compile_CodegenError_IsReportedOnTheAuthorsLine_Issue202()
    {
        // The reporter's class: a construct vkd3d cannot lower at SM <= 3, raised from
        // codegen after the whole file parsed, so every intrinsic template above it has
        // already inflated the counter. (The original case here was an int-typed ternary;
        // vkd3d 2.0 implemented that, so the case is now a vector store through a runtime
        // index. What is under test is the relocation, never the particular gap.)
        var result = CompileIssue202(Issue202Prelude + Issue202User("    float3 v = uv.xyy; v[i] = a; b = v.x + v.y + v.z;"));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("E5017");
        result.Error.Message.ShouldContain("Non-constant vector addressing on store", Case.Sensitive);
        result.Error.File.ShouldBe("user.fx");
        result.Error.Line.ShouldBe(13);
        result.Error.Column.ShouldBeInRange(24, 32, "a token of the 'v[i] = a' store itself");
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
    public void Compile_TheSentinel_IsSpelledOneOfTheTwoBisonWays_OnThisHost_Issue202()
    {
        // The locator's probes plant '@' and look for the "unexpected <undefined token>"
        // diagnostic; bison spells that token "invalid token" (3.6+: the win-x64 MinGW and
        // macOS Homebrew builds) or '$undefined' (3.5: the linux-x64 Ubuntu 20.04 build).
        // Pin that THIS host's native says one of the two, and that an author's own '@'
        // still lands on its line and column through the ambiguity handling.
        var result = CompileIssue202(Issue202Prelude + Issue202User("    @"));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("E5000");
        result.Error.Message.ShouldBeOneOf(
            Vkd3dSourceLocator.SentinelMessage, Vkd3dSourceLocator.SentinelMessageLegacyBison);
        result.Error.File.ShouldBe("user.fx");
        result.Error.Line.ShouldBe(13);
        result.Error.Column.ShouldBe(5);
    }

    [Vkd3dFact]
    public void Compile_TheTerminator_EndsTheParseAndIsNotTheSentinel_OnThisHost_Issue202()
    {
        // Every probe ends with the terminator, so a probe whose sentinel was swallowed (a
        // skipped #if arm, a block comment) still stops at the parse instead of running the
        // whole failing compile again. It must be a syntax error on a source that is
        // otherwise complete, and must not read as the sentinel.
        var result = CompileIssue202(
            Issue202Prelude + Issue202User("    b += a;") + Vkd3dSourceLocator.Terminator);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("E5000");
        result.Error.Message.ShouldStartWith("syntax error", Case.Sensitive);
        result.Error.Message.ShouldNotContain(Vkd3dSourceLocator.SentinelMessage, Case.Sensitive);
        result.Error.Message.ShouldNotContain(Vkd3dSourceLocator.SentinelMessageLegacyBison, Case.Sensitive);
        result.Error.Line.ShouldBe(16, customMessage: "the terminator's own line, one past the 15-line file");
    }

    [Vkd3dFact]
    public void Compile_SeveralDiagnostics_TheRawTextMovesToo_ForOneMoreCompile_Issue202()
    {
        // Two runtime-indexed stores, both reported by vkd3d, with a template call between
        // them. The raw text printed under the summary must name the author's lines for
        // both, and the second must come from the ONE statement-marker compile (real vkd3d
        // answering every marker with a located warning that names it), not from a
        // bisection of its own.
        static D3DCompileRequest Request(string line13) => new()
        {
            HlslSource      = Issue202Prelude + Issue202User(line13),
            SourceFileName  = "user.fx",
            EntryPoint      = "PS",
            Stage           = ShaderStage.Pixel,
            ProfileOverride = "ps_3_0",
        };

        int oneDiagnosticCalls = 0;
        Vkd3dShaderCompiler.CompileCore(
            Request("    float3 v = uv.xyy; v[i] = a;\n    b = asin(saturate(v.x));\n    b += v.y;"),
            CancellationToken.None, () => oneDiagnosticCalls++).IsFailure.ShouldBeTrue();

        int twoDiagnosticCalls = 0;
        var result = Vkd3dShaderCompiler.CompileCore(
            Request("    float3 v = uv.xyy; v[i] = a;\n    b = asin(saturate(v.x));\n    v[i] = b; b += v.y;"),
            CancellationToken.None, () => twoDiagnosticCalls++);

        result.IsFailure.ShouldBeTrue();
        result.Error.Line.ShouldBe(13);
        string[] raw = result.Error.RawDiagnostics!.Split('\n');
        raw.Length.ShouldBe(2);
        raw[0].ShouldStartWith($"user.fx:13:{result.Error.Column}: E5017: ", Case.Sensitive);
        raw[1].ShouldStartWith("user.fx:15:", Case.Sensitive);
        raw[1].ShouldContain(": E5017: Aborting due to not yet implemented feature: Non-constant vector addressing on store", Case.Sensitive);
        twoDiagnosticCalls.ShouldBe(oneDiagnosticCalls + 1, "the same summary search, plus one marker compile for the rest");
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

    // -------------------------------------------------------------------------
    // Issue #255: a cancelled compile does not go on to pay for the relocation probes
    // -------------------------------------------------------------------------

    private static D3DCompileRequest Issue255FailingRequest() => new()
    {
        HlslSource      = Issue202Prelude + Issue202User("    float z=;"),
        SourceFileName  = "user.fx",
        EntryPoint      = "PS",
        Stage           = ShaderStage.Pixel,
        ProfileOverride = "ps_3_0",
    };

    [Vkd3dFact]
    public void Compile_TokenCancelledAfterTheFailingCompile_RunsNoRelocationProbe_Issue255()
    {
        // The window the check exists for: the real compile has failed (one native call
        // nothing can interrupt) and up to MaxProbes more native calls are about to run to
        // relocate its diagnostic. An already-cancelled token never gets here (the entry
        // check throws first), so the token is cancelled from the seam that runs as the
        // FIRST native call returns: deterministic, no timing, real vkd3d.
        D3DCompileRequest request = Issue255FailingRequest();

        // Control: uncancelled, this very request probes. Without it the assertion below
        // could pass on a shader whose diagnostic needed no relocation at all.
        int uncancelledCalls = 0;
        var located = Vkd3dShaderCompiler.CompileCore(request, CancellationToken.None, () => uncancelledCalls++);
        located.IsFailure.ShouldBeTrue();
        located.Error.Line.ShouldBe(13);
        uncancelledCalls.ShouldBeGreaterThan(1, "the failing compile plus at least one relocation probe");

        using var cts = new CancellationTokenSource();
        int nativeCalls = 0;
        void CancelAfterTheFirstCall()
        {
            if (++nativeCalls == 1)
                cts.Cancel();
        }

        Should.Throw<OperationCanceledException>(() =>
            Vkd3dShaderCompiler.CompileCore(request, cts.Token, CancelAfterTheFirstCall));

        nativeCalls.ShouldBe(1, "only the compile itself may have reached vkd3d; every probe must be skipped");
    }

    [Vkd3dFact]
    public void Compile_TokenCancelledMidRelocation_StopsAtTheNextProbe_Issue255()
    {
        // "Before EACH probe", not just the first: cancel as the second probe returns.
        D3DCompileRequest request = Issue255FailingRequest();
        using var cts = new CancellationTokenSource();
        int nativeCalls = 0;
        void CancelAfterTheThirdCall()
        {
            if (++nativeCalls == 3)
                cts.Cancel();
        }

        Should.Throw<OperationCanceledException>(() =>
            Vkd3dShaderCompiler.CompileCore(request, cts.Token, CancelAfterTheThirdCall));

        nativeCalls.ShouldBe(3, "the compile and two probes ran; the third probe must not start");
    }

    [Vkd3dFact]
    public async Task Compile_TokenAlreadyCancelled_ThrowsWithoutCompiling_Issue255()
    {
        // The public contract on both entry points. This is the ENTRY check (it would pass
        // with the probe check deleted); the two tests above pin the probe check.
        var compiler = new Vkd3dShaderCompiler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() => compiler.Compile(Issue255FailingRequest(), cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await compiler.CompileAsync(Issue255FailingRequest(), cts.Token));
    }

    // -------------------------------------------------------------------------
    // Issue #319 — the desktop hands vkd3d EXACTLY Vkd3dCompileContract.PrepareSource(HlslSource)
    // -------------------------------------------------------------------------

    /// <summary>
    /// The bytes the desktop REALLY hands <c>vkd3d_shader_compile</c>, read back from the
    /// marshalled compile info (<see cref="Vkd3dShaderCompiler.NativeSourceObserver"/>), must be
    /// the UTF-8 of <see cref="Vkd3dCompileContract.PrepareSource"/> of the request's text: the
    /// string the browser host sends through its shim. A desktop that transformed the source
    /// on its own (which is how the browser came to hand vkd3d the <c>#line</c> directives the
    /// desktop blanks, issue #319) fails here. The source is the include-flattened shape the
    /// real pipeline produces: macro prelude, main file, an include with its two directives.
    /// </summary>
    [Vkd3dFact]
    public void Compile_HandsVkd3dThePreparedSource_ReadBackFromTheNativeCall_Issue319()
    {
        string source =
            Issue202Prelude +                                              // ends in '#line 1 "user.fx"'
            "float2 dir;\n" +
            "#line 1 \"shared/helpers.fxh\"\n" +
            "float Helper(float2 v) { return v.x + v.y; }\n" +
            "#line 3 \"user.fx\"\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR { return Helper(uv) + dir.x; }\n";
        source.Split('\n').Count(l => l.StartsWith("#line", StringComparison.Ordinal)).ShouldBe(3, "the fixture carries the three directive shapes");

        var observed = new List<byte[]>();
        Vkd3dShaderCompiler.NativeSourceObserver.Value = bytes => observed.Add(bytes);
        try
        {
            var result = CompileIssue202(source);
            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "a valid SM3 PS");
        }
        finally
        {
            Vkd3dShaderCompiler.NativeSourceObserver.Value = null;
        }

        observed.Count.ShouldBe(1, "a successful compile is one native call (no relocation probes)");
        string handed = Encoding.UTF8.GetString(observed[0]);
        handed.ShouldBe(Vkd3dCompileContract.PrepareSource(source), customMessage:
            "the desktop handed vkd3d_shader_compile text other than Vkd3dCompileContract.PrepareSource(HlslSource). " +
            "The browser backend sends exactly that string, so the two hosts would compile different text " +
            "(issue #319). Change the preparation in the contract, never in a host.");
        handed.ShouldNotContain("#line", Case.Sensitive);
        handed.Split('\n').Length.ShouldBe(source.Split('\n').Length, "blanked, not deleted: the locator needs the alignment");
        handed.ShouldNotBe(source, "the control: this source does carry directives, so preparation changed it");
    }

    /// <summary>
    /// The relocation probes compile the SAME prepared text (with the locator's edits), never
    /// the directive-carrying request text: no probe may show vkd3d a <c>#line</c> either.
    /// </summary>
    [Vkd3dFact]
    public void Compile_RelocationProbes_NeverShowVkd3dALineDirective_Issue319()
    {
        var observed = new List<byte[]>();
        Vkd3dShaderCompiler.NativeSourceObserver.Value = bytes => observed.Add(bytes);
        try
        {
            CompileIssue202(Issue202Prelude + Issue202User("    float z=;")).IsFailure.ShouldBeTrue();
        }
        finally
        {
            Vkd3dShaderCompiler.NativeSourceObserver.Value = null;
        }

        observed.Count.ShouldBeGreaterThan(1, "a failing compile is the real compile plus relocation probes");
        foreach (byte[] bytes in observed)
            Encoding.UTF8.GetString(bytes).ShouldNotContain("#line", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Issue #335 — a SUCCESSFUL compile's non-fatal diagnostics: the shared path, the
    // author's position, and the verbatim text the browser gate compares against
    // -------------------------------------------------------------------------

    /// <summary>
    /// The desktop's <c>PlatformBlob.Warnings</c> on a warning-bearing success must be what
    /// the SHARED <see cref="Vkd3dCompileContract.MapCompileWarnings"/> makes of the text vkd3d
    /// really returned (<see cref="Vkd3dShaderCompiler.NativeMessagesObserver"/>), relocated onto
    /// the author's line and column through the macro prelude's <c>#line</c> and the skipped
    /// <c>#if</c> arm: the exact path the browser host runs on the same text (issue #335). The
    /// construct is a float4 assigned to a float3 on user.fx line 13 (<c>W5300</c>, measured
    /// on vkd3d 2.1 for every profile).
    /// </summary>
    [Vkd3dFact]
    public void Compile_SuccessWithANonFatalDiagnostic_WarningsAreTheSharedMappingOfVkd3dsText_Relocated_Issue335()
    {
        var observed = new List<string>();
        Vkd3dShaderCompiler.NativeMessagesObserver.Value = m => observed.Add(m);
        Result<PlatformBlob, ShaderError> result;
        try
        {
            result = CompileIssue202(Issue202Prelude + Issue202User("    float3 t = float4(uv, b, 1.0); b = t.x;"));
        }
        finally
        {
            Vkd3dShaderCompiler.NativeMessagesObserver.Value = null;
        }

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "a valid SM3 PS with an implicit truncation");
        observed.Count.ShouldBeGreaterThanOrEqualTo(1, "the real compile reports its message text first");
        string messages = observed[0];
        messages.ShouldContain("W5300", Case.Sensitive);
        messages.ShouldContain("Implicit truncation of vector type.", Case.Sensitive);
        messages.ShouldStartWith("user.fx:", Case.Sensitive);

        // The text the desktop got is what the browser shim must hand back (the node gate
        // compares them); the warnings are the shared mapping of that text, relocated.
        IReadOnlyList<ShaderError> shared = Vkd3dCompileContract.MapCompileWarnings(messages, "user.fx");
        shared.Count.ShouldBe(1);
        shared[0].Line.ShouldNotBe(13, "vkd3d's own line is drifted by the prelude, the skipped arm and the intrinsic templates; the control that relocation did something");

        result.Value.Warnings.Count.ShouldBe(1);
        ShaderError warning = result.Value.Warnings[0];
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.Code.ShouldBe(shared[0].Code);
        warning.Message.ShouldBe(shared[0].Message, customMessage: "verbatim: only the position moves");
        warning.Code.ShouldBe("W5300");
        warning.File.ShouldBe("user.fx");
        warning.Line.ShouldBe(13, customMessage: "the author's line, through the prelude's '#line 1 \"user.fx\"' and the skipped #if arm");
        warning.Column.ShouldBe(12, customMessage: "the 't' declarator vkd3d reports, remapped onto the author's indentation");
        warning.RawDiagnostics.ShouldBe("user.fx:13:12: W5300: Implicit truncation of vector type.", customMessage: "the raw line moves with the summary; the text after the prefix stays vkd3d's");
    }

    /// <summary>
    /// A silent success reports an empty message text, so the cross-host comparison of that
    /// text is '' against '' for the many corpus compiles that warn about nothing, and
    /// <c>Warnings</c> is empty, not a one-entry list of nothing.
    /// </summary>
    [Vkd3dFact]
    public void Compile_SilentSuccess_ReportsEmptyMessageTextAndNoWarnings_Issue335()
    {
        var observed = new List<string>();
        Vkd3dShaderCompiler.NativeMessagesObserver.Value = m => observed.Add(m);
        Result<PlatformBlob, ShaderError> result;
        try
        {
            result = CompileIssue202(Issue202Prelude + Issue202User("    b += a;"));
        }
        finally
        {
            Vkd3dShaderCompiler.NativeMessagesObserver.Value = null;
        }

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "a valid SM3 PS");
        observed.Count.ShouldBe(1, "one native call");
        observed[0].ShouldBe(string.Empty, customMessage: "nothing said");
        result.Value.Warnings.ShouldBeEmpty();
    }
}
