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
        result.Value.Warnings.ShouldBeEmpty();
        return result.Value.Data;
    }
}
