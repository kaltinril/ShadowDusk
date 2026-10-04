#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// Phase 5 §9.3.1/§9.3.2 (closed by Phase 27): the parameter-reflection golden snapshot.
///
/// <para>For each corpus shader, ShadowDusk compiles the <c>.fx</c> for OpenGL and the
/// parameter block of its <c>.mgfx</c> is compared EXACTLY — name, class, type, rows,
/// columns, element count (no fuzzy matching) — against the parameter block of the
/// committed <c>mgfxc</c> reference golden (<c>tests/fixtures/golden/OpenGL/*.mgfx</c>,
/// produced by MonoGame's real <c>mgfxc</c>). These are the fields MonoGame's
/// <c>EffectReader</c> builds <c>EffectParameter</c> from; a divergence means
/// <c>effect.Parameters["Name"]</c> behaves differently than with <c>mgfxc</c> output,
/// which would silently break the drop-in promise.</para>
///
/// <para>The golden .mgfx BINARY is the snapshot (not a separate JSON file as Phase 5
/// originally sketched): it is the artifact mgfxc actually produced, already committed
/// and shared with <c>MgfxcCrossValidationTests</c>. Parameter ORDER is deliberately not
/// compared — MonoGame looks parameters up by name, and mgfxc's MojoShader pipeline
/// orders the block differently than ShadowDusk's reflection does. Byte-equality with
/// mgfxc is a non-goal (CLAUDE.md); parameter-metadata equality is the bar.</para>
///
/// <para><b>The one pinned, render-proven divergence</b> (Phase 27; the phase doc's risk note
/// requires pinning exactly which fields are exact). Value-class parameters
/// (Scalar/Vector/Matrix, the <c>SetValue</c> fidelity surface) match mgfxc EXACTLY, and so
/// does every mgfxc texture parameter: same name, class and type. ShadowDusk additionally
/// exposes each sampler as an Object parameter (<c>ParameterListBuilder</c>, Phase 5 §7.4.3);
/// mgfxc's GL path does not. Additive only: name-based lookup of every mgfxc parameter still
/// works.</para>
/// <para>A legacy texture-less sampler (<c>sampler s0;</c>) used to be a second pinned
/// divergence: ShadowDusk named its texture parameter <c>s0_SDTexture</c>, so
/// <c>Parameters["s0"]</c> found only the standalone sampler parameter, which no sampler
/// record points at, and setting a texture through it drew nothing. ShadowDusk now names it
/// <c>s0</c> (Texture2D) exactly as mgfxc does, and that allowance is gone.</para>
/// <para>Any divergence OUTSIDE the additive sampler parameters (a missing parameter, any
/// metadata delta, an unexpected extra value-class or texture parameter) fails the test.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Fidelity")] // also runs on the PR unit lane (folds the fidelity gate into the required build-and-test job)
[Trait("Platform", "OpenGL")]
public sealed class MgfxParameterMatchTests
{
    private readonly ITestOutputHelper _output;

    public MgfxParameterMatchTests(ITestOutputHelper output) => _output = output;

    // The rung-4-proven SM3 corpus stems that have a committed mgfxc OpenGL golden:
    // the Phase 17 PS-only set plus the Phase 28 VS-driven set.
    private static readonly string[] s_corpus =
    {
        "Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
        "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve",
        "PolygonLight", "VertexAndPixel", "VsTransformColorTexture",
        // Phase 43C: shared/multi/array cbuffer shapes (F4/F5/F6) — exact
        // element/member COUNTS asserted here; the full recursive element
        // sub-record comparison is Phase43CbufferModelTests.
        "SharedCbuffer", "MultiCbuffer", "MultiCbufferVs",
        "ArrayUniform", "ArrayUniformVs",
    };

    public static IEnumerable<object[]> Corpus() =>
        s_corpus.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task ParameterMetadata_MatchesMgfxcGolden(string fixtureStem)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var ct = cts.Token;

        // --- ShadowDusk side: compile the same .fx source mgfxc compiled ---
        string fxPath = TestHelpers.FixturePath(fixtureStem + ".fx");
        File.Exists(fxPath).ShouldBeTrue($".fx fixture must exist at {fxPath}");
        string source = await File.ReadAllTextAsync(fxPath, ct);

        var result = await new EffectCompiler().CompileAsync(
            source,
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = fxPath },
            ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
                ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
                : "the corpus shader must compile");

        MgfxBlobReader subject = MgfxBlobReader.Parse(result.Value.Data);

        // --- mgfxc side: the committed reference golden ---
        string goldenPath = Path.Combine(
            FindRepoRoot(), "tests", "fixtures", "golden", "OpenGL", fixtureStem + ".mgfx");
        File.Exists(goldenPath).ShouldBeTrue($"mgfxc golden must exist at {goldenPath}");

        MgfxBlobReader golden = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(goldenPath, ct));

        Dump("SHADOWDUSK", subject);
        Dump("MGFXC GOLDEN", golden);

        // --- Comparison (Phase 5 §9.3.2: name, class, type, rows, columns, elements;
        //     keyed by name, order is not part of the contract). Exact everywhere,
        //     modulo ONLY the additive sampler parameters in the class doc. ---
        const byte ClassObject = 3; // EffectParameterClass.Object
        const byte TypeSampler = 5; // EffectParameterType.Texture (the sampler param type)

        var subjectByName = subject.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (MgfxParameterRecord gold in golden.Parameters)
        {
            subjectByName.ContainsKey(gold.Name).ShouldBeTrue(customMessage: $"every mgfxc parameter must be reachable by name ('{gold.Name}')");
            MgfxParameterRecord sub = subjectByName[gold.Name];

            sub.Class.ShouldBe(gold.Class, customMessage: $"parameter '{gold.Name}' Class");
            sub.Type.ShouldBe(gold.Type, customMessage: $"parameter '{gold.Name}' Type");
            sub.Rows.ShouldBe(gold.Rows, customMessage: $"parameter '{gold.Name}' Rows");
            sub.Columns.ShouldBe(gold.Columns, customMessage: $"parameter '{gold.Name}' Columns");
            sub.ElementCount.ShouldBe(gold.ElementCount, customMessage: $"parameter '{gold.Name}' Elements");
            sub.MemberCount.ShouldBe(gold.MemberCount, customMessage: $"parameter '{gold.Name}' Members");
        }

        // Every sampler record points at the parameter mgfxc's record points at, by NAME
        // (what Parameters["x"].SetValue(texture) reaches): the texture-less legacy sampler's
        // parameter used to be a synthesized companion here.
        string RecordTargets(MgfxBlobReader r) => string.Join(" ", r.Samplers
            .Select(x => $"{(r.Shaders[x.ShaderIndex].IsVertex ? "vs" : "ps")}:{r.Parameters[x.Parameter].Name}")
            .OrderBy(x => x, StringComparer.Ordinal));
        RecordTargets(subject).ShouldBe(RecordTargets(golden), customMessage: "sampler records must bind the parameters mgfxc's bind");

        // Extras: ShadowDusk may additionally expose object-class SAMPLER params, NOTHING
        // else. An unexpected extra value-class parameter would change cbuffer
        // layout/SetValue behavior; an extra texture parameter would be a name mgfxc
        // never emits.
        var goldenNames = golden.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (MgfxParameterRecord extra in subject.Parameters.Where(p => !goldenNames.Contains(p.Name)))
        {
            extra.Class.ShouldBe(ClassObject, customMessage: $"extra parameter '{extra.Name}' must be object-class (sampler) - " +
                         "extra value-class parameters are never allowed");
            extra.Type.ShouldBe(TypeSampler, customMessage: $"extra parameter '{extra.Name}' must be a sampler parameter; " +
                         "an extra texture parameter is a name mgfxc never emits");
        }
    }

    private void Dump(string label, MgfxBlobReader reader)
    {
        _output.WriteLine($"--- {label} ({reader.Parameters.Count} parameters) ---");
        foreach (MgfxParameterRecord p in reader.Parameters)
            _output.WriteLine(
                $"  {p.Name} class={p.Class} type={p.Type} rows={p.Rows} cols={p.Columns} " +
                $"members={p.MemberCount} elements={p.ElementCount} semantic='{p.Semantic}'");
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (ShadowDusk.slnx) above the test output directory.");
    }
}
