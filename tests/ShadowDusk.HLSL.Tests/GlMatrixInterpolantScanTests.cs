#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// The matrix declarations each entry point's signature carries
/// (<see cref="GlSamplerSlots.EntryPointInterpolants"/>): DXC emits byte-identical GLSL for a
/// <c>row_major</c> and a <c>column_major</c> interpolant, while fxc packs the first one row per
/// register and the second one column per register (measured against <c>mgfxc</c> 3.8.4.1 in real
/// MonoGame DesktopGL), so the OpenGL rewrite takes the layout from the entry point's own
/// declarations. Read off the preprocessed view, so an inactive branch does not count.
/// </summary>
public sealed class GlMatrixInterpolantScanTests
{
    private static IReadOnlyDictionary<string, EntryPointMatrixInterpolants> Scan(string body)
    {
        string flattened = PlatformMacros.For(PlatformTarget.OpenGL).ToTextPrepend("t.fx") + body;
        var parsed = FxPreParser.Parse(flattened, "t.fx");
        parsed.IsSuccess.ShouldBeTrue(parsed.IsFailure ? parsed.Error.Message : "");
        var slots = FxPreParser.CollectGlSamplerSlots(flattened, "t.fx", parsed.Value);
        slots.IsSuccess.ShouldBeTrue(slots.IsFailure ? slots.Error.Message : "");
        return slots.Value.EntryPointInterpolants;
    }

    [Fact]
    public void StructReturn_RecordsOutputsWithShapeAndMajorness()
    {
        var vs = Scan("""
            struct VSOut
            {
                float4 Position : POSITION0;
                float3x3 Frame : TEXCOORD2;
                row_major float4x3 M : texcoord05;
                column_major float2x2 N : TEXCOORD;
                float3 NotAMatrix : TEXCOORD9;
            };
            VSOut VS(float4 p : POSITION0) { VSOut o = (VSOut)0; return o; }
            """)["VS"];

        vs.Inputs.ShouldBeEmpty();
        vs.Unmodelled.ShouldBeEmpty();
        vs.Outputs.ShouldBe(new[]
        {
            new MatrixInterpolantDeclaration("TEXCOORD2", "Frame", 3, 3, false),
            new MatrixInterpolantDeclaration("TEXCOORD5", "M", 4, 3, true),   // index by value
            new MatrixInterpolantDeclaration("TEXCOORD0", "N", 2, 2, false),
        });
    }

    [Fact]
    public void ReturnSemantic_OutParams_TypedefsAndTemplates_AreSeen()
    {
        var vs = Scan("""
            typedef row_major float3x3 RowFrame;
            typedef float2x4 Plain;
            row_major float3x3 VS(float4 p : POSITION0, out float4 o : POSITION0, out RowFrame B : TEXCOORD3,
                                  out matrix<float, 2, 3> C : TEXCOORD6, inout Plain D : TEXCOORD8, uniform float3x3 U) : TEXCOORD0
            { o = p; return (float3x3)0; }
            """)["VS"];

        vs.Outputs.ShouldBe(new[]
        {
            new MatrixInterpolantDeclaration("TEXCOORD0", "VS", 3, 3, true),
            new MatrixInterpolantDeclaration("TEXCOORD3", "B", 3, 3, true),
            new MatrixInterpolantDeclaration("TEXCOORD6", "C", 2, 3, false),
            new MatrixInterpolantDeclaration("TEXCOORD8", "D", 2, 4, false),
        });
        vs.Inputs.ShouldBe(new[] { new MatrixInterpolantDeclaration("TEXCOORD8", "D", 2, 4, false) });
    }

    [Fact]
    public void CommaDeclarators_ShareTheTypeAndMajorness()
    {
        Scan("""
            struct O { float4 P : POSITION0; row_major float3x3 A : TEXCOORD0, B : TEXCOORD3; };
            O VS() { return (O)0; }
            """)["VS"].Outputs.Select(d => (d.Semantic, d.RowMajor))
            .ShouldBe(new[] { ("TEXCOORD0", true), ("TEXCOORD3", true) });
    }

    [Fact]
    public void NestedStructMembers_AreSeen_OuterSemanticOnAMatrixStruct_IsUnmodelled()
    {
        var found = Scan("""
            struct Inner { row_major float3x3 M : TEXCOORD0; };
            struct Holder { float3x3 M; };
            struct A { float4 P : POSITION0; Inner I; };
            struct B { float4 P : POSITION0; Holder H : TEXCOORD0; };
            A VSA() { return (A)0; }
            B VSB() { return (B)0; }
            """);

        found["VSA"].Outputs.ShouldBe(new[] { new MatrixInterpolantDeclaration("TEXCOORD0", "M", 3, 3, true) });
        found["VSB"].Outputs.ShouldBeEmpty();
        found["VSB"].Unmodelled.Single().ShouldContain("Holder", Case.Sensitive);
    }

    [Fact]
    public void EachEntryPoint_HasItsOwnDeclarations()
    {
        // Two techniques may declare one semantic differently; and an unused struct is nobody's.
        var found = Scan("""
            struct Unused { float1x3 Weird : TEXCOORD1; };
            struct OA { float4 P : POSITION0; row_major float3x3 M : TEXCOORD0; };
            struct OB { float4 P : POSITION0; float3x3 M : TEXCOORD0; };
            OA VSA() { return (OA)0; }
            OB VSB() { return (OB)0; }
            float4 PSA(OA i) : COLOR0 { return 1; }
            """);

        found["VSA"].Outputs.Single().RowMajor.ShouldBeTrue();
        found["VSB"].Outputs.Single().RowMajor.ShouldBeFalse();
        found["PSA"].Inputs.Single().RowMajor.ShouldBeTrue();
        found["PSA"].Outputs.ShouldBeEmpty();
        found.Values.SelectMany(e => e.Inputs.Concat(e.Outputs)).ShouldNotContain(d => d.Name == "Weird");
    }

    [Fact]
    public void PragmaPackMatrix_DoesNotMakeAnInterpolantRowMajor()
    {
        // fxc ignores #pragma pack_matrix for interpolants (measured: mgfxc still packs by columns).
        Scan("""
            #pragma pack_matrix(row_major)
            struct O { float4 Position : POSITION0; float3x3 Frame : TEXCOORD2; };
            O VS() { return (O)0; }
            """)["VS"].Outputs.Single().RowMajor.ShouldBeFalse();
    }

    [Fact]
    public void InactiveBranches_AndOverloads()
    {
        var found = Scan("""
            #if !OPENGL
            struct O { float4 P : POSITION0; row_major float3x3 F : TEXCOORD2; };
            O VS() { return (O)0; }
            #endif
            float4 Twice(float4 a) { return a; }
            float4 Twice(float3 a) { return a.xyzz; }
            """);

        found.ContainsKey("VS").ShouldBeFalse();
        found["Twice"].Unmodelled.Single().ShouldContain("overloads", Case.Sensitive);
    }

    [Fact]
    public void LocalStructsAndTypedefs_DoNotReplaceTheFileScopeOnes()
    {
        // A struct or typedef declared inside a function body is that function's own (mgfxc
        // accepts the shadowing); the entry point's signature uses the file-scope definition.
        var found = Scan("""
            struct VSOut { float4 Position : POSITION0; row_major float3x3 M : TEXCOORD1; };
            typedef row_major float3x3 M33;
            float3 Helper(float3 v)
            {
                struct VSOut { float4 Position : POSITION0; float3x3 M : TEXCOORD1; };
                typedef float3x3 M33;
                VSOut t = (VSOut)0; M33 u = (M33)0; return t.M[0] + u[0];
            }
            VSOut VS() { return (VSOut)0; }
            float4 PS(M33 m : TEXCOORD1) : COLOR0 { return 1; }
            """);

        found["VS"].Outputs.Single().RowMajor.ShouldBeTrue();
        found["PS"].Inputs.Single().RowMajor.ShouldBeTrue();
    }

    [Fact]
    public void ATypeNameDefinedTwiceAtFileScope_IsUnmodelled()
    {
        var vs = Scan("""
            typedef row_major float3x3 M33;
            typedef float3x3 M33;
            void VS(out float4 p : POSITION0, out M33 m : TEXCOORD1) { p = 0; m = (float3x3)0; }
            """)["VS"];

        vs.Outputs.ShouldBeEmpty();
        vs.Unmodelled.Single().ShouldContain("'M33'", Case.Sensitive);
    }

    [Fact]
    public void AStructTypedef_IsTheStruct()
    {
        Scan("""
            struct VSOut { float4 Position : POSITION0; row_major float3x3 M : TEXCOORD1; };
            typedef VSOut PSIn;
            float4 PS(PSIn i) : COLOR0 { return 1; }
            """)["PS"].Inputs.Single().ShouldBe(new MatrixInterpolantDeclaration("TEXCOORD1", "M", 3, 3, true));
    }

    [Fact]
    public void DeepWideStructNests_AreBounded_NotExponential()
    {
        // Ten struct-typed members per level, many levels deep, each leaf a matrix with a semantic:
        // naive expansion is 10^levels; the memoized, bounded scan reports it unmodelled at once.
        var sb = new System.Text.StringBuilder("struct S0 { float3x3 M : TEXCOORD0; };\n");
        for (int level = 1; level <= 12; level++)
        {
            sb.Append($"struct S{level} {{ ");
            for (int m = 0; m < 10; m++) sb.Append($"S{level - 1} m{m}; ");
            sb.Append("};\n");
        }
        sb.Append("S12 VS() { return (S12)0; }\n");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var vs = Scan(sb.ToString())["VS"];
        watch.Stop();

        vs.Outputs.Count.ShouldBeLessThanOrEqualTo(GlEntryPointInterpolantScanner.MaxFlattened);
        vs.Unmodelled.ShouldContain(u => u.Contains("flattens to more than", StringComparison.Ordinal));
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("float1x3", false, true)]
    [InlineData("float3x1", false, false)]
    [InlineData("float3x1", true, true)]
    [InlineData("float1x3", true, false)]
    [InlineData("float1x1", false, false)]
    public void DegenerateShapes_AreFlagged(string type, bool rowMajor, bool degenerate)
    {
        Scan($"float4 PS({(rowMajor ? "row_major " : "")}{type} X : TEXCOORD1) : COLOR0 {{ return 1; }}")["PS"]
            .Inputs.Single().IsDegenerate.ShouldBe(degenerate);
    }
}
