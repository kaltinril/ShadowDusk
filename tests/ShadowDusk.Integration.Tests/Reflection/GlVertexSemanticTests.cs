#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// OpenGL vertex-input semantics beyond POSITION / COLOR / TEXCOORD / NORMAL, and matrix
/// interpolants, against real <c>mgfxc</c> 3.8.4.1 <c>/Profile:OpenGL</c> goldens
/// (<c>tests/fixtures/golden/vertex-semantics/</c>, sources beside them). Until this fix ShadowDusk
/// refused every one of these shapes with SD0210 (KNI EffectTest's <c>DeferredBasicEffect</c> at
/// <c>BINORMAL0</c>), while mgfxc compiles them.
///
/// <para>MonoGame's and KNI's GL runtimes bind each <c>VertexElement</c> to the attribute whose
/// record carries its (usage, index), and link the two stages by varying NAME, so what is compared
/// per shape is: the vertex shader's attribute records (name, usage, index, location) in order,
/// equal to mgfxc's; and the set of <c>varying</c> names each stage declares, equal to mgfxc's.
/// The real-engine renders are <c>validation/VertexSemanticsGl</c> (MonoGame DesktopGL) and
/// <c>validation/VertexSemanticsKniGl</c> (KNI SDL2.GL, MGFX v10 and KNIFX).</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class GlVertexSemanticTests
{
    public static TheoryData<string> Shapes() => new()
    {
        "Binormal", "Tangent", "BlendIndices", "BlendWeight", "PointSize", "Fog", "Depth",
        "TexCoordIndex", "MixedCase", "SemanticsRender", "TangentFrameRender", "DeferredGBufferShape",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task AttributeRecords_MatchMgfxc(string shape)
    {
        MgfxBlobReader subject = MgfxBlobReader.Parse(await CompileAsync(shape));
        MgfxBlobReader golden = Golden(shape);

        Records(subject).ShouldBe(Records(golden),
            customMessage: "every attribute record (name, usage, index, location) must be mgfxc's, in order");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task VaryingNames_MatchMgfxc(string shape)
    {
        MgfxBlobReader subject = MgfxBlobReader.Parse(await CompileAsync(shape));
        MgfxBlobReader golden = Golden(shape);

        foreach (bool vertex in new[] { true, false })
        {
            Varyings(subject, vertex).ShouldBe(Varyings(golden, vertex),
                customMessage: $"the {(vertex ? "vertex" : "pixel")} stage must declare mgfxc's varyings (the runtime links by name)");
        }
    }

    // The usage bytes each per-semantic shape must carry, independent of the golden: MonoGame's
    // VertexElementUsage (Position=0 ... PointSize=10), each semantic at index 0 and a non-zero index.
    [Theory]
    [InlineData("Binormal",      4, 0, 4, 2)]
    [InlineData("Tangent",       5, 0, 5, 7)]
    [InlineData("BlendIndices",  6, 0, 6, 1)]
    [InlineData("BlendWeight",   7, 0, 7, 1)]
    [InlineData("PointSize",    10, 0, 10, 1)]
    [InlineData("Fog",           9, 0, 9, 1)]
    [InlineData("Depth",         8, 0, 8, 1)]
    [InlineData("TexCoordIndex", 2, 0, 2, 3)]
    [InlineData("MixedCase",     4, 0, 5, 1)]
    public async Task PerSemanticShape_CarriesTheExpectedUsageAndIndex(
        string shape, byte usageA, byte indexA, byte usageB, byte indexB)
    {
        var attributes = MgfxBlobReader.Parse(await CompileAsync(shape)).Shaders
            .Single(s => s.IsVertex).Attributes;

        attributes.Select(a => (a.Usage, a.Index)).ShouldBe(new[]
        {
            ((byte)0, (byte)0), (usageA, indexA), (usageB, indexB),
        });
    }

    // packing/: the two stages declare one matrix interpolant differently, so these pin fxc's
    // register layout (rendered vs mgfxc in validation/VertexSemanticsGl and VertexSemanticsKniGl).
    public static TheoryData<string> PackingShapes() => new()
    {
        "packing/MatToVec33", "packing/RowMatToVec33", "packing/VecToMat33", "packing/VecToRowMat33",
        "packing/MatToVec43", "packing/RowMatToVec43", "packing/MatToVec34", "packing/RowMatToVec34",
        "packing/MatToVec22", "packing/RowMatToVec22", "packing/VecToMat43", "packing/VecToRowMat43",
        "packing/VecToMat34", "packing/VecToRowMat22", "packing/TypedefRowMatToVec33",
        "packing/PragmaRowMajorMatToVec33", "packing/DuplicatePosition",
    };

    private static readonly HashSet<string> NonSquare = new(StringComparer.Ordinal)
    {
        "packing/MatToVec43", "packing/RowMatToVec43", "packing/MatToVec34", "packing/RowMatToVec34",
        "packing/VecToMat43", "packing/VecToRowMat43", "packing/VecToMat34",
    };

    [Theory]
    [MemberData(nameof(PackingShapes))]
    public async Task PackingShape_RecordsAndVaryingsMatchMgfxc(string shape)
    {
        MgfxBlobReader subject = MgfxBlobReader.Parse(await CompileAsync(shape));
        MgfxBlobReader golden = Golden(shape);

        Records(subject).ShouldBe(Records(golden));
        foreach (bool vertex in new[] { true, false })
            Varyings(subject, vertex).ShouldBe(Varyings(golden, vertex));
    }

    [Fact]
    public async Task RowMajorMatrixInterpolant_IsPackedByRows_ColumnMajorByColumns()
    {
        string RowVs(MgfxBlobReader r) => Encoding.ASCII.GetString(r.Shaders.Single(s => s.IsVertex).Bytecode);

        RowVs(MgfxBlobReader.Parse(await CompileAsync("packing/RowMatToVec33")))
            .ShouldContain("vTexCoord2.xyz = vs_oT2_mat[0];", Case.Sensitive);
        RowVs(MgfxBlobReader.Parse(await CompileAsync("packing/MatToVec33")))
            .ShouldContain("vTexCoord2.xyz = vec3(vs_oT2_mat[0][0], vs_oT2_mat[1][0], vs_oT2_mat[2][0]);", Case.Sensitive);
        RowVs(MgfxBlobReader.Parse(await CompileAsync("packing/PragmaRowMajorMatToVec33")))
            .ShouldContain("vTexCoord2.xyz = vec3(vs_oT2_mat[0][0], vs_oT2_mat[1][0], vs_oT2_mat[2][0]);", Case.Sensitive);
    }

    [Theory]
    // mgfxc compiles this one (a row_major VS output read as a column_major PS input); which
    // declaration belongs to which stage is not modelled, so neither layout is provably fxc's.
    [InlineData("""
        struct VSOut { float4 Position : POSITION0; row_major float3x3 F : TEXCOORD2; };
        struct PSIn { float4 Position : POSITION0; float3x3 F : TEXCOORD2; };
        VSOut VS(float4 p : POSITION0) { VSOut o; o.Position = p; o.F = float3x3(p.xyz, p.yzx, p.zxy); return o; }
        float4 PS(PSIn i) : COLOR0 { return float4(i.F[0], 1); }
        """, "row_major")]
    // fxc spreads a float1x3 over three registers of one component; DXC hands the rewrite a vec3.
    [InlineData("""
        struct VSOut { float4 Position : POSITION0; float1x3 A : TEXCOORD2; };
        VSOut VS(float4 p : POSITION0) { VSOut o; o.Position = p; o.A = (float1x3)p.xyz; return o; }
        float4 PS(VSOut i) : COLOR0 { return float4((float3)i.A, 1); }
        """, "1x3")]
    public async Task UnmodelledInterpolantLayout_FailsLoudly(string body, string expected)
    {
        string src = body + "\ntechnique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }\n";
        var result = await new EffectCompiler().CompileAsync(src, new CompilerOptions { Target = PlatformTarget.OpenGL });

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain(e => e.Code == "SD0210" && e.Message.Contains(expected, StringComparison.Ordinal)
                                        && e.Message.Contains("TEXCOORD2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownSemantic_StillFailsLoudly()
    {
        // mgfxc's fxc vs_3_0 refuses it too (X4502); the GL target must not invent a binding.
        const string src = """
float4 VS(float4 p : POSITION0, float4 x : MYSEM0) : POSITION0 { return p + x; }
float4 PS() : COLOR0 { return 1; }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
""";
        var result = await new EffectCompiler().CompileAsync(src, new CompilerOptions { Target = PlatformTarget.OpenGL });

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain(e => e.Code == "SD0210" && e.Message.Contains("MYSEM0", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> Records(MgfxBlobReader r) =>
        r.Shaders.Where(s => s.IsVertex)
                 .SelectMany(s => s.Attributes)
                 .Select(a => $"{a.Name}:{a.Usage}/{a.Index}@{a.Location}")
                 .ToList();

    private static IReadOnlyList<string> Varyings(MgfxBlobReader r, bool vertex) =>
        r.Shaders.Where(s => s.IsVertex == vertex)
                 .SelectMany(s => Regex.Matches(Encoding.ASCII.GetString(s.Bytecode), @"\bvarying\s+vec4\s+(\w+)\s*;")
                                       .Select(m => m.Groups[1].Value))
                 .Distinct()
                 .Order(StringComparer.Ordinal)
                 .ToList();

    private static MgfxBlobReader Golden(string shape)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "vertex-semantics", $"{shape}.OpenGL.mgfx");
        File.Exists(path).ShouldBeTrue($"mgfxc golden missing: {path}");
        return MgfxBlobReader.Parse(File.ReadAllBytes(path));
    }

    private static async Task<byte[]> CompileAsync(string shape)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "vertex-semantics", shape + ".fx");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await new EffectCompiler().CompileAsync(
            await File.ReadAllTextAsync(path, cts.Token),
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = shape + ".fx" },
            cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : "");
        // A non-square matrix interpolant puts a mat4x3 / mat3x4 / mat2x4 in at least one stage's
        // GLSL, which is the registered SD0403 warning (strict front ends refuse it); nothing else
        // may warn.
        result.Value.Warnings.Where(w => !(NonSquare.Contains(shape) && w.Code == "SD0403")).ShouldBeEmpty();
        if (NonSquare.Contains(shape))
            result.Value.Warnings.ShouldContain(w => w.Code == "SD0403");
        return result.Value.Data;
    }
}
