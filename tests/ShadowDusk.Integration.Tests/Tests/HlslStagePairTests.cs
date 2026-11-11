#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// End-to-end proof for the two-source HLSL frontend (<see cref="HlslStageCompiler"/>): a
/// vertex source and a pixel source compile to <b>exactly the bytes</b> of the same effect
/// written by hand as one <c>.fx</c>, on every target and container. That identity is what
/// shows the frontend is a pure text step in front of the unchanged pipeline: if it ever moved
/// an output byte, this fails. It is a distinct evidence axis, never <c>mgfxc</c>-equivalence of
/// the input (see <c>docs/validation-matrix.md</c>); the downstream half is already proven.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HlslStagePairTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "hlsl-stages");

    private sealed record Pair(string Name, string VertexEntry, string PixelEntry);

    private static readonly Dictionary<string, Pair> Pairs = new()
    {
        ["Textured"] = new("Textured", "VSMain", "PSMain"),
        ["SharedMain"] = new("SharedMain", "main", "main"),
        ["Sm3"] = new("Sm3", "VSMain", "PSMain"),
    };

    // Every container/target combination the pipeline supports, by a stable label.
    private static CompilerOptions OptionsFor(string label, string pair) => label switch
    {
        "OpenGL" => new() { Target = PlatformTarget.OpenGL, SourceFileName = pair + ".fx" },
        "OpenGL-v11" => new() { Target = PlatformTarget.OpenGL, MgfxVersion = 11, SourceFileName = pair + ".fx" },
        "DirectX11" => new() { Target = PlatformTarget.DirectX, SourceFileName = pair + ".fx" },
        "DirectX12" => new() { Target = PlatformTarget.DirectX12, SourceFileName = pair + ".fx" },
        "Vulkan" => new() { Target = PlatformTarget.Vulkan, SourceFileName = pair + ".fx" },
        "Fna" => new() { Target = PlatformTarget.Fna, SourceFileName = pair + ".fx" },
        "Knifx-GL-v11" => new() { Profile = CapabilityProfile.KniGL_4_02, SourceFileName = pair + ".fx" },
        "Knifx-GL-v12" => new() { Profile = CapabilityProfile.KniGL_4_03, SourceFileName = pair + ".fx" },
        "Knifx-DX11" => new()
        {
            Target = PlatformTarget.DirectX, Container = EffectContainer.Knifx, SourceFileName = pair + ".fx",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, null),
    };

    public static IEnumerable<object[]> Cases()
    {
        string[] modern = ["OpenGL", "OpenGL-v11", "DirectX11", "DirectX12", "Vulkan", "Knifx-GL-v11", "Knifx-GL-v12", "Knifx-DX11"];
        foreach (string label in modern)
            yield return ["Textured", label];

        // SharedMain has a cbuffer and uniform globals in the same stage. Vulkan allows one constant
        // buffer per stage (SD0026), so the hand-written effect itself is refused there: not a pair issue.
        foreach (string label in modern.Where(l => l != "Vulkan"))
            yield return ["SharedMain", label];

        // The Shader Model 3 pair is the one FNA (fx_2_0) can read; it also runs on the GL/DX targets.
        foreach (string label in new[] { "Fna", "OpenGL", "DirectX11", "Knifx-GL-v12" })
            yield return ["Sm3", label];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Pair_CompilesToTheBytesOfTheHandMergedEffect(string pairName, string label)
    {
        Pair pair = Pairs[pairName];
        CompilerOptions options = OptionsFor(label, pairName);
        var compiler = new EffectCompiler();

        var reference = await compiler.CompileAsync(Read(pairName + ".fx"), options);
        reference.IsSuccess.ShouldBeTrue(Describe(reference));

        var fromPair = await HlslStageCompiler.CompileAsync(compiler, Sources(pair), options);
        fromPair.IsSuccess.ShouldBeTrue(Describe(fromPair));

        fromPair.Value.Data.ShouldBe(reference.Value.Data, $"{pairName} on {label}");
        fromPair.Value.Target.ShouldBe(reference.Value.Target);
        fromPair.Value.Parameters.Select(p => p.Name).ShouldBe(reference.Value.Parameters.Select(p => p.Name));
    }

    [Fact]
    public async Task SyncAndAsync_AgreeByteForByte()
    {
        Pair pair = Pairs["Textured"];
        var options = OptionsFor("OpenGL", pair.Name);
        var compiler = new EffectCompiler();

        var sync = HlslStageCompiler.Compile(compiler, Sources(pair), options);
        var async = await HlslStageCompiler.CompileAsync(compiler, Sources(pair), options);

        sync.IsSuccess.ShouldBeTrue(Describe(sync));
        async.IsSuccess.ShouldBeTrue(Describe(async));
        sync.Value.Data.ShouldBe(async.Value.Data);
    }

    [Fact]
    public async Task SharedDeclarations_AppearOnceAsEffectParameters_AndStageOnlyOnesSurvive()
    {
        var result = await HlslStageCompiler.CompileAsync(
            new EffectCompiler(), Sources(Pairs["SharedMain"]), OptionsFor("DirectX11", "SharedMain"));

        result.IsSuccess.ShouldBeTrue(Describe(result));
        string[] names = result.Value.Parameters.Select(p => p.Name).ToArray();

        // Shared cbuffer members once each; Time is vertex-only and Exposure is pixel-only.
        names.ShouldContain("ViewProjection");
        names.ShouldContain("Tint");
        names.ShouldContain("Time");
        names.ShouldContain("Exposure");
        names.Count(n => n == "Tint").ShouldBe(1);
        names.Count(n => n == "ViewProjection").ShouldBe(1);
    }

    // ---- Location contract (project rule): plant a known error at a known line behind known drift ----

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task ErrorInThePixelSource_IsReportedInThePixelFile_AtItsOwnLineAndColumn(PlatformTarget target)
    {
        // The pixel source repeats the vertex source's struct, which the merge removes: the generated
        // effect's lines have drifted from the pixel file's by the time DXC reads the error.
        string pixel = Read("Textured.ps.hlsl").Replace("* Tint;", "* Tintt;");
        (int line, int column) = Locate(pixel, "Tintt");

        var result = await HlslStageCompiler.CompileAsync(
            new EffectCompiler(),
            new HlslStageSources(Read("Textured.vs.hlsl"), pixel, "Textured.vs.hlsl", "Textured.ps.hlsl", "VSMain", "PSMain"),
            new CompilerOptions { Target = target });

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.First(e => e.Severity == ShaderErrorSeverity.Error);
        error.File.ShouldBe("Textured.ps.hlsl");
        error.Line.ShouldBe(line);
        error.Column.ShouldBe(column);
        error.Message.ShouldContain("Tintt", Case.Sensitive);
    }

    [Fact]
    public async Task ErrorInTheVertexSource_IsReportedInTheVertexFile()
    {
        string vertex = Read("Textured.vs.hlsl").Replace("input.TexCoord;", "input.TexCoordd;");
        (int line, int column) = Locate(vertex, "TexCoordd");

        var result = await HlslStageCompiler.CompileAsync(
            new EffectCompiler(),
            new HlslStageSources(vertex, Read("Textured.ps.hlsl"), "Textured.vs.hlsl", "Textured.ps.hlsl", "VSMain", "PSMain"),
            new CompilerOptions { Target = PlatformTarget.OpenGL });

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.First(e => e.Severity == ShaderErrorSeverity.Error);
        error.File.ShouldBe("Textured.vs.hlsl");
        error.Line.ShouldBe(line);
        error.Column.ShouldBe(column);
    }

    [Fact]
    public async Task ValidateAsync_ReportsAMergeFailureForEveryTarget()
    {
        var sources = new HlslStageSources("float4 a;", "float4 b;", VertexEntry: "nope", PixelEntry: "nada");

        var report = await HlslStageCompiler.ValidateAsync(new EffectCompiler(), sources);

        report.IsValid.ShouldBeFalse();
        report.Targets.Select(t => t.Target).ShouldBe([PlatformTarget.OpenGL, PlatformTarget.DirectX]);
        report.Targets.ShouldAllBe(t => t.Errors.Any(e => e.Code == "SD0661"));
    }

    [Fact]
    public async Task ValidateAsync_OfAGoodPair_IsValidOnTheDefaultTargets()
    {
        var report = await HlslStageCompiler.ValidateAsync(new EffectCompiler(), Sources(Pairs["Textured"]));

        report.IsValid.ShouldBeTrue(report.ToString());
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static HlslStageSources Sources(Pair pair) => new(
        Read(pair.Name + ".vs.hlsl"), Read(pair.Name + ".ps.hlsl"),
        pair.Name + ".vs.hlsl", pair.Name + ".ps.hlsl", pair.VertexEntry, pair.PixelEntry);

    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(Fixtures, file)).Replace("\r\n", "\n");

    private static (int Line, int Column) Locate(string text, string token)
    {
        int index = text.IndexOf(token, StringComparison.Ordinal);
        index.ShouldBeGreaterThanOrEqualTo(0);
        int line = 1 + text.AsSpan(0, index).Count('\n');
        int column = index - (text.LastIndexOf('\n', index) + 1) + 1;
        return (line, column);
    }

    private static string Describe(Result<CompiledShader, ShaderError[]> result) =>
        result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.File}({e.Line},{e.Column}) {e.Code}: {e.Message}")) : "";
}
