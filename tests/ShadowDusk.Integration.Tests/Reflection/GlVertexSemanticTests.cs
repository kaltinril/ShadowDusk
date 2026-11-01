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
        "packing/RetValRowMatToVec33", "packing/CommaDeclRowMat", "packing/LeadingZeroRowMatToVec33",
        "packing/RowMatToColMat33", "packing/NestedStructRowMatToVec33", "packing/UnusedDegenerateStruct",
        "packing/TwoTechniques", "packing/Split1x4Both", "packing/Split1x4ToVec", "packing/SplitRow4x1Both",
        "packing/VecToSplit1x3", "packing/LocalStructShadow", "packing/LocalTypedefShadow",
    };

    [Theory]
    [MemberData(nameof(PackingShapes))]
    public async Task PackingShape_RecordsAndVaryingsMatchMgfxc(string shape)
    {
        MgfxBlobReader subject = MgfxBlobReader.Parse(await CompileAsync(shape));
        MgfxBlobReader golden = Golden(shape);

        Records(subject).ShouldBe(Records(golden));
        // The VS writes exactly mgfxc's varyings. mgfxc's PS declares only the registers it reads
        // (a PS reading m[1] of a row_major matrix declares just that one); ours rebuilds the whole
        // matrix, so its PS may declare more, but every one must be written by the VS and every
        // one mgfxc's PS reads must be there.
        Varyings(subject, vertex: true).ShouldBe(Varyings(golden, vertex: true));
        Varyings(golden, vertex: false).Except(Varyings(subject, vertex: false)).ShouldBeEmpty();
        Varyings(subject, vertex: false).Except(Varyings(subject, vertex: true)).ShouldBeEmpty();
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

    [Fact]
    public async Task EachStage_IsLaidOutFromItsOwnEntryPoint()
    {
        // A row_major VS output read by a column_major PS input (mgfxc compiles it): the VS packs
        // rows, the PS rebuilds from columns, each as fxc lays out its own declaration.
        var r = MgfxBlobReader.Parse(await CompileAsync("packing/RowMatToColMat33"));
        Encoding.ASCII.GetString(r.Shaders.Single(s => s.IsVertex).Bytecode)
            .ShouldContain("vTexCoord2.xyz = vs_oT2_mat[0];", Case.Sensitive);
        Encoding.ASCII.GetString(r.Shaders.Single(s => !s.IsVertex).Bytecode)
            .ShouldContain("mat3(vec3(vTexCoord2.x, vTexCoord3.x, vTexCoord4.x)", Case.Sensitive);
    }

    [Fact]
    public async Task DegenerateInterpolant_IsSplitOverOneComponentRegisters()
    {
        var r = MgfxBlobReader.Parse(await CompileAsync("packing/Split1x4Both"));
        string vs = Encoding.ASCII.GetString(r.Shaders.Single(s => s.IsVertex).Bytecode);
        string ps = Encoding.ASCII.GetString(r.Shaders.Single(s => !s.IsVertex).Bytecode);

        vs.ShouldContain("vTexCoord4.x = vs_oT1_vec.w;", Case.Sensitive);
        ps.ShouldContain("vec4(vTexCoord1.x, vTexCoord2.x, vTexCoord3.x, vTexCoord4.x)", Case.Sensitive);
    }

    [Theory]
    // A semantic on a struct-typed member whose struct holds a matrix: the matrix's registers
    // come from the outer semantic, a shape the declaration scan does not model (mgfxc compiles it).
    [InlineData("""
        struct Inner { row_major float3x3 M; };
        struct VSOut { float4 Position : POSITION0; Inner I : TEXCOORD2; };
        VSOut VS(float4 p : POSITION0) { VSOut o; o.Position = p; o.I.M = float3x3(p.xyz, p.yzx, p.zxy); return o; }
        float4 PS(float4 a : TEXCOORD2) : COLOR0 { return a; }
        """, "Inner")]
    // A float1x4 VERTEX INPUT: mgfxc emits four attributes (TEXCOORD2..5), one register each.
    // (As an interpolant the same shape is split like fxc: packing/Split1x4Both.)
    [InlineData("""
        struct VSIn { float4 Position : POSITION0; float1x4 W : TEXCOORD2; };
        float4 VS(VSIn i) : POSITION0 { return i.Position + float4(i.W[0][0], i.W[0][1], i.W[0][2], i.W[0][3]); }
        float4 PS() : COLOR0 { return 1; }
        """, "1x4")]
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
        // Nothing may warn. A non-square matrix interpolant used to leave a mat4x3 / mat3x4 / mat2x4
        // in one stage's GLSL (SD0403: strict front ends and DesktopGL4 refuse it); Rule 17
        // (issue #453) now holds it as a zero-padded square matN, so no non-square type survives.
        result.Value.Warnings.ShouldBeEmpty();
        foreach (var stage in MgfxBlobReader.Parse(result.Value.Data).Shaders)
            Encoding.ASCII.GetString(stage.Bytecode).ShouldNotMatch(@"mat[234]x[234]");
        return result.Value.Data;
    }
}
