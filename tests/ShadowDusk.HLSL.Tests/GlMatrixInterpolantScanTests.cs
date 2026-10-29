#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// The matrix-interpolant declarations the OpenGL rewrite needs from the HLSL source
/// (<see cref="GlSamplerSlots.MatrixInterpolants"/>): DXC emits byte-identical GLSL for a
/// <c>row_major</c> and a <c>column_major</c> interpolant, while fxc packs the first one row per
/// register and the second one column per register (measured against <c>mgfxc</c> 3.8.4.1 in real
/// MonoGame DesktopGL). Read off the preprocessed view, so an inactive branch does not count.
/// </summary>
public sealed class GlMatrixInterpolantScanTests
{
    private static IReadOnlyList<MatrixInterpolantDeclaration> Scan(string body)
    {
        string flattened = PlatformMacros.For(PlatformTarget.OpenGL).ToTextPrepend("t.fx") + body;
        var parsed = FxPreParser.Parse(flattened, "t.fx");
        parsed.IsSuccess.ShouldBeTrue(parsed.IsFailure ? parsed.Error.Message : "");
        var slots = FxPreParser.CollectGlSamplerSlots(flattened, "t.fx", parsed.Value);
        slots.IsSuccess.ShouldBeTrue(slots.IsFailure ? slots.Error.Message : "");
        return slots.Value.MatrixInterpolants;
    }

    [Fact]
    public void StructMembers_RecordShapeAndMajorness()
    {
        var found = Scan("""
            struct VSOut
            {
                float4 Position : POSITION0;
                float3x3 Frame : TEXCOORD2;
                row_major float4x3 M : texcoord5;
                column_major float2x2 N : TEXCOORD;
                float3 NotAMatrix : TEXCOORD9;
            };
            """);

        found.ShouldBe(new[]
        {
            new MatrixInterpolantDeclaration("TEXCOORD2", "Frame", 3, 3, false),
            new MatrixInterpolantDeclaration("TEXCOORD5", "M", 4, 3, true),
            new MatrixInterpolantDeclaration("TEXCOORD0", "N", 2, 2, false),
        });
    }

    [Fact]
    public void OutParameter_TypedefAndMatrixTemplate_AreSeen()
    {
        var found = Scan("""
            typedef row_major float3x3 RowFrame;
            typedef float2x4 Plain;
            void VS(float4 p : POSITION0, out float4 o : POSITION0, out row_major float3x3 A : TEXCOORD0,
                    out RowFrame B : TEXCOORD3, out matrix<float, 2, 3> C : TEXCOORD6, out Plain D : TEXCOORD8) { }
            """);

        found.ShouldBe(new[]
        {
            new MatrixInterpolantDeclaration("TEXCOORD0", "A", 3, 3, true),
            new MatrixInterpolantDeclaration("TEXCOORD3", "B", 3, 3, true),
            new MatrixInterpolantDeclaration("TEXCOORD6", "C", 2, 3, false),
            new MatrixInterpolantDeclaration("TEXCOORD8", "D", 2, 4, false),
        });
    }

    [Fact]
    public void PragmaPackMatrix_DoesNotMakeAnInterpolantRowMajor()
    {
        // fxc ignores #pragma pack_matrix for interpolants (measured: mgfxc still packs by columns).
        Scan("""
            #pragma pack_matrix(row_major)
            struct VSOut { float4 Position : POSITION0; float3x3 Frame : TEXCOORD2; };
            """).Single().RowMajor.ShouldBeFalse();
    }

    [Fact]
    public void GlobalsAndInactiveBranches_AreNotInterpolants()
    {
        Scan("""
            row_major float4x4 World : TEXCOORD0;
            #if !OPENGL
            struct Dead { row_major float3x3 F : TEXCOORD2; };
            #endif
            """).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("float1x3", false, true)]
    [InlineData("float3x1", false, false)]
    [InlineData("float3x1", true, true)]
    [InlineData("float1x3", true, false)]
    [InlineData("float1x1", false, false)]
    public void DegenerateShapes_AreFlagged(string type, bool rowMajor, bool degenerate)
    {
        Scan($"struct S {{ {(rowMajor ? "row_major " : "")}{type} X : TEXCOORD1; }};").Single()
            .IsDegenerate.ShouldBe(degenerate);
    }
}
