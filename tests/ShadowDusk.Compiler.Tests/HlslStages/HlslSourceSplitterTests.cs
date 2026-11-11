#nullable enable

using ShadowDusk.Compiler.HlslStages;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.HlslStages;

/// <summary>Pure tests (no disk, no natives) for the top-level declaration splitter.</summary>
public sealed class HlslSourceSplitterTests
{
    private static List<HlslDeclaration> Split(string source)
    {
        var result = HlslSourceSplitter.Split(source, "test.hlsl");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => e.Message)) : "");
        return result.Value;
    }

    [Fact]
    public void ClassifiesEveryTopLevelShape()
    {
        List<HlslDeclaration> d = Split("""
            #define SCALE 2.0
            #pragma pack_matrix(row_major)
            struct VSOut { float4 Position : SV_Position; };
            cbuffer Frame { float4x4 ViewProjection; }
            Texture2D Tex;
            SamplerState Samp;
            float4 Tint;
            static const float Pi = 3.14159;
            float4 Helper(float4 v);
            float4 Main(VSOut i) : SV_Target0 { return Tint; }
            typedef float4 Color;
            """);

        d.Select(x => x.Kind).ShouldBe(
        [
            HlslDeclarationKind.Macro, HlslDeclarationKind.Directive, HlslDeclarationKind.Struct,
            HlslDeclarationKind.ConstantBuffer, HlslDeclarationKind.Resource, HlslDeclarationKind.Resource,
            HlslDeclarationKind.Parameter, HlslDeclarationKind.Constant, HlslDeclarationKind.Prototype,
            HlslDeclarationKind.Function, HlslDeclarationKind.Typedef,
        ]);
        d.Select(x => x.Name).ShouldBe(["SCALE", "", "VSOut", "Frame", "Tex", "Samp", "Tint", "Pi", "Helper", "Main", "Color"]);
    }

    [Fact]
    public void BracesAndSemicolonsInCommentsAndStrings_AreNotCode()
    {
        List<HlslDeclaration> d = Split("""
            // a stray } and ; in a comment
            float4 A; /* { */ float4 B;
            #include "weird};name.h"
            float4 F() { /* } */ return 0; }
            """);

        d.Select(x => x.Name).ShouldBe(["A", "B", "", "F"]);
        d[1].Kind.ShouldBe(HlslDeclarationKind.Parameter);
        d[3].Kind.ShouldBe(HlslDeclarationKind.Function);
    }

    [Fact]
    public void StructEndsAtItsSemicolon_AndACbufferNeedsNone()
    {
        List<HlslDeclaration> d = Split("struct S { float a; }; cbuffer C { float b; } float c;");

        d.Select(x => (x.Kind, x.Name)).ShouldBe(
        [
            (HlslDeclarationKind.Struct, "S"), (HlslDeclarationKind.ConstantBuffer, "C"), (HlslDeclarationKind.Parameter, "c"),
        ]);
        d[0].Text.ShouldEndWith("};");
    }

    [Fact]
    public void AFunctionWithAnOutputSemanticAndAttributes_IsOneFunction()
    {
        List<HlslDeclaration> d = Split("""
            [numthreads(8, 8, 1)]
            void Cs(uint3 id : SV_DispatchThreadID) { }
            float4 Ps(float2 uv : TEXCOORD0) : SV_Target0 { return 0; }
            """);

        d.Count.ShouldBe(2);
        d.ShouldAllBe(x => x.Kind == HlslDeclarationKind.Function);
        d.Select(x => x.Name).ShouldBe(["Cs", "Ps"]);
    }

    [Fact]
    public void GlobalsWithInitializersRegistersAndSemantics_AreNotFunctions()
    {
        List<HlslDeclaration> d = Split("""
            float4 Tint = float4(1, 1, 1, 1);
            float4x4 World : register(c0);
            float Weights[4] = { 1, 2, 3, 4 };
            sampler s = sampler_state { Filter = LINEAR; };
            """);

        d.ShouldAllBe(x => x.Kind != HlslDeclarationKind.Function && x.Kind != HlslDeclarationKind.Prototype);
        d.Select(x => x.Name).ShouldBe(["Tint", "World", "Weights", "s"]);
        d[3].Kind.ShouldBe(HlslDeclarationKind.Resource);
    }

    [Fact]
    public void AMultiDeclaratorGlobal_NamesEveryDeclarator()
    {
        List<HlslDeclaration> d = Split("float a, b = 2, c[3]; Texture2DMS<float4, 4> ms, ms2;");

        d[0].Names.ShouldBe(["a", "b", "c"]);
        d[1].Names.ShouldBe(["ms", "ms2"]);
        d[1].Kind.ShouldBe(HlslDeclarationKind.Resource);
    }

    [Fact]
    public void QualifiersDecideConstantFromParameter()
    {
        List<HlslDeclaration> d = Split("static float A = 1; const float B = 2; uniform const float C; float D; groupshared float E[4];");

        d.Select(x => x.Kind).ShouldBe(
        [
            HlslDeclarationKind.Constant, HlslDeclarationKind.Constant, HlslDeclarationKind.Parameter,
            HlslDeclarationKind.Parameter, HlslDeclarationKind.Parameter,
        ]);
    }

    [Fact]
    public void AConditionalBlock_IsOneOpaqueDeclaration_AndNestingIsTracked()
    {
        List<HlslDeclaration> d = Split("""
            #if SM4
            #ifdef X
            float4 Inner;
            #endif
            float4 Outer;
            #else
            float4 Other;
            #endif
            float4 After;
            """);

        d.Count.ShouldBe(2);
        d[0].Kind.ShouldBe(HlslDeclarationKind.Conditional);
        d[0].Text.ShouldContain("float4 Other;", Case.Sensitive);
        d[1].Name.ShouldBe("After");
    }

    [Fact]
    public void ADirectiveInsideAFunctionBody_DoesNotSplitIt()
    {
        List<HlslDeclaration> d = Split("""
            float4 F()
            {
            #if X
                return 1;
            #else
                return 2;
            #endif
            }
            """);

        d.Count.ShouldBe(1);
        d[0].Kind.ShouldBe(HlslDeclarationKind.Function);
    }

    [Fact]
    public void BackslashContinuedDefines_AreOneDirective()
    {
        List<HlslDeclaration> d = Split("#define M(a) \\\n    ((a) + \\\n 1)\nfloat4 X;");

        d.Count.ShouldBe(2);
        d[0].Kind.ShouldBe(HlslDeclarationKind.Macro);
        d[0].Name.ShouldBe("M");
        d[1].Name.ShouldBe("X");
    }

    [Theory]
    [InlineData("#if X\nfloat a;\n", "never closed")]
    [InlineData("float a;\n#endif\n", "no matching #if")]
    [InlineData("float a;\n#else\n", "no matching #if")]
    [InlineData("float4 F() { return 0;", "not closed")]
    [InlineData("float a", "not closed")]
    public void Malformed_ReportsSd0666WithItsPosition(string source, string messagePart)
    {
        var result = HlslSourceSplitter.Split(source, "bad.hlsl");

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0666");
        result.Error.Single().File.ShouldBe("bad.hlsl");
        result.Error.Single().Message.ShouldContain(messagePart, Case.Sensitive);
        result.Error.Single().Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void PositionsAreTheOriginals_AndTextIsVerbatim()
    {
        List<HlslDeclaration> d = Split("\n\n  float4 A;  float4 B;\n\n\tfloat4 C;\n");

        d[0].StartLine.ShouldBe(3);
        d[0].StartColumn.ShouldBe(3);
        d[0].Text.ShouldBe("float4 A;");
        d[1].StartLine.ShouldBe(3);
        d[1].StartColumn.ShouldBe(14);
        d[2].StartLine.ShouldBe(5);
        d[2].StartColumn.ShouldBe(2);
    }

    [Fact]
    public void Normalize_IgnoresCommentsAndWhitespaceButNotStringsOrTokens()
    {
        HlslSourceSplitter.Normalize("float4   A; // note").ShouldBe(HlslSourceSplitter.Normalize("float4 /* x */ A;"));
        HlslSourceSplitter.Normalize("#include \"a.h\"").ShouldNotBe(HlslSourceSplitter.Normalize("#include \"b.h\""));
        HlslSourceSplitter.Normalize("float A;").ShouldNotBe(HlslSourceSplitter.Normalize("float B;"));
        HlslSourceSplitter.Normalize("\"// not a comment\"").ShouldBe("\"// not a comment\"");
    }
}
