#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// Pure tests for <see cref="DxcSpirvEntryRedeclarations"/>: DXC's SPIR-V back end crashes the
/// process on an entry function declared more than once, so the entry's redundant prototypes are
/// blanked out of the compile's input (lines and columns kept), shapes DXC reports cleanly are
/// left to it, and shapes that cannot be made safe are refused with SD0236. No native library,
/// disk or process; the integration half (<c>EntryPointRedeclarationTests</c>) runs every target
/// in a child process, with a control that still crashes DXC directly.
/// </summary>
public sealed class DxcSpirvEntryRedeclarationsTests
{
    private const string Definition = "float4 PS(float4 c : COLOR0) : SV_Target { return c; }";

    private static string Prepared(string source, string entry = "PS")
    {
        Result<string, ShaderError> result = DxcSpirvEntryRedeclarations.Prepare(source, entry, "t.fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "");
        return result.Value;
    }

    private static ShaderError Refused(string source, string entry = "PS")
    {
        Result<string, ShaderError> result = DxcSpirvEntryRedeclarations.Prepare(source, entry, "t.fx");
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
        DxcSpirvEntryRedeclarations.Prepare(source, "PS", "t.fx").Value.ShouldBeSameAs(source);
        DxcSpirvEntryRedeclarations.Prepare(Definition, "PS", "t.fx").Value.ShouldBeSameAs(Definition);
    }

    [Theory]
    [InlineData("float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n", new[] { 1 })]                    // before
    [InlineData(Definition + "\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n", new[] { 2 })]                         // after
    [InlineData("float4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition, new[] { 1, 2 })]
    [InlineData("float4 PS(float4 c);\n" + Definition, new[] { 1 })]                                              // no semantics
    [InlineData("float4 PS(float4);\n" + Definition, new[] { 1 })]                                                // unnamed parameter
    [InlineData("float4 PS(in float4 color : TEXCOORD3) : COLOR1;\n" + Definition, new[] { 1 })]                  // other name, `in`, other semantics
    [InlineData("float4 PS(float4 c /* the colour */ : COLOR0) : SV_Target;\n" + Definition, new[] { 1 })]         // a comment inside
    [InlineData("static inline float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition, new[] { 1 })]           // storage modifiers
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
    public void WithoutATrustworthyView_APrototypeBesideSeveralWrittenDefinitions_IsRefused()
    {
        // A conditional on a macro only the compiler defines: the managed view cannot say which
        // branch DXC takes, so neither can this.
        string source =
            "float4 PS(float4 c : COLOR0) : SV_Target;\n" +
            "#if __HLSL_VERSION >= 2021\n" +
            "float4 PS(float4 c : COLOR0) : SV_Target { return c.bgra; }\n" +
            "#else\n" +
            Definition + "\n" +
            "#endif\n";

        ShaderError error = Refused(source);

        error.Line.ShouldBe(1);
        error.Message.ShouldContain("more than one written definition", Case.Sensitive);
    }

    [Fact]
    public void WithoutATrustworthyView_ASingleDefinitionStillHasItsPrototypeBlanked()
    {
        string source =
            "#if __HLSL_VERSION >= 2021\n#define TINT 1\n#endif\n" +
            "float4 PS(float4 c : COLOR0) : SV_Target;\n" +
            Definition + "\n";

        Prepared(source).ShouldBe(BlankLines(source, 4));
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
    public void APrototypeAMacroProduces_IsRefused_BecauseNoTextEditReachesIt()
    {
        string source =
            "#define DECLARE(name) float4 name(float4 c : COLOR0) : SV_Target;\n" +
            "DECLARE(PS)\n" +
            Definition + "\n";

        ShaderError error = Refused(source);

        error.Line.ShouldBe(2);
        error.Message.ShouldContain("a macro writes, renames or moves part of it", Case.Sensitive);
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
    public void AMacroCallOnThePrototypesOwnLine_IsRefused_NeverBlankedWithIt()
    {
        string source = "#define DECL_PARAM(n) float4 n;\nDECL_PARAM(Unused) float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n";

        Refused(source).Message.ShouldContain("a macro writes, renames or moves part of it", Case.Sensitive);
    }

    [Fact]
    public void AnEntryNamedThroughAMacro_IsDecidedOnTheExpandedName_AndRefused()
    {
        // `-E PSX`: the raw text names PSX only in the #define, so a raw count found it once.
        string source = "#define PF PSX\nfloat4 PF(float4 c : COLOR0) : SV_Target;\nfloat4 PF(float4 c : COLOR0) : SV_Target { return c; }\n";

        ShaderError error = Refused(source, "PSX");

        error.Line.ShouldBe(2);
    }

    [Fact]
    public void TwoPrototypesOnOneLine_AreBothBlanked()
    {
        string source = "float4 PS(float4 c : COLOR0) : SV_Target; float4 PS(float4 c : COLOR0) : SV_Target;\n" + Definition + "\n";

        Prepared(source).ShouldBe(BlankLines(source, 1));
    }

    [Fact]
    public void APrototypeAndAMacroProducedOneOnOneLine_IsRefused()
    {
        string source = "#define P2 float4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 PS(float4 c : COLOR0) : SV_Target; P2\n" + Definition + "\n";

        Refused(source).Line.ShouldBe(2);
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
        DxcSpirvEntryRedeclarations.Prepare(source, "PS", "t.fx", [("VARIANT", "1")]).Value.ShouldBe(source);
    }

    [Fact]
    public void AFunctionLikeMacroWritingItsArgumentTwice_IsNotMissedByTheCheapGate()
    {
        string source = "#define DECL(n) float4 n(float4 c : COLOR0) : SV_Target; float4 n(float4 c : COLOR0) : SV_Target { return c; }\n" +
                        "DECL(PS)\n";

        Refused(source).Line.ShouldBe(2);
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
}
