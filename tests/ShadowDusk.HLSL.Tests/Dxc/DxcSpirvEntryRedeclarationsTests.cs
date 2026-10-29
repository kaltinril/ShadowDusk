#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// Tests for <see cref="DxcSpirvEntryRedeclarations"/>: DXC's SPIR-V back end crashes the
/// process on an entry function declared more than once, so the entry's redundant prototypes are
/// blanked out of the compile's input (lines and columns kept), shapes DXC reports cleanly are
/// left to it, and shapes that cannot be made safe are refused with SD0236. Every decision and
/// every proof is DXC's own preprocessor (<c>-P</c>, the pinned DXC, in this process: a
/// preprocess-only run never reaches the back end that faults, which every test here also
/// shows by surviving). The integration half (<c>EntryPointRedeclarationTests</c>) runs every
/// target in a child process, with a control that still crashes DXC directly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcSpirvEntryRedeclarationsTests
{
    private const string Definition = "float4 PS(float4 c : COLOR0) : SV_Target { return c; }";

    /// <summary>A conditional only clang's preprocessor (DXC's) can evaluate; the managed one cannot.</summary>
    private const string ClangOnly = "#if __has_feature(cxx_rtti)\n#define TINT 1\n#endif\n";

    private static readonly DxcShaderCompiler Compiler = new();

    /// <summary>DXC's preprocessor with the OpenGL pixel compile's own arguments (and <paramref name="macros"/> as its -D).</summary>
    private static Func<string, DxcSpirvEntryRedeclarations.PreprocessedText> Dxc(
        IReadOnlyList<(string Name, string? Value)>? macros = null, bool allowWarnings = false)
    {
        IReadOnlyList<string> arguments = DxcSpirvEntryRedeclarations.PreprocessArguments(
            DxcFlagBuilder.Build(PlatformTarget.OpenGL, ShaderStage.Pixel, "PS", macros ?? [], new DxcCompileOptions { AllowWarnings = allowWarnings }));
        return text => Compiler.PreprocessOnly(text, arguments, includeHandler: null);
    }

    private static Result<string, ShaderError> Prepare(string source, string entry = "PS", IReadOnlyList<(string Name, string? Value)>? macros = null) =>
        DxcSpirvEntryRedeclarations.Prepare(source, entry, "t.fx", macros, Dxc(macros));

    private static string Prepared(string source, string entry = "PS")
    {
        Result<string, ShaderError> result = Prepare(source, entry);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "");
        return result.Value;
    }

    private static ShaderError Refused(string source, string entry = "PS")
    {
        Result<string, ShaderError> result = Prepare(source, entry);
        result.IsFailure.ShouldBeTrue("expected an SD0236 refusal, got:\n" + (result.IsSuccess ? result.Value : ""));
        result.Error.Code.ShouldBe("SD0236");
        return result.Error;
    }
    /// <summary>The source with each listed line replaced by spaces of the same width.</summary>
    private static string BlankLines(string source, params int[] oneBasedLines)
    {
        string[] lines = source.Split('\n');
        foreach (int line in oneBasedLines)
            lines[line - 1] = new string(' ', lines[line - 1].Length);
        return string.Join('\n', lines);
    }

    [Fact]
    public void ASourceWithoutAPrototypeOfTheEntry_IsReturnedAsIs()
    {
        string source = "float4 Tint(float4 c);\n" + Definition.Replace("return c;", "return Tint(c);", StringComparison.Ordinal) +
                        "\nfloat4 Tint(float4 c) { return c * 2; }\n";

        // The same instance: nothing was even copied, so the compile is the one it always was.
        Prepare(source).Value.ShouldBeSameAs(source);
        Prepare(Definition).Value.ShouldBeSameAs(Definition);
    }

    [Theory]
    [InlineData("float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", new[] { 1 })]                    // before
    [InlineData(Definition + "\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n", new[] { 2 })]                         // after
    [InlineData("float4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition, new[] { 1, 2 })]
    [InlineData("float4 PS(float4 c);\n" + Definition, new[] { 1 })]                                              // no semantics
    [InlineData("float4 PS(float4);\n" + Definition, new[] { 1 })]                                                // unnamed parameter
    [InlineData("float4 PS(in float4 color : TEXCOORD3) : COLOR1;\n" + Definition, new[] { 1 })]                  // other name, `in`, other semantics
    [InlineData("float4 PS(float4 c /* the colour */ : COLOR0) : SV_Target;\n" + Definition, new[] { 1 })]         // a comment inside
    public void APrototypeMatchingTheDefinition_IsBlankedKeepingEveryLineAndColumn(string source, int[] blankedLines)
    {
        string prepared = Prepared(source);

        // Exactly the prototype's characters became spaces: the definition is untouched and
        // every later line keeps its number and its columns (diagnostics do not move).
        prepared.ShouldBe(BlankLines(source, blankedLines));
        prepared.Length.ShouldBe(source.Length);
        prepared.ShouldContain(Definition, Case.Sensitive);
    }

    [Fact]
    public void OnlyTheEntryPointsPrototypeIsBlanked_NotAHelpersOrTheOtherStagesEntry()
    {
        string source =
            "float4 Tint(float4 c);\n" +
            "float4 VS(float4 p : POSITION0) : SV_Position;\n" +
            "float4 PS(float4 c : COLOR0) : SV_Target;\n" +
            "float4 VS(float4 p : POSITION0) : SV_Position { return p; }\n" +
            "float4 PS(float4 c : COLOR0) : SV_Target { return Tint(c); }\n" +
            "float4 Tint(float4 c) { return c * 2; }\n";

        Prepared(source, "PS").ShouldBe(BlankLines(source, 3));
        Prepared(source, "VS").ShouldBe(BlankLines(source, 2));
    }

    [Fact]
    public void AMultiLinePrototypeWithAttributesAndATemplateReturnType_IsBlankedWhole()
    {
        string source =
            "struct S { float4 c : COLOR0; };\n" +
            "[earlydepthstencil]\n" +
            "vector<float, 4> PS(S s,\n" +
            "                    float2 uv : TEXCOORD0) : SV_Target;\n" +
            "[earlydepthstencil]\n" +
            "vector<float, 4> PS(S s, float2 uv : TEXCOORD0) : SV_Target { return s.c; }\n";

        Prepared(source).ShouldBe(BlankLines(source, 2, 3, 4));
    }

    [Fact]
    public void ACallAfterTheDefinition_StillResolves_SoThePrototypeIsBlanked()
    {
        string source =
            "float4 PS(float4 c : COLOR0) : SV_Target;\n" +
            Definition + "\n" +
            "float4 Half(float4 c : COLOR0) : SV_Target { return PS(c) * 0.5; }\n";

        Prepared(source).ShouldBe(BlankLines(source, 1));
    }

    [Fact]
    public void ACallBetweenThePrototypeAndTheDefinition_IsRefusedAtTheCall()
    {
        string source =
            "float4 PS(float4 c : COLOR0) : SV_Target;\n" +
            "float4 Half(float4 c : COLOR0) : SV_Target { return PS(c) * 0.5; }\n" +
            Definition + "\n";

        ShaderError error = Refused(source);

        error.Line.ShouldBe(2);
        error.Column.ShouldBe(53);
        error.Message.ShouldContain("'PS' is called here, between its prototype and its definition", Case.Sensitive);
        error.Message.ShouldContain("Move the definition of 'PS' above its first call", Case.Sensitive);
    }

    [Fact]
    public void AnUnusedOverloadPrototype_IsInertAndBlanked_AsFxcCompilesPastIt()
    {
        string source = "float4 PS(float2 uv : TEXCOORD0) : SV_Target;\n" + Definition + "\n";

        Prepared(source).ShouldBe(BlankLines(source, 1));
    }

    [Fact]
    public void AnOverloadPrototypeOfACalledEntry_IsRefused()
    {
        string source =
            "float4 PS(float2 uv : TEXCOORD0) : SV_Target;\n" +
            Definition + "\n" +
            "float4 Half(float4 c : COLOR0) : SV_Target { return PS(c) * 0.5; }\n";

        ShaderError error = Refused(source);

        error.Line.ShouldBe(1);
        error.Message.ShouldContain("parameter list differs from its definition", Case.Sensitive);
    }

    [Theory]
    [InlineData("float3 PS(float4 c : COLOR0) : SV_Target;\n" + Definition)]                              // return type only: DXC (and fxc, X3068) refuse it
    [InlineData("float4 PS(float4 c : COLOR0) : SV_Target;\n")]                                          // no definition: "missing entry point definition"
    [InlineData("float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n" +
                "float4 PS(float2 c : TEXCOORD0) : SV_Target { return c.xyxy; }\n")]                    // two definitions: "ambiguous entry point function"
    public void AShapeDxcReportsCleanly_IsLeftToDxc(string source)
    {
        Prepared(source).ShouldBe(source);
    }

    [Fact]
    public void DefinitionsInEachBranchOfAConditional_AreOneDefinitionForDxc_SoThePrototypeIsBlanked()
    {
        // The VsTransformColorTexture shape: one definition per shader-model branch. DXC sees one
        // of them, so the prototype must go (counting both on the raw text left it, and crashed).
        string source =
            "float4 PS(float4 c : COLOR0) : SV_Target;\n" +
            "#if SM6\n" +
            "float4 PS(float4 c : COLOR0) : SV_Target { return c.bgra; }\n" +
            "#else\n" +
            Definition + "\n" +
            "#endif\n";

        Prepared(source).ShouldBe(BlankLines(source, 1));
        Prepared("#define SM6 1\n" + source).ShouldBe("#define SM6 1\n" + BlankLines(source, 1));
    }

    [Fact]
    public void APrototypeInADeadBranch_IsLeftAlone_AndADefinitionOnlyInADeadBranchIsDxcsToReport()
    {
        string deadPrototype = "#if 0\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n#endif\n" + Definition + "\n";
        string deadDefinition = "float4 PS(float4 c : COLOR0) : SV_Target;\n#if 0\n" + Definition + "\n#endif\n";

        Prepared(deadPrototype).ShouldBe(deadPrototype);
        Prepared(deadDefinition).ShouldBe(deadDefinition); // "missing entry point definition", from DXC
    }

    [Fact]
    public void AConditionalOnACompilerMacro_IsDxcsToDecide_SoAPrototypeBesideADefinitionPerBranchIsBlanked()
    {
        // DXC's preprocessor takes the branch DXC compiles (__HLSL_VERSION is 2018 on the pin);
        // either way there is one definition beside the prototype.
        string source =
            "float4 PS(float4 c : COLOR0) : SV_Target;\n" +
            "#if __HLSL_VERSION >= 2021\n" +
            "float4 PS(float4 c : COLOR0) : SV_Target { return c.bgra; }\n" +
            "#else\n" +
            Definition + "\n" +
            "#endif\n";

        Prepared(source).ShouldBe(BlankLines(source, 1));
    }

    // ---- Final review of 2026-10-29: DXC's preprocessor is the only judge ------------------

    [Theory]
    // A prototype (and a helper calling the entry, which would be refused if DXC compiled it) in
    // a branch DXC never compiles: _XBOX is defined by no compiler, __HLSL_VERSION always by DXC.
    [InlineData("#ifdef _XBOX\nfloat4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 Half(float4 c : COLOR0) : SV_Target { return PS(c) * 0.5; }\n#endif\n" + Definition + "\n")]
    [InlineData("#ifdef _XBOX\nprecise float4 PS(float4 c : COLOR0) : SV_Target;\n#endif\n" + Definition + "\n")]
    [InlineData("#ifndef __HLSL_VERSION\nfloat4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 Half(float4 c : COLOR0) : SV_Target { return PS(c) * 0.5; }\n#endif\n" + Definition + "\n")]
    [InlineData("#if defined(__PSSL__) || defined(_PS4)\nprecise float4 PS(float4 c : COLOR0) : SV_Target;\n#endif\n" + Definition + "\n")]
    [InlineData("#ifdef __SOME_FUTURE_DXC_MACRO__\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n#endif\n" + Definition + "\n")]
    public void APrototypeInABranchDxcDoesNotCompile_IsHandedToDxcAsWritten(string source)
    {
        Prepare(source).Value.ShouldBeSameAs(source);
    }

    [Theory]
    // C's unsigned arithmetic, which the managed evaluator got wrong (it crashed DXC on main):
    // -1 converts to the largest unsigned value, so the conditions are TRUE to DXC. DXC warns
    // about the conversion ("left side of operator converted from negative value to unsigned"),
    // which -WX (the default compile) makes an error: the preprocess run fails, so the source goes
    // to DXC as written and the compile stops on that error in the front end, before the back end.
    [InlineData("#if -1 > 0u", true)]
    [InlineData("#if (-1 / 2u) > 100", true)]
    [InlineData("#if 0xFFFFFFFFFFFFFFFF > 0", false)]
    public void APrototypeUnderUnsignedPreprocessorArithmetic_IsDecidedAsDxcEvaluatesIt(string conditional, bool dxcWarns)
    {
        string source = conditional + "\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n#endif\n" + Definition + "\n";

        // Warnings allowed (the OpenGL reflection companion, or AllowWarnings): blanked as DXC evaluates it.
        DxcSpirvEntryRedeclarations.Prepare(source, "PS", "t.fx", null, Dxc(allowWarnings: true)).Value.ShouldBe(BlankLines(source, 2));

        Result<string, ShaderError> strict = Prepare(source);
        if (dxcWarns)
            strict.Value.ShouldBeSameAs(source);
        else
            strict.Value.ShouldBe(BlankLines(source, 2));
    }

    [Fact]
    public void ASourceDxcsPreprocessorRejects_IsHandedToDxcAsWritten_ForDxcToReport()
    {
        // DXC's front end stops on the #error before its back end runs (measured): no crash.
        string source = "#error not this target\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n";

        Prepare(source).Value.ShouldBeSameAs(source);
    }
    [Fact]
    public void ADeclarationInsideABlockOrAnExpression_IsNotTakenForAPrototype()
    {
        string source =
            "struct Ops { float4 PS(float4 c); };\n" +                     // a method: not global scope
            "static const float4 K = Base + PS(float4(1, 1, 1, 1));\n" +   // a call in an initializer
            Definition + "\n";

        Prepared(source).ShouldBe(source);
    }

    [Fact]
    public void APrototypeAMacroCallWritesWhole_IsRemovedWithTheCall()
    {
        // The call produces the prototype and nothing else, so blanking the call is proven to
        // take out exactly the prototype.
        string source =
            "#define DECLARE(name) float4 name(float4 c : COLOR0) : SV_Target;\n" +
            "DECLARE(PS)\n" +
            Definition + "\n";

        Prepared(source).ShouldBe(BlankLines(source, 2));
    }

    [Fact]
    public void AMacroCallWritingThePrototypeAndOtherCode_IsRefused_BecauseNoEditTakesOnlyThePrototype()
    {
        string source =
            "#define DECLARE(name) float4 Unused; float4 name(float4 c : COLOR0) : SV_Target;\n" +
            "DECLARE(PS)\n" +
            Definition + "\n";

        ShaderError error = Refused(source);

        error.Line.ShouldBe(2);
        error.Message.ShouldContain("cannot be removed from the source as written without changing anything else", Case.Sensitive);
    }

    [Fact]
    public void APrototypeInsideAMacroArgument_IsBlankedOnlyWhenTheCallStillExpandsToEverythingElse()
    {
        // `ID(x)` writes its argument once: blanking the argument leaves `ID()`, which expands to nothing.
        string once = "#define ID(x) x\nID(float4 PS(float4 c : COLOR0) : SV_Target;)\n" + Definition + "\n";
        Prepared(once).ShouldBe("#define ID(x) x\nID(" + new string(' ', 41) + ")\n" + Definition + "\n");

        // `WRAP(x)` writes more than its argument: blanking the argument keeps `float4 W;`.
        string wrapped = "#define WRAP(x) float4 W; x\nWRAP(float4 PS(float4 c : COLOR0) : SV_Target;)\n" + Definition + "\n";
        Prepared(wrapped).ShouldBe("#define WRAP(x) float4 W; x\nWRAP(" + new string(' ', 41) + ")\n" + Definition + "\n");

        // `TWICE(x)` writes it twice: no single range takes out one copy and nothing else.
        string twice = "#define TWICE(x) x x\nTWICE(float4 PS(float4 c : COLOR0) : SV_Target;)\n" + Definition + "\n";
        Refused(twice).Line.ShouldBe(2);
    }

    [Fact]
    public void AMacroThatDoesNotDeclareTheEntry_IsNoRefusal()
    {
        string source =
            "#define PS_SM ps_3_0\n" +
            "#define HALF(x) ((x) * 0.5)\n" +
            "float4 PS(float4 c : COLOR0) : SV_Target;\n" +
            "float4 PS(float4 c : COLOR0) : SV_Target { return HALF(c); }\n";

        Prepared(source).ShouldBe(BlankLines(source, 3));
    }

    [Fact]
    public void ARefusalIsReportedAtTheAuthorsLine_ThroughTheFlattenersLineDirectives()
    {
        string source =
            "#define OPENGL 1\n" +
            "#define GLSL 1\n" +
            "#line 1 \"effect.fx\"\n" +
            "float4 PS(float4 c : COLOR0) : SV_Target;\n" +
            "float4 Half(float4 c : COLOR0) : SV_Target { return PS(c) * 0.5; }\n" +
            Definition + "\n";

        ShaderError error = Refused(source);

        error.File.ShouldBe("effect.fx");
        error.Line.ShouldBe(2);
    }

    // ---- Review of 2026-10-28: the blanked range, and shapes that still reached DXC ----------

    [Theory]
    // A macro call with no `;` of its own right before the prototype (MonoGame's Macros.fxh
    // END_CONSTANTS, DECLARE_TEXTURE): the range starts at the return type, so the call stays.
    [InlineData("#define DECL_PARAM(n) float4 n;\nDECL_PARAM(Unused)\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", 3)]
    [InlineData("#define END_CONSTANTS };\ncbuffer C {\nfloat4 Tint;\nEND_CONSTANTS\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", 5)]
    [InlineData("#define DECLARE_TEXTURE(n, i) Texture2D n : register(t##i);\nDECLARE_TEXTURE(Mask, 1)\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", 3)]
    public void AMacroCallBeforeThePrototype_IsKept_OnlyThePrototypeIsBlanked(string source, int prototypeLine)
    {
        Prepared(source).ShouldBe(BlankLines(source, prototypeLine));
    }

    [Fact]
    public void AMacroCallOnThePrototypesOwnLine_IsKept_TheRangeStartsAtTheReturnType()
    {
        // The review's BLOCKER: the range used to start at the call, so `float4 Unused;` vanished
        // with the prototype and the definition silently lost a parameter on OpenGL and Vulkan.
        const string Call = "DECL_PARAM(Unused) ";
        const string Prototype = "float4 PS(float4 c : COLOR0) : SV_Target;";
        string source = "#define DECL_PARAM(n) float4 n;\n" + Call + Prototype + "\n" + Definition + "\n";

        string prepared = Prepared(source);

        prepared.ShouldBe("#define DECL_PARAM(n) float4 n;\n" + Call + new string(' ', Prototype.Length) + "\n" + Definition + "\n");
        prepared.ShouldContain("DECL_PARAM(Unused)", Case.Sensitive);
    }

    [Fact]
    public void AMacroCallBetweenTwoPrototypesOnOneLine_IsKept()
    {
        const string Prototype = "float4 PS(float4 c : COLOR0) : SV_Target;";
        string source = "#define DECL_PARAM(n) float4 n;\n" + Prototype + " DECL_PARAM(Unused) " + Prototype + "\n" + Definition + "\n";

        Prepared(source).ShouldBe("#define DECL_PARAM(n) float4 n;\n" + new string(' ', Prototype.Length) + " DECL_PARAM(Unused) " +
                                  new string(' ', Prototype.Length) + "\n" + Definition + "\n");
    }

    [Fact]
    public void AnEntryNamedThroughAMacro_IsDecidedOnTheExpandedName_AndItsPrototypeBlanked()
    {
        // `-E PSX`: the raw text names PSX only in the #define, so a raw count found it once.
        string source = "#define PF PSX\nfloat4 PF(float4 c : COLOR0) : SV_Target;\nfloat4 PF(float4 c : COLOR0) : SV_Target { return c; }\n";

        Prepared(source, "PSX").ShouldBe(BlankLines(source, 2));
    }

    [Fact]
    public void TwoPrototypesOnOneLine_AreBothBlanked()
    {
        string source = "float4 PS(float4 c : COLOR0) : SV_Target; float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n";

        Prepared(source).ShouldBe(BlankLines(source, 1));
    }

    [Fact]
    public void APrototypeAndAMacroProducedOneOnOneLine_AreBothRemoved()
    {
        string source = "#define P2 float4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 PS(float4 c : COLOR0) : SV_Target; P2\n" + Definition + "\n";

        Prepared(source).ShouldBe(BlankLines(source, 2));
    }
    [Theory]
    [InlineData("typedef float4 F4;\nF4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", 2)]
    [InlineData("vector<float, 4> PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", 1)]
    [InlineData("float4 PS(vector<float,4> c : COLOR0) : SV_Target;\n" + Definition + "\nfloat4 H(float4 c) { return PS(c); }\n", 1)]
    [InlineData("float4 PS(const float4 c : COLOR0) : SV_Target;\n" + Definition + "\nfloat4 H(float4 c) { return PS(c); }\n", 1)]
    [InlineData("float4 PS(in float4 c : COLOR0) : SV_Target;\n" + Definition + "\nfloat4 H(float4 c) { return PS(c); }\n", 1)]
    public void ASameTypeSpelledDifferently_IsTheSameDeclaration_AndBlanked(string source, int prototypeLine)
    {
        Prepared(source).ShouldBe(BlankLines(source, prototypeLine));
    }

    [Fact]
    public void AReturnTypeThatMayBeTheDefinitionsUnderAnotherName_IsRefused()
    {
        // A struct alias the reading does not resolve: one type to DXC or two cannot be told.
        string source = "struct S { float4 c : SV_Target; };\nstruct T { float4 c : SV_Target; };\n" +
                        "T PS(float4 c : COLOR0);\nS PS(float4 c : COLOR0) { S s; s.c = c; return s; }\n";

        Refused(source).Message.ShouldContain("spelled differently", Case.Sensitive);
    }

    [Theory]
    [InlineData("float4 PS /* note */ (float4 c : COLOR0) : SV_Target;\n" + Definition + "\n")]
    [InlineData("float4 /* note */ PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n")]
    [InlineData("float4 // note\nPS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n")]
    public void CommentsInsideThePrototype_AreRead_AndBlankedWithIt(string source)
    {
        string prepared = Prepared(source);

        prepared.ShouldNotContain("note", Case.Sensitive);
        prepared.ShouldEndWith(Definition + "\n", Case.Sensitive);
        prepared.Length.ShouldBe(source.Length);
    }

    [Fact]
    public void AConditionalChoosingThePrototypesSemantic_IsBlankedWithThePrototype()
    {
        string source = "float4 PS(float4 c : COLOR0)\n#if SM4\n : SV_Target\n#else\n : COLOR0\n#endif\n;\n" +
                        "float4 PS(float4 c : COLOR0) : COLOR0 { return c; }\n";

        Prepared(source).ShouldBe(BlankLines(source, 1, 2, 3, 4, 5, 6, 7));
    }

    [Fact]
    public void ATechniquesCompileReference_IsNotACall()
    {
        string source = "float4 PS(float2 uv : TEXCOORD0) : SV_Target;\n" + Definition + "\n" +
                        "technique T { pass { PixelShader = compile ps_3_0 PS(); } }\n";

        Prepared(source).ShouldBe(BlankLines(source, 1));
    }

    [Fact]
    public void ADMacroTheCompilePasses_IsSeenByTheView()
    {
        string source = "#if VARIANT\nfloat4 PS(float4 c : COLOR0) : SV_Target { return c.bgra; }\n#endif\n" +
                        "float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n";

        // Without VARIANT one definition: blanked. With `-D VARIANT` two: DXC reports it.
        Prepared(source).ShouldBe(BlankLines(source, 4));
        Prepare(source, macros: [("VARIANT", "1")]).Value.ShouldBe(source);
    }

    [Fact]
    public void AFunctionLikeMacroWritingItsArgumentTwice_IsNotMissedByTheCheapGate()
    {
        string source = "#define DECL(n) float4 n(float4 c : COLOR0) : SV_Target; float4 n(float4 c : COLOR0) : SV_Target { return c; }\n" +
                        "DECL(PS)\n";

        Refused(source).Line.ShouldBe(2);
    }

    // ---- Re-review of 2026-10-29: only a prototype that adds nothing is removed --------------

    [Theory]
    [InlineData("[earlydepthstencil]\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", "the attribute '[earlydepthstencil]'")]
    [InlineData("precise float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", "the modifier 'precise'")]
    // DXC's own front end answers an `inline` or `static` prototype of the entry with "cannot
    // find entry function" (measured on DirectX 12): removing it would compile what DXC refuses.
    [InlineData("static inline float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", "the modifier 'static'")]
    [InlineData("inline float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", "the modifier 'inline'")]
    [InlineData("float4 PS(nointerpolation float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", "the modifier 'nointerpolation' on parameter 1")]
    [InlineData("float4 PS(float4 c : COLOR0, uniform float k = 0.5) : SV_Target;\n" +
                "float4 PS(float4 c : COLOR0, uniform float k) : SV_Target { return c * k; }\n", "a default value for parameter 2")]
    public void APrototypeThatSaysMoreThanItsDefinition_IsRefused_NeverSilentlyDropped(string source, string what)
    {
        ShaderError error = Refused(source);

        error.Message.ShouldContain($"carries {what}, which its definition does not", Case.Sensitive);
    }

    [Theory]
    [InlineData("[earlydepthstencil]\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n[earlydepthstencil]\n" + Definition + "\n", new[] { 1, 2 })]
    [InlineData("float4 PS(float4 c : COLOR0, uniform float k) : SV_Target;\n" +
                "float4 PS(float4 c : COLOR0, uniform float k = 0.5) : SV_Target { return c * k; }\n", new[] { 1 })]
    [InlineData("float4 PS(nointerpolation float4 c : COLOR0) : SV_Target;\n" +
                "float4 PS(nointerpolation float4 c : COLOR0) : SV_Target { return c; }\n", new[] { 1 })]
    [InlineData("float4 PS(float4 c) : SV_Target;\n" + Definition + "\n", new[] { 1 })]          // the definition says more: fine
    // DXC takes an entry's semantics from its definition only (DirectX 12: "Semantic must be
    // defined for all parameters"), so a semantic only the prototype has is DXC's to report.
    [InlineData("float4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 PS(float4 c) : SV_Target { return c; }\n", new[] { 1 })]
    [InlineData("float4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 PS(float4 c : COLOR0) { return c; }\n", new[] { 1 })]
    [InlineData("static float4 PS(float4 c : COLOR0) : SV_Target;\nstatic " + Definition + "\n", new[] { 1 })]  // on both: DXC judges the definition
    public void APrototypeThatSaysNothingTheDefinitionDoesNot_IsBlanked(string source, int[] blankedLines)
    {
        Prepared(source).ShouldBe(BlankLines(source, blankedLines));
    }

    [Theory]
    [InlineData("float4 Other(float x), PS(float4 c : COLOR0);\n" + Definition + "\n")]               // second declarator
    [InlineData("float4 PS(float4 c : COLOR0) : SV_Target, Other(float x);\n" + Definition + "\n")]   // first of two declarators
    [InlineData("float4 PS\n#pragma warning(disable : 3571)\n(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n")]
    public void ADeclarationShapeTheReadingDoesNotModel_IsRefused_NeverPassedToDxc(string source)
    {
        Refused(source).Message.ShouldContain("cannot read as a declaration", Case.Sensitive);
    }

    [Fact]
    public void APrototypeInsideAnActiveConditional_IsBlanked_TheDirectivesKept()
    {
        string source = "#if 1\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n#endif\n" + Definition + "\n";

        Prepared(source).ShouldBe(BlankLines(source, 2));
    }

    [Fact]
    public void AMultiLinePrototypeAMacroCompletes_IsBlanked()
    {
        // The MonoGame template's `: POS_OUT` / `SV_POSITION` macros inside the prototype.
        string source =
            "#define POS_OUT SV_Position\n" +
            "float4 VS(float4 p : POSITION0)\n" +
            "    : POS_OUT;\n" +
            "float4 VS(float4 p : POSITION0) : POS_OUT { return p; }\n";

        Prepared(source, "VS").ShouldBe(BlankLines(source, 2, 3));
    }

    [Fact]
    public void ADMacroThatWritesThePrototype_IsNotMissedByTheCheapGate()
    {
        string source = "PROTO\n" + Definition + "\n";

        Result<string, ShaderError> result = Prepare(source, macros: [("PROTO", "float4 PS(float4 c : COLOR0) : SV_Target;")]);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.ShouldBe(BlankLines(source, 1));
    }

    [Fact]
    public void ADMacroThatWritesThePrototype_UnderACompilerMacroConditional_IsBlanked()
    {
        string source = "#if __HLSL_VERSION >= 2021\n#define TINT 1\n#endif\nPROTO\n" + Definition + "\n";

        Result<string, ShaderError> result = Prepare(source, macros: [("PROTO", "float4 PS(float4 c : COLOR0) : SV_Target;")]);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.ShouldBe(BlankLines(source, 4));
    }

    [Fact]
    public void PreprocessArguments_AreTheCompilesOwn_WithoutCodegenSwitches_PlusP()
    {
        IReadOnlyList<string> compile = DxcFlagBuilder.Build(
            PlatformTarget.OpenGL, ShaderStage.Pixel, "PS", [("SM4", "1")], new DxcCompileOptions { EmbedDebugInfo = true, SkipValidation = true });

        IReadOnlyList<string> preprocess = DxcSpirvEntryRedeclarations.PreprocessArguments(compile);

        preprocess[^1].ShouldBe("-P");
        preprocess.ShouldContain("-spirv");
        preprocess.ShouldContain("ps_5_0");
        preprocess.ShouldContain("-DSM4=1");
        preprocess.ShouldContain(DxcDebugSpirvSource.InputName);  // __FILE__ as the compile sees it
        preprocess.ShouldNotContain("-Zi");
        preprocess.ShouldNotContain("-Qembed_debug");
        preprocess.ShouldNotContain("-Vd");
    }
    [Fact]
    public void AppliesToSpirvCompilesAndTheUnvalidatedReflectionCompanion_NotShippedDxilOrPreprocessing()
    {
        DxcSpirvEntryRedeclarations.AppliesTo(
            DxcFlagBuilder.Build(PlatformTarget.OpenGL, ShaderStage.Pixel, "PS", [], new DxcCompileOptions())).ShouldBeTrue();
        DxcSpirvEntryRedeclarations.AppliesTo(
            DxcFlagBuilder.Build(PlatformTarget.Vulkan, ShaderStage.Vertex, "main", [], new DxcCompileOptions())).ShouldBeTrue();
        DxcSpirvEntryRedeclarations.AppliesTo(
            DxcFlagBuilder.Build(PlatformTarget.DirectX, ShaderStage.Pixel, "PS", [], new DxcCompileOptions { SkipValidation = true })).ShouldBeTrue(
            "the OpenGL reflection companion must read the same text as the SPIR-V compile it describes");
        DxcSpirvEntryRedeclarations.AppliesTo(
            DxcFlagBuilder.Build(PlatformTarget.DirectX12, ShaderStage.Pixel, "PS", [], new DxcCompileOptions())).ShouldBeFalse(
            "shipped DirectX 12 DXIL never had the fault and keeps its input");
        DxcSpirvEntryRedeclarations.AppliesTo(DxcFlagBuilder.BuildPreprocess([])).ShouldBeFalse();
    }
    // ---- Third review of 2026-10-29 -------------------------------------------------------

    private const string Tech = "technique T { pass { PixelShader = compile ps_4_0 PS(); } }\n";

    // BLOCKER: a source with NO prototype must reach DXC exactly as written, whatever its
    // conditionals on compiler macros and whatever macros paste tokens or name the entry.
    [Theory]
    [InlineData("#define CAT(a,b) a##b\n#if __SHADER_TARGET_MAJOR >= 4\n#define SCALE 2\n#else\n#define SCALE 1\n#endif\n")]
    [InlineData("#define CAT(a,b) a##b\n#ifdef __spirv__\n#define SCALE 2\n#else\n#define SCALE 1\n#endif\n")]
    [InlineData("#define CAT(a,b) a##b\n#if defined(__HLSL_VERSION)\n#define SCALE 2\n#else\n#define SCALE 1\n#endif\n")]
    [InlineData("#define ENTRY_NAME PS\n#if __SHADER_TARGET_MAJOR >= 4\n#define SCALE 2\n#else\n#define SCALE 1\n#endif\n")]
    [InlineData("#define CAT(a,b) a##b\nstatic const float CAT(Sc,ale) = 2;\n#define SCALE Scale\n" + ClangOnly)]
    [InlineData("#define ENTRY_NAME PS\n#define SCALE 2\n" + ClangOnly)]
    public void ASourceWithoutAPrototype_IsHandedToDxcAsWritten_WhateverItsMacros(string prelude)
    {
        foreach (string definition in new[]
                 {
                     "float4 PS(float4 c : COLOR0) : COLOR0 { return c * SCALE; }\n",
                     "float4 ENTRY_NAME(float4 c : COLOR0) : COLOR0 { return c * SCALE; }\n",
                 })
        {
            if (definition.Contains("ENTRY_NAME", StringComparison.Ordinal) && !prelude.Contains("ENTRY_NAME", StringComparison.Ordinal))
                continue;
            string source = prelude + Tech + definition;
            Prepare(source).Value.ShouldBeSameAs(source);
        }
    }

    [Theory]
    [InlineData("#if __SHADER_TARGET_MAJOR >= 4\n#define SM4 1\n#endif\n")]
    [InlineData(ClangOnly)]
    public void AMonoGameMacrosFxhSpriteShader_IsHandedToDxcAsWritten(string conditional)
    {
        // MonoGame's Macros.fxh pastes tokens (DECLARE_TEXTURE, SAMPLE_TEXTURE): nothing it pastes
        // here can spell the entry's name.
        string source =
            "#define DECLARE_TEXTURE(Name, index) Texture2D<float4> Name : register(t##index); sampler Name##Sampler : register(s##index)\n" +
            "#define SAMPLE_TEXTURE(Name, texCoord) Name.Sample(Name##Sampler, texCoord)\n" +
            conditional +
            "DECLARE_TEXTURE(Texture, 0);\n" +
            "float4 SpritePS(float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target { return SAMPLE_TEXTURE(Texture, uv) * color; }\n" +
            "technique SpriteBatch { pass { PixelShader = compile ps_4_0 SpritePS(); } }\n";

        Prepare(source, "SpritePS").Value.ShouldBeSameAs(source);
    }

    [Fact]
    public void AnEntryDeclaredTwiceThroughAMacro_IsBlanked()
    {
        const string Declarations =
            "#define ENTRY_NAME PS\n" +
            "float4 ENTRY_NAME(float4 c : COLOR0) : SV_Target;\n" +
            "float4 ENTRY_NAME(float4 c : COLOR0) : SV_Target { return c; }\n";

        string source = "#if __SHADER_TARGET_MAJOR >= 4\n#define SM4 1\n#endif\n" + Declarations;
        Prepared(source).ShouldBe(BlankLines(source, 5));
    }

    [Fact]
    public void APasteThatSpellsTheEntry_IsBlankedWhenTheCallWritesOnlyThePrototype()
    {
        string source = "#define CAT(a,b) a##b\nfloat4 CAT(P,S)(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n";

        Prepared(source).ShouldBe(BlankLines(source, 2));
    }

    [Fact]
    public void ManyConditionalsOnNamesNoCompilerDefines_AreSimplyUndefined()
    {
        string conditionals = string.Concat(Enumerable.Range(1, 5).Select(i => $"#ifdef __X{i}__\n#define K{i} 1\n#endif\n"));

        string plain = conditionals + "float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n";
        Prepared(plain).ShouldBe(BlankLines(plain, 16));

        string macro = conditionals + "#define DECLARE(n) float4 n(float4 c : COLOR0) : SV_Target;\nDECLARE(PS)\n" + Definition + "\n";
        Prepared(macro).ShouldBe(BlankLines(macro, 17));
    }
    // SHOULD-FIX 1: DXC defines more than the old list knew (measured). DXC's preprocessor knows.
    [Theory]
    [InlineData("#ifdef __GNUC__")]
    [InlineData("#if __llvm__")]
    [InlineData("#if __BYTE_ORDER__ == __ORDER_LITTLE_ENDIAN__")]
    public void APrototypeUnderAConditionalOnAMacroDxcDefines_IsBlanked(string conditional)
    {
        string source = conditional + "\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n#endif\n" + Definition + "\n";

        Prepared(source).ShouldBe(BlankLines(source, 2));
    }

    [Fact]
    public void AMacroThatDxcExpandsToCode_OnThePrototypesLine_IsKept()
    {
        // DXC expands EXTRA (it defines __GNUC__) to the overload the definition calls: a range
        // that also took EXTRA would be a silent change, and DXC's proof rejects it.
        string source =
            "#ifdef __GNUC__\n#define EXTRA float4 Tint(float4 c) { return c * 2; }\n#else\n#define EXTRA\n#endif\n" +
            "#define SEM : COLOR0\n" +
            "float4 Tint(int4 c) { return float4(c); }\n" +
            "EXTRA float4 PS(float4 c : COLOR0) SEM;\n" +
            "float4 PS(float4 c : COLOR0) : COLOR0 { return Tint(c); }\n";

        string prepared = Prepared(source);

        string[] lines = prepared.Split('\n');
        lines[7].ShouldBe("EXTRA" + new string(' ', "EXTRA float4 PS(float4 c : COLOR0) SEM;".Length - 5));
        BlankLines(prepared, 8).ShouldBe(BlankLines(source, 8));
    }

    // SHOULD-FIX 2: a parenthesised declarator is a declaration to DXC.
    [Theory]
    [InlineData("float4 (PS)(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n")]
    [InlineData("float4 ((PS))(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n")]
    [InlineData("float4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 (PS)(float4 c : COLOR0) : SV_Target { return c; }\n")]
    public void AParenthesisedDeclaratorBesideAnotherDeclaration_IsRefused(string source)
    {
        Refused(source).Message.ShouldContain("cannot read as a declaration", Case.Sensitive);
    }

    [Fact]
    public void AParenthesisedDefinitionAlone_IsHandedToDxcAsWritten()
    {
        string source = "float4 (PS)(float4 c : COLOR0) : SV_Target { return c; }\n";

        Prepare(source).Value.ShouldBeSameAs(source);
    }

    [Fact]
    public void AParenthesisedCallBetweenThePrototypeAndTheDefinition_IsACall()
    {
        string source =
            "float4 PS(float4 c : COLOR0) : SV_Target;\n" +
            "float4 Half(float4 c : COLOR0) : SV_Target { return (PS)(c) * 0.5; }\n" +
            Definition + "\n";

        Refused(source).Message.ShouldContain("is called here, between its prototype and its definition", Case.Sensitive);
    }

    // NITs.
    [Fact]
    public void ABlankedCharacter_BecomesAsManySpacesAsItsUtf8Bytes_SoDxcColumnsDoNotMove()
    {
        const string Prototype = "float4 PS(float4 c /* é😀 */ : COLOR0) : SV_Target;";
        string source = Prototype + "\n" + Definition + "\n";

        string prepared = Prepared(source);

        prepared.Split('\n')[0].ShouldBe(new string(' ', System.Text.Encoding.UTF8.GetByteCount(Prototype)));
        prepared.Split('\n')[1].ShouldBe(Definition);
    }

    [Fact]
    public void ACarriageReturnOnlySource_IsBlankedLikeAnyOther()
    {
        string source = "#define K 1\rfloat4 PS(float4 c : COLOR0) : SV_Target;\r" + Definition + "\r";

        Prepared(source).ShouldBe("#define K 1\r" + new string(' ', 41) + "\r" + Definition + "\r");
    }

    [Fact]
    public void MacroNamesAfterThePrototype_DoNotUseUpTheAttempts()
    {
        const string Prototype = "float4 PS(float4 c SEM) SEM;";
        string tail = string.Concat(Enumerable.Repeat(" E", 40));
        string source = "#define E ;\n#define SEM : COLOR0\n" + Prototype + tail + "\nfloat4 PS(float4 c : COLOR0) : COLOR0 { return c; }\n";

        Prepared(source).Split('\n')[2].ShouldBe(new string(' ', Prototype.Length) + tail);
    }
}
