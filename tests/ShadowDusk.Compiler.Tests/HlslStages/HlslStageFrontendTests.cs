#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.HlslStages;

/// <summary>
/// Pure tests (no disk, no natives) for the two-source HLSL frontend: entry discovery, renaming,
/// merging, conflicts and the generated effect text. The generated effect compiles through the
/// unchanged pipeline, which the integration tests prove byte for byte.
/// </summary>
public sealed class HlslStageFrontendTests
{
    private const string Vertex = """
        float4x4 WorldViewProjection;

        struct VSOut
        {
            float4 Position : SV_Position;
            float2 UV : TEXCOORD0;
        };

        VSOut VSMain(float4 p : POSITION0, float2 uv : TEXCOORD0)
        {
            VSOut o;
            o.Position = mul(p, WorldViewProjection);
            o.UV = uv;
            return o;
        }
        """;

    private const string Pixel = """
        Texture2D Tex;
        SamplerState Samp;

        struct VSOut
        {
            float4 Position : SV_Position;
            float2 UV : TEXCOORD0;
        };

        float4 PSMain(VSOut i) : SV_Target0
        {
            return Tex.Sample(Samp, i.UV);
        }
        """;

    private static HlslStageSources Pair(string vs = Vertex, string ps = Pixel, string ve = "VSMain", string pe = "PSMain") =>
        new(vs, ps, "a.vs.hlsl", "a.ps.hlsl", ve, pe);

    private static HlslStageFxConversion Convert(HlslStageSources sources)
    {
        var result = HlslStageFrontend.ConvertToFx(sources);
        result.IsSuccess.ShouldBeTrue(Messages(result));
        return result.Value;
    }

    private static ShaderError[] Fails(HlslStageSources sources)
    {
        var result = HlslStageFrontend.ConvertToFx(sources);
        result.IsFailure.ShouldBeTrue("expected a failure but got:\n" + (result.IsSuccess ? result.Value.FxText : ""));
        return result.Error;
    }

    private static string Messages(Result<HlslStageFxConversion, ShaderError[]> r) =>
        r.IsFailure ? string.Join(" | ", r.Error.Select(e => $"{e.File}({e.Line},{e.Column}) {e.Code}: {e.Message}")) : "";

    // ---- The generated effect ---------------------------------------------------------------------

    [Fact]
    public void SynthesizesTheTechniqueAndTheShaderModelBlockTheOtherFrontendsUse()
    {
        string fx = Convert(Pair()).FxText;

        fx.ShouldContain("#if SM4\n    #define VS_SHADERMODEL vs_4_0_level_9_1\n    #define PS_SHADERMODEL ps_4_0_level_9_1\n#else\n    #define VS_SHADERMODEL vs_3_0\n    #define PS_SHADERMODEL ps_3_0\n#endif\n", Case.Sensitive);
        fx.ShouldEndWith(
            "technique HlslStages\n{\n    pass P0\n    {\n        VertexShader = compile VS_SHADERMODEL VSMain();\n        PixelShader = compile PS_SHADERMODEL PSMain();\n    }\n}\n",
            Case.Sensitive);
    }

    [Fact]
    public void TheTechniqueNameIsConfigurable()
    {
        Convert(Pair() with { TechniqueName = "Water" }).FxText.ShouldContain("technique Water\n", Case.Sensitive);
    }

    [Fact]
    public void UsesOnlyLineFeeds_EvenForCrlfInput()
    {
        string fx = Convert(Pair(Vertex.Replace("\n", "\r\n"), Pixel.Replace("\n", "\r\n"))).FxText;

        fx.ShouldNotContain("\r", Case.Sensitive);
    }

    [Fact]
    public void IsDeterministic() => Convert(Pair()).FxText.ShouldBe(Convert(Pair()).FxText);

    [Fact]
    public void EveryDeclarationIsPrecededByALineMarkerForItsOriginalFileAndLine()
    {
        string fx = Convert(Pair()).FxText;

        fx.ShouldContain("#line 1 \"a.vs.hlsl\"\nfloat4x4 WorldViewProjection;", Case.Sensitive);
        fx.ShouldContain("#line 3 \"a.vs.hlsl\"\nstruct VSOut", Case.Sensitive);
        fx.ShouldContain("#line 1 \"a.ps.hlsl\"\nTexture2D Tex;", Case.Sensitive);
        fx.ShouldContain("#line 2 \"a.ps.hlsl\"\nSamplerState Samp;", Case.Sensitive);
        fx.ShouldContain("#line 10 \"a.ps.hlsl\"\nfloat4 PSMain", Case.Sensitive);
    }

    [Fact]
    public void KeepsTheOriginalColumnOfAnIndentedDeclaration()
    {
        string fx = Convert(Pair(ps: "    Texture2D Tex;\n    float4 PSMain() : SV_Target0 { return 0; }")).FxText;

        fx.ShouldContain("#line 1 \"a.ps.hlsl\"\n    Texture2D Tex;", Case.Sensitive);
    }

    [Fact]
    public void LogicalFileNamesAreEscapedInLineMarkers()
    {
        string fx = Convert(Pair() with { VertexName = "C:\\shaders\\a \"1\".hlsl" }).FxText;

        fx.ShouldContain("#line 1 \"C:\\\\shaders\\\\a \\\"1\\\".hlsl\"", Case.Sensitive);
    }

    // ---- Merging ----------------------------------------------------------------------------------

    [Fact]
    public void AnIdenticalDeclarationInBothSources_IsWrittenOnce()
    {
        string fx = Convert(Pair()).FxText;

        fx.Split("struct VSOut").Length.ShouldBe(2); // exactly one occurrence
        fx.ShouldContain("Texture2D Tex;", Case.Sensitive);
        fx.ShouldContain("float4x4 WorldViewProjection;", Case.Sensitive);
    }

    [Fact]
    public void IdenticalMeansIgnoringWhitespaceAndComments()
    {
        string ps = Pixel.Replace("""
            struct VSOut
            {
                float4 Position : SV_Position;
                float2 UV : TEXCOORD0;
            };
            """, "struct VSOut { float4 Position : SV_Position; /* same */ float2 UV : TEXCOORD0; };");

        Convert(Pair(ps: ps)).FxText.Split("struct VSOut").Length.ShouldBe(2);
    }

    [Fact]
    public void VertexDeclarationsComeFirst_ThenThePixelOnes()
    {
        string fx = Convert(Pair()).FxText;

        fx.IndexOf("WorldViewProjection;", StringComparison.Ordinal).ShouldBeLessThan(fx.IndexOf("Texture2D Tex;", StringComparison.Ordinal));
        fx.IndexOf("VSOut VSMain", StringComparison.Ordinal).ShouldBeLessThan(fx.IndexOf("float4 PSMain", StringComparison.Ordinal));
    }

    [Fact]
    public void StageOnlyParametersAreKept()
    {
        string fx = Convert(Pair(Vertex + "\nfloat Time;", Pixel + "\nfloat Exposure;")).FxText;

        fx.ShouldContain("float Time;", Case.Sensitive);
        fx.ShouldContain("float Exposure;", Case.Sensitive);
    }

    [Fact]
    public void IdenticalPragmasAndIncludesCollapse()
    {
        string fx = Convert(Pair(
            "#pragma pack_matrix(row_major)\n" + Vertex, "#pragma pack_matrix(row_major)\n" + Pixel)).FxText;

        fx.Split("#pragma pack_matrix(row_major)").Length.ShouldBe(2);
    }

    [Fact]
    public void OverloadsWithDifferentParameterListsAreBothKept()
    {
        string fx = Convert(Pair(
            "float Twice(float x) { return x * 2; }\n" + Vertex,
            "float Twice(float2 x) { return x.x * 2; }\n" + Pixel)).FxText;

        fx.ShouldContain("float Twice(float x)", Case.Sensitive);
        fx.ShouldContain("float Twice(float2 x)", Case.Sensitive);
    }

    [Fact]
    public void AnIdenticalHelperFunctionInBothSources_IsWrittenOnce()
    {
        const string helper = "float Lum(float3 c) { return dot(c, float3(0.3, 0.6, 0.1)); }\n";

        Convert(Pair(helper + Vertex, helper + Pixel)).FxText.Split("float Lum(").Length.ShouldBe(2);
    }

    // ---- Conflicts --------------------------------------------------------------------------------

    [Theory]
    [InlineData("struct VSOut { float4 Position : SV_Position; };", "struct")]
    [InlineData("#define VSOut 1", "macro")]
    public void ADifferentStructOrMacroOfTheSameName_IsSd0664_AtThePixelDeclaration(string pixelDecl, string what)
    {
        string vertex = pixelDecl.StartsWith('#') ? "#define VSOut 2\n" + Vertex.Replace("VSOut", "VSOutX") : Vertex;
        string pixel = pixelDecl.StartsWith('#') ? pixelDecl + "\n" + Pixel.Replace("VSOut", "VSOutX") : pixelDecl + "\n" + Pixel.Replace("VSOut i", "int i");

        ShaderError error = Fails(Pair(vertex, pixel)).Single();

        error.Code.ShouldBe("SD0664");
        error.File.ShouldBe("a.ps.hlsl");
        error.Line.ShouldBe(1);
        error.Column.ShouldBe(1);
        error.Message.ShouldContain(what, Case.Sensitive);
        error.Message.ShouldContain("a.vs.hlsl line", Case.Sensitive);
    }

    [Fact]
    public void ASameSignatureFunctionWithADifferentBody_IsSd0664()
    {
        ShaderError error = Fails(Pair(
            "float Twice(float x) { return x * 2; }\n" + Vertex,
            "float Twice(float x) { return x + x; }\n" + Pixel)).Single();

        error.Code.ShouldBe("SD0664");
        error.Message.ShouldContain("function", Case.Sensitive);
        error.Message.ShouldContain("'Twice'", Case.Sensitive);
    }

    [Theory]
    [InlineData("float4x4 WorldViewProjection;", "float4 WorldViewProjection;")]
    [InlineData("Texture2D Tex;", "Texture3D Tex;")]
    [InlineData("SamplerState Samp;", "SamplerComparisonState Samp;")]
    [InlineData("cbuffer Frame { float a; };", "cbuffer Frame { float b; };")]
    public void ADifferentConstantBufferTextureSamplerOrParameter_IsSd0665(string vertexDecl, string pixelDecl)
    {
        // Put both declarations in each source so the clash is on the declaration, whichever stage owns it.
        ShaderError error = Fails(Pair(Vertex + "\n" + vertexDecl, Pixel + "\n" + pixelDecl)).Single();

        error.Code.ShouldBe("SD0665");
        error.File.ShouldBe("a.ps.hlsl");
        error.Message.ShouldContain("declare them identically", Case.Sensitive);
    }

    [Fact]
    public void EveryConflictIsReported_NotOnlyTheFirst()
    {
        ShaderError[] errors = Fails(Pair(
            "float A;\nfloat B;\n" + Vertex, "float3 A;\nfloat3 B;\n" + Pixel));

        errors.Select(e => e.Code).ShouldBe(["SD0665", "SD0665"]);
        errors.Select(e => e.Line).ShouldBe([1, 2]);
    }

    // ---- Inputs and entry points ------------------------------------------------------------------

    [Theory]
    [InlineData("", "p")]
    [InlineData("   \n", "p")]
    [InlineData("v", "")]
    public void AnEmptySource_IsSd0660_NamingTheStage(string vertex, string pixel)
    {
        ShaderError error = Fails(Pair(vertex, pixel)).First(e => e.Code == "SD0660");

        error.File.ShouldBe(string.IsNullOrWhiteSpace(vertex) ? "a.vs.hlsl" : "a.ps.hlsl");
    }

    [Fact]
    public void AMissingEntryPoint_IsSd0661_ForEachStage()
    {
        ShaderError[] errors = Fails(Pair(ve: "Nope", pe: "Nada"));

        errors.Select(e => (e.Code, e.File)).ShouldBe([("SD0661", "a.vs.hlsl"), ("SD0661", "a.ps.hlsl")]);
        errors[0].Message.ShouldContain("VertexEntry", Case.Sensitive);
        errors[1].Message.ShouldContain("PixelEntry", Case.Sensitive);
    }

    [Fact]
    public void AnEntryThatIsOnlyDeclared_IsSd0661_WithAnExplanation()
    {
        ShaderError error = Fails(Pair(vs: "float4 VSMain();\nfloat4x4 W;")).Single();

        error.Code.ShouldBe("SD0661");
        error.Message.ShouldContain("only declared", Case.Sensitive);
    }

    [Fact]
    public void AnEntryNameThatIsAVariableOrAMember_IsNotAnEntry()
    {
        ShaderError error = Fails(Pair(vs: "float4 VSMain;")).Single();

        error.Code.ShouldBe("SD0661");
    }

    [Fact]
    public void AnEntryDefinedTwiceInOneSource_IsSd0662_AtTheSecondDefinition()
    {
        ShaderError error = Fails(Pair(
            vs: Vertex + "\nVSOut VSMain(float4 q : POSITION0) { VSOut o; return o; }")).Single();

        error.Code.ShouldBe("SD0662");
        error.File.ShouldBe("a.vs.hlsl");
        error.Line.ShouldBeGreaterThan(1);
    }

    [Theory]
    [InlineData("1bad")]
    [InlineData("two words")]
    [InlineData("")]
    [InlineData("a;b")]
    public void AnInvalidEntryName_IsSd0667(string name)
    {
        Fails(Pair(ve: name)).ShouldContain(e => e.Code == "SD0667");
    }

    [Fact]
    public void AnInvalidTechniqueName_IsSd0667() =>
        Fails(Pair() with { TechniqueName = "bad name" }).ShouldContain(e => e.Code == "SD0667");

    [Fact]
    public void ATechniqueBlockInAStageSource_IsSd0666_ExplainingTheAlternative()
    {
        ShaderError error = Fails(Pair(vs: Vertex + "\ntechnique T { pass P { } }")).Single();

        error.Code.ShouldBe("SD0666");
        error.File.ShouldBe("a.vs.hlsl");
        error.Message.ShouldContain("EffectCompiler", Case.Sensitive);
    }

    [Fact]
    public void AnUnbalancedConditional_IsSd0666_InItsOwnFile()
    {
        ShaderError error = Fails(Pair(ps: "#if X\n" + Pixel)).Single();

        error.Code.ShouldBe("SD0666");
        error.File.ShouldBe("a.ps.hlsl");
        error.Line.ShouldBe(1);
    }

    [Fact]
    public void ErrorsFromBothSourcesAreReportedTogether()
    {
        ShaderError[] errors = Fails(Pair(vs: "#endif\n" + Vertex, ps: "#else\n" + Pixel));

        errors.Select(e => e.File).ShouldBe(["a.vs.hlsl", "a.ps.hlsl"]);
    }

    // ---- Renaming entry points that share a name ---------------------------------------------------

    private const string MainVertex = "struct O { float4 p : SV_Position; };\nO main(float4 p : POSITION0) { O o; o.p = p; return o; }";
    private const string MainPixel = "struct O { float4 p : SV_Position; };\nfloat4 main(O i) : SV_Target0 { return i.p; }";

    private static HlslStageSources Mains(string vs = MainVertex, string ps = MainPixel) => Pair(vs, ps, "main", "main");

    [Fact]
    public void WhenBothEntriesAreCalledMain_EachIsRenamedApart()
    {
        HlslStageFxConversion conversion = Convert(Mains());

        conversion.VertexEntry.ShouldBe("main_VS");
        conversion.PixelEntry.ShouldBe("main_PS");
        conversion.FxText.ShouldContain("O main_VS(float4 p : POSITION0)", Case.Sensitive);
        conversion.FxText.ShouldContain("float4 main_PS(O i) : SV_Target0", Case.Sensitive);
        conversion.FxText.ShouldContain("VertexShader = compile VS_SHADERMODEL main_VS();", Case.Sensitive);
        conversion.FxText.ShouldContain("PixelShader = compile PS_SHADERMODEL main_PS();", Case.Sensitive);
    }

    [Fact]
    public void DifferentEntryNames_AreLeftAlone()
    {
        HlslStageFxConversion conversion = Convert(Pair());

        conversion.VertexEntry.ShouldBe("VSMain");
        conversion.PixelEntry.ShouldBe("PSMain");
    }

    [Fact]
    public void TheRenameTouchesDefinitionsAndCallsOnly_NotMembersVariablesOrText()
    {
        string vertex = """
            struct S { float4 main; };
            float4 main(float4 p : POSITION0) { /* main is the entry point */ S s; s.main = p; float main2 = 1; string_note("main"); return s.main; }
            """;

        string fx = Convert(Mains(vertex.Replace("string_note(\"main\");", ""))).FxText;

        fx.ShouldContain("float4 main;", Case.Sensitive);              // the field keeps its name
        fx.ShouldContain("s.main = p;", Case.Sensitive);               // member access untouched
        fx.ShouldContain("float4 main_VS(float4 p", Case.Sensitive);   // the definition is renamed
        fx.ShouldContain("/* main is the entry point */", Case.Sensitive); // comments inside a declaration are verbatim
    }

    [Fact]
    public void ARecursiveOrRepeatedCallToTheEntryIsRenamedToo()
    {
        string vertex = "float4 helper() { return main(0); }\n" + MainVertex.Replace("O main", "O main");

        string fx = Convert(Mains(vertex)).FxText;

        fx.ShouldContain("return main_VS(0);", Case.Sensitive);
    }

    [Fact]
    public void ARenameSuffixAlreadyInUse_GetsAnotherName()
    {
        string vertex = "float main_VS_unused;\nfloat4 main_VSx() { return 0; }\nfloat main_VS;\n" + MainVertex;

        HlslStageFxConversion conversion = Convert(Mains(vertex));

        conversion.VertexEntry.ShouldBe("main_VS_");
    }

    [Fact]
    public void AnEntryNameOnAPreprocessorLine_IsSd0663_NotGuessed()
    {
        ShaderError error = Fails(Mains(vs: "#define main main2\n" + MainVertex)).Single();

        error.Code.ShouldBe("SD0663");
        error.File.ShouldBe("a.vs.hlsl");
        error.Line.ShouldBe(1);
        error.Message.ShouldContain("not safe", Case.Sensitive);
    }

    [Fact]
    public void ARenamedLineKeepsItsLineNumber()
    {
        string fx = Convert(Mains()).FxText;

        fx.ShouldContain("#line 2 \"a.vs.hlsl\"\nO main_VS(", Case.Sensitive);
        fx.ShouldContain("#line 2 \"a.ps.hlsl\"\nfloat4 main_PS(", Case.Sensitive);
    }

    [Fact]
    public void TheWholePairIsDeduplicatedAfterTheRename()
    {
        string fx = Convert(Mains()).FxText;

        fx.Split("struct O").Length.ShouldBe(2);
    }
}
