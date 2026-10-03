#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Vkd3d;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Vkd3d;

/// <summary>
/// PURE unit tests (no disk, no process, no native) for
/// <see cref="Vkd3dCompileContract"/> — the request→ABI argument mapping and error
/// mapping SHARED by the desktop P/Invoke backend (<c>Vkd3dShaderCompiler</c>) and
/// the browser/WASM backend (<c>WasmVkd3dShaderCompiler</c>, Phase 4.1). Pinning this
/// contract here is what guarantees the two hosts ask vkd3d the identical question.
/// </summary>
public sealed class Vkd3dCompileContractTests
{
    private static D3DCompileRequest Request(ShaderStage stage, string? profileOverride = null) => new()
    {
        HlslSource      = "float4 PS() : SV_TARGET { return 0; }",
        SourceFileName  = "test.fx",
        EntryPoint      = "PS",
        Stage           = stage,
        ProfileOverride = profileOverride,
    };

    // -------------------------------------------------------------------------
    // Profile resolution — SM5 stage defaults, override wins (the FNA path)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(ShaderStage.Vertex, "vs_5_0")]
    [InlineData(ShaderStage.Pixel,  "ps_5_0")]
    public void ResolveProfile_DefaultsToSm5ForStage(ShaderStage stage, string expected)
    {
        Vkd3dCompileContract.ResolveProfile(Request(stage)).ShouldBe(expected, customMessage: "the MonoGame DX11 path compiles at SM5 when no override is given — " +
                     "the desktop Vkd3dShaderCompiler default the WASM backend must mirror");
    }

    [Theory]
    [InlineData("ps_2_0")]
    [InlineData("vs_3_0")]
    [InlineData("ps_2_b")]
    public void ResolveProfile_OverrideWins(string profileOverride)
    {
        Vkd3dCompileContract.ResolveProfile(Request(ShaderStage.Pixel, profileOverride))
            .ShouldBe(profileOverride, customMessage: "ProfileOverride (the FNA SM ≤ 3 path) is verbatim");
    }

    [Fact]
    public void ResolveProfile_UnsupportedStage_Throws()
    {
        var act = () => Vkd3dCompileContract.ResolveProfile(Request((ShaderStage)99));

        Should.Throw<ArgumentOutOfRangeException>(act, "an unmapped stage is a programming error, not a compile diagnostic");
    }

    // -------------------------------------------------------------------------
    // SM routing — SM ≤ 3 → D3D_BYTECODE (4), else DXBC_TPF (5)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("ps_1_1")]
    [InlineData("ps_2_0")]
    [InlineData("ps_2_b")]
    [InlineData("vs_3_0")]
    public void Sm3OrBelow_RoutesToD3dBytecode(string profile)
    {
        Vkd3dCompileContract.IsSm3OrBelow(profile).ShouldBeTrue();
        Vkd3dCompileContract.ResolveTargetType(profile)
            .ShouldBe(Vkd3dCompileContract.TargetTypeD3dBytecode);
        Vkd3dCompileContract.ResolveBlobKind(profile).ShouldBe(BlobKind.D3dBytecode);
    }

    [Theory]
    [InlineData("vs_4_0")]
    [InlineData("ps_5_0")]
    [InlineData("vs_5_0")]
    public void Sm4AndUp_RoutesToDxbcTpf(string profile)
    {
        Vkd3dCompileContract.IsSm3OrBelow(profile).ShouldBeFalse();
        Vkd3dCompileContract.ResolveTargetType(profile)
            .ShouldBe(Vkd3dCompileContract.TargetTypeDxbcTpf);
        Vkd3dCompileContract.ResolveBlobKind(profile).ShouldBe(BlobKind.Dxbc);
    }

    [Theory]
    [InlineData("garbage")]   // no underscore
    [InlineData("ps_")]       // nothing after the underscore
    [InlineData("ps_x_0")]    // non-digit SM major
    public void UnparseableProfile_FallsThroughToDxbcTpf_SoVkd3dRejectsItLoudly(string profile)
    {
        // Constraint 5: an unparseable profile must reach vkd3d (DXBC_TPF arm) so the
        // consumer gets vkd3d's own diagnostic — never a silent reroute.
        Vkd3dCompileContract.IsSm3OrBelow(profile).ShouldBeFalse();
        Vkd3dCompileContract.ResolveTargetType(profile)
            .ShouldBe(Vkd3dCompileContract.TargetTypeDxbcTpf);
    }

    [Fact]
    public void TargetTypeConstants_MatchTheVkd3dAbiAndTheWasmWrapperContract()
    {
        // 4/5 are pinned by BOTH the vkd3d 2.1 enum (Vkd3dNative.cs, verified against
        // vkd3d_shader.h) and the Phase 4.1 sdw_vkd3d_compile wrapper contract. If this
        // ever fails, one side of the [JSImport]/P-Invoke split has drifted.
        Vkd3dCompileContract.TargetTypeD3dBytecode.ShouldBe((int)Vkd3dTargetType.D3dBytecode);
        Vkd3dCompileContract.TargetTypeDxbcTpf.ShouldBe((int)Vkd3dTargetType.DxbcTpf);
        Vkd3dCompileContract.TargetTypeD3dBytecode.ShouldBe(4);
        Vkd3dCompileContract.TargetTypeDxbcTpf.ShouldBe(5);
    }

    // -------------------------------------------------------------------------
    // Compile options — ONE list per target type, for every host (issue #295)
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveCompileOptions_DxbcTpf_IsBackwardCompatibilityMapSemanticNames()
    {
        // The desktop marshals this list into vkd3d_shader_compile_info and the browser
        // sends it through its shim into the WASM wrapper. Issue #295 was the browser
        // wrapper carrying its own (empty) list; the values are pinned here against
        // vkd3d_shader.h so neither the list nor its ABI encoding can move unnoticed.
        IReadOnlyList<Vkd3dCompileOption> options =
            Vkd3dCompileContract.ResolveCompileOptions(Vkd3dCompileContract.TargetTypeDxbcTpf);

        options.Count.ShouldBe(1);
        options[0].Name.ShouldBe(Vkd3dCompileOptionName.BackwardCompatibility);
        options[0].Value.ShouldBe((uint)Vkd3dBackwardCompatibility.MapSemanticNames);
        Vkd3dCompileContract.FlattenCompileOptions(options).ShouldBe(
            [0x00000008, 0x00000001],
            customMessage: "VKD3D_SHADER_COMPILE_OPTION_BACKWARD_COMPATIBILITY = 8, " +
                           "VKD3D_SHADER_COMPILE_OPTION_BACKCOMPAT_MAP_SEMANTIC_NAMES = 1");
    }

    [Fact]
    public void ResolveCompileOptions_D3dBytecode_IsEmpty()
    {
        // On the SM1-3 target POSITION / COLOR ARE the native semantics: no option.
        Vkd3dCompileContract.ResolveCompileOptions(Vkd3dCompileContract.TargetTypeD3dBytecode)
            .ShouldBeEmpty();
        Vkd3dCompileContract.FlattenCompileOptions(
                Vkd3dCompileContract.ResolveCompileOptions(Vkd3dCompileContract.TargetTypeD3dBytecode))
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("vs_4_0")]
    [InlineData("ps_4_0_level_9_1")]
    [InlineData("ps_5_0")]
    [InlineData("vs_2_0")]
    [InlineData("ps_3_0")]
    public void ResolveCompileOptions_FollowsTheProfilesTargetType(string profile)
    {
        // The options hang off the target type the profile routes to, the same value both
        // hosts pass to vkd3d, so an SM4+ profile always carries the option and an SM1-3
        // profile never does.
        int targetType = Vkd3dCompileContract.ResolveTargetType(profile);

        Vkd3dCompileContract.FlattenCompileOptions(Vkd3dCompileContract.ResolveCompileOptions(targetType))
            .ShouldBe(Vkd3dCompileContract.IsSm3OrBelow(profile) ? [] : [8, 1]);
    }

    [Fact]
    public void FlattenCompileOptions_IsNameValuePairsInOrder_ValueCarriedBitForBit()
    {
        // The (name, value) word pairs are the sdw_vkd3d_compile_options ABI: the wrapper
        // reads options[2*i] as the name and options[2*i + 1] as the unsigned value.
        var options = new Vkd3dCompileOption[]
        {
            new() { Name = Vkd3dCompileOptionName.BackwardCompatibility, Value = 3 },
            new() { Name = (Vkd3dCompileOptionName)0x0000000c, Value = 0xFFFFFFFF },
        };

        Vkd3dCompileContract.FlattenCompileOptions(options).ShouldBe([8, 3, 12, -1]);
    }

    [Fact]
    public void CompileOption_LayoutIsTwo32BitWords()
    {
        // struct vkd3d_shader_compile_option { enum name; unsigned int value; }: the
        // desktop marshals an array of these, so the managed layout is the C layout.
        System.Runtime.InteropServices.Marshal.SizeOf<Vkd3dCompileOption>().ShouldBe(8);
    }

    // -------------------------------------------------------------------------
    // Source preparation — ONE text per request, for every host (issue #319)
    // -------------------------------------------------------------------------

    /// <summary>
    /// What the real pipeline hands the backends for an effect with an include: the macro
    /// prelude (ending in a <c>#line 1</c> back to the main file), the main file, and the
    /// include flattener's <c>#line 1 "&lt;include&gt;"</c> / <c>#line N "&lt;main&gt;"</c> pair
    /// around the included text (the exact shapes <c>MacroSet.ToTextPrepend</c> and
    /// <c>Preprocessor.FlattenFile</c> emit).
    /// </summary>
    private const string FlattenedWithInclude =
        "#line 1 \"MinimalWithInclude.fx\"\n" +
        "\n" +
        "// ShadowDusk platform macros — DO NOT EDIT (generated)\n" +
        "#define MGFX 1\n" +
        "#define HLSL 1\n" +
        "#define SM4 1\n" +
        "#line 1 \"MinimalWithInclude.fx\"\n" +
        "// MinimalWithInclude.fx\n" +
        "\n" +
        "#line 1 \"includes/TestHelper.fxh\"\n" +
        "float4 ApplyIdentity(float4 value) { return value; }\n" +
        "#line 4 \"MinimalWithInclude.fx\"\n" +
        "float4 PS(float4 c : COLOR0) : SV_TARGET { return ApplyIdentity(c); }\n";

    private const string FlattenedWithIncludePrepared =
        "\n" +
        "\n" +
        "// ShadowDusk platform macros — DO NOT EDIT (generated)\n" +
        "#define MGFX 1\n" +
        "#define HLSL 1\n" +
        "#define SM4 1\n" +
        "\n" +
        "// MinimalWithInclude.fx\n" +
        "\n" +
        "\n" +
        "float4 ApplyIdentity(float4 value) { return value; }\n" +
        "\n" +
        "float4 PS(float4 c : COLOR0) : SV_TARGET { return ApplyIdentity(c); }\n";

    [Fact]
    public void PrepareSource_BlanksEveryLineDirective_AndKeepsEveryOtherLine_ForAnIncludeFlattenedEffect()
    {
        // The desktop backend marshals PrepareSource(HlslSource) and the browser backend sends
        // PrepareSource(HlslSource) through its shim: this literal IS the text both hosts hand
        // vkd3d for an include-carrying effect. The directive lines become EMPTY lines (never
        // deleted), so vkd3d's line numbers stay those of the directive-carrying text and
        // Vkd3dSourceLocator can map them back through the directives (issue #202).
        string prepared = Vkd3dCompileContract.PrepareSource(FlattenedWithInclude);

        prepared.ShouldBe(FlattenedWithIncludePrepared);
        prepared.Split('\n').Length.ShouldBe(FlattenedWithInclude.Split('\n').Length, "blanking must not move a line");
        prepared.ShouldNotContain("#line", Case.Sensitive, "vkd3d prints a fixme per #line directive it is shown (issue #319)");
        prepared.ShouldContain("#define MGFX 1", Case.Sensitive, "only #line goes; every other directive is vkd3d's to see");
    }

    [Theory]
    [InlineData("#line 1 \"user.fx\"\nfloat x;\n",            "\nfloat x;\n")]       // the prelude's form
    [InlineData("#line 42\nfloat x;\n",                        "\nfloat x;\n")]       // no file name
    [InlineData("  #  line 7 \"a b/c.fxh\"\nfloat x;\n",        "\nfloat x;\n")]       // spaces, a path with a space
    [InlineData("\t#line 3 \"u.fx\" // tail\nfloat x;\n",       "\nfloat x;\n")]       // tab indent, trailing comment
    [InlineData("float x;\n#line 2 \"u.fx\"",                   "float x;\n")]         // last line, no newline after it
    // CRLF text: the directive's CR goes with the directive (the pattern runs to the LF), the LF
    // stays, so the line count holds. This is the desktop's long-standing behavior, pinned so a
    // "tidier" rewrite cannot move a byte of what vkd3d is handed.
    [InlineData("#line 1 \"u.fx\"\r\nfloat x;\r\n",             "\nfloat x;\r\n")]
    public void PrepareSource_BlanksTheWholeDirectiveLine_InEveryShapeTheFlattenerAndAuthorsWrite(string source, string expected)
    {
        Vkd3dCompileContract.PrepareSource(source).ShouldBe(expected);
    }

    [Theory]
    [InlineData("#define LINE 1\n")]            // not #line
    [InlineData("#linefoo 1\n")]                // \b: 'line' must be a whole word
    [InlineData("// #line 5 \"c.fx\"\n")]       // not at the start of the line (a comment)
    [InlineData("float line = 1; // #line\n")]  // the word in code
    [InlineData("#pragma once\n#if X\n#endif\n")]
    public void PrepareSource_LeavesEverythingThatIsNotALineDirective(string source)
    {
        Vkd3dCompileContract.PrepareSource(source).ShouldBe(source);
    }

    [Fact]
    public void PrepareSource_IsIdempotent_AndEmptyIsEmpty()
    {
        string once = Vkd3dCompileContract.PrepareSource(FlattenedWithInclude);

        Vkd3dCompileContract.PrepareSource(once).ShouldBe(once);
        Vkd3dCompileContract.PrepareSource(string.Empty).ShouldBe(string.Empty);
    }

    // -------------------------------------------------------------------------
    // Error mapping — verbatim diagnostics first, SD0212 fallback
    // -------------------------------------------------------------------------

    [Fact]
    public void MapCompileFailure_ParsesVerbatimDiagnosticLine()
    {
        const string messages = "test.fx(12,5): error E5005: variable 'foo' is undefined\n";

        ShaderError error = Vkd3dCompileContract.MapCompileFailure(messages, "test.fx", "fallback");

        error.File.ShouldBe("test.fx");
        error.Line.ShouldBe(12);
        error.Column.ShouldBe(5);
        error.Code.ShouldBe("E5005");
        error.Message.ShouldBe("variable 'foo' is undefined", customMessage: "constraint 5: the message is surfaced exactly as the compiler emitted it");
    }

    [Fact]
    public void MapCompileFailure_EmptyMessages_FallsBackToSd0212WithCallerText()
    {
        ShaderError error = Vkd3dCompileContract.MapCompileFailure(
            "", "test.fx", "vkd3d-shader DXBC compilation failed (rc=-4) with no diagnostics");

        error.Code.ShouldBe("SD0212");
        error.Message.ShouldBe("vkd3d-shader DXBC compilation failed (rc=-4) with no diagnostics");
        error.RawDiagnostics.ShouldBeNull();
    }

    [Fact]
    public void MapCompileFailure_UnstructuredMessages_CarriesRawTextNotSwallowed()
    {
        const string messages = "some completely unstructured vkd3d output";

        ShaderError error = Vkd3dCompileContract.MapCompileFailure(messages, "test.fx", "fallback");

        // The reformatter wraps unmatched text as a raw-diagnostics error — the text
        // must remain reachable (constraint 5), whatever the wrapper shape.
        error.RawDiagnostics!.ShouldContain(messages, Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Warning mapping (issue #335) — a SUCCESSFUL compile's message text, both hosts
    // -------------------------------------------------------------------------

    [Fact]
    public void MapCompileWarnings_ParsesVkd3dsOwnWarningLine_Verbatim()
    {
        // Exactly what vkd3d 2.1 writes for a float4 assigned to a float3 (measured).
        const string messages = "user.fx:47:12: W5300: Implicit truncation of vector type.\n";

        IReadOnlyList<ShaderError> warnings = Vkd3dCompileContract.MapCompileWarnings(messages, "user.fx");

        warnings.Count.ShouldBe(1);
        warnings[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warnings[0].Code.ShouldBe("W5300");
        warnings[0].Message.ShouldBe("Implicit truncation of vector type.", customMessage: "constraint 5: vkd3d's text, verbatim");
        warnings[0].File.ShouldBe("user.fx");
        warnings[0].Line.ShouldBe(47, customMessage: "vkd3d's OWN coordinates; the caller relocates them (Vkd3dSourceLocator)");
        warnings[0].Column.ShouldBe(12);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void MapCompileWarnings_NothingSaid_IsNoWarning(string? messages)
    {
        Vkd3dCompileContract.MapCompileWarnings(messages, "user.fx").ShouldBeEmpty(
            "a silent success has no warnings on either host; '' is what the shim and the P/Invoke both hand over for a NULL buffer");
    }

    [Fact]
    public void MapCompileWarnings_ErrorSeverityTextOnASuccess_IsNormalizedToWarning_TextKept()
    {
        // A successful compile cannot carry an error; whatever vkd3d spelled, the entry is a
        // warning, with every character of the message kept.
        IReadOnlyList<ShaderError> warnings = Vkd3dCompileContract.MapCompileWarnings(
            "user.fx:3:5: error: something vkd3d chose to call an error\nuser.fx:9:1: W5302: Unrecognized attribute 'foo'.", "user.fx");

        warnings.Count.ShouldBe(2);
        warnings[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warnings[0].Message.ShouldBe("something vkd3d chose to call an error");
        warnings[1].Code.ShouldBe("W5302");
        warnings[1].Message.ShouldBe("Unrecognized attribute 'foo'.");
    }
}
