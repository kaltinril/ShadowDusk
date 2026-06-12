#nullable enable

using FluentAssertions;
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
/// <para><b>Pinned, render-proven divergences</b> (Phase 27; the phase doc's risk note
/// requires pinning exactly which fields are exact). Value-class parameters
/// (Scalar/Vector/Matrix — the <c>SetValue</c> fidelity surface) match mgfxc EXACTLY.
/// Object-class (texture/sampler) parameters carry two deliberate differences, both
/// proven equivalent in the real MonoGame runtime (Phases 17/28):</para>
/// <list type="number">
///   <item>ShadowDusk additionally exposes each sampler as an Object parameter
///         (<c>ParameterListBuilder</c>, Phase 5 §7.4.3); mgfxc's GL path does not.
///         Additive only — name-based lookup of every mgfxc parameter still works.</item>
///   <item>For legacy <c>sampler s0;</c> sources, mgfxc names the texture parameter
///         after the sampler (<c>s0</c>, type Texture2D), while ShadowDusk synthesizes
///         <c>s0_SDTexture</c> (type Texture2D) and exposes <c>s0</c> as the sampler
///         parameter wired to it via the shader's sampler table — so
///         <c>Parameters["s0"].SetValue(texture)</c> binds identically on both.</item>
/// </list>
/// <para>Any divergence OUTSIDE these two shapes (a missing parameter, any value-class
/// metadata delta, an unexpected extra value-class parameter) fails the test.</para>
/// </summary>
[Trait("Category", "Integration")]
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = cts.Token;

        // --- ShadowDusk side: compile the same .fx source mgfxc compiled ---
        string fxPath = TestHelpers.FixturePath(fixtureStem + ".fx");
        File.Exists(fxPath).Should().BeTrue($".fx fixture must exist at {fxPath}");
        string source = await File.ReadAllTextAsync(fxPath, ct);

        var result = await new EffectCompiler().CompileAsync(
            source,
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = fxPath },
            ct);
        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure
                ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
                : "the corpus shader must compile");

        MgfxBlobReader subject = MgfxBlobReader.Parse(result.Value.Data);

        // --- mgfxc side: the committed reference golden ---
        string goldenPath = Path.Combine(
            FindRepoRoot(), "tests", "fixtures", "golden", "OpenGL", fixtureStem + ".mgfx");
        File.Exists(goldenPath).Should().BeTrue($"mgfxc golden must exist at {goldenPath}");

        MgfxBlobReader golden = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(goldenPath, ct));

        Dump("SHADOWDUSK", subject);
        Dump("MGFXC GOLDEN", golden);

        // --- Comparison (Phase 5 §9.3.2: name, class, type, rows, columns, elements;
        //     keyed by name — order is not part of the contract). Exact everywhere,
        //     modulo ONLY the two pinned object-class divergences in the class doc. ---
        const byte ClassObject = 3; // EffectParameterClass.Object
        const byte TypeSampler = 5; // EffectParameterType.Texture (the sampler param type)
        const string SynthesizedTextureSuffix = "_SDTexture";

        var subjectByName = subject.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (MgfxParameterRecord gold in golden.Parameters)
        {
            subjectByName.Should().ContainKey(gold.Name,
                because: $"every mgfxc parameter must be reachable by name ('{gold.Name}')");
            MgfxParameterRecord sub = subjectByName[gold.Name];

            if (gold.Class != ClassObject)
            {
                // Value-class parameter (SetValue surface): EXACT match, no exceptions.
                sub.Class.Should().Be(gold.Class,               $"parameter '{gold.Name}' Class");
                sub.Type.Should().Be(gold.Type,                 $"parameter '{gold.Name}' Type");
                sub.Rows.Should().Be(gold.Rows,                 $"parameter '{gold.Name}' Rows");
                sub.Columns.Should().Be(gold.Columns,           $"parameter '{gold.Name}' Columns");
                sub.ElementCount.Should().Be(gold.ElementCount, $"parameter '{gold.Name}' Elements");
                sub.MemberCount.Should().Be(gold.MemberCount,   $"parameter '{gold.Name}' Members");
                continue;
            }

            // Object-class parameter (texture).
            sub.Class.Should().Be(ClassObject, $"parameter '{gold.Name}' must stay object-class");
            if (sub.Type == gold.Type)
                continue; // identical texture parameter — done.

            // Pinned divergence 2: mgfxc's texture param name is ShadowDusk's sampler
            // param; the texture itself is the synthesized companion parameter.
            sub.Type.Should().Be(TypeSampler,
                because: $"'{gold.Name}': the only allowed type divergence is mgfxc-texture vs " +
                         "ShadowDusk-sampler (legacy `sampler s0;` shape)");
            string companion = gold.Name + SynthesizedTextureSuffix;
            subjectByName.Should().ContainKey(companion,
                because: $"the sampler '{gold.Name}' must be backed by the synthesized texture " +
                         $"parameter '{companion}'");
            subjectByName[companion].Class.Should().Be(ClassObject, $"'{companion}' Class");
            subjectByName[companion].Type.Should().Be(gold.Type,    $"'{companion}' Type");
        }

        // Extras: pinned divergence 1 — ShadowDusk may additionally expose object-class
        // sampler params and synthesized texture companions, NOTHING else. An unexpected
        // extra value-class parameter would change cbuffer layout/SetValue behavior.
        var goldenNames = golden.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (MgfxParameterRecord extra in subject.Parameters.Where(p => !goldenNames.Contains(p.Name)))
        {
            extra.Class.Should().Be(ClassObject,
                because: $"extra parameter '{extra.Name}' must be object-class (sampler/texture) — " +
                         "extra value-class parameters are never allowed");
            bool isSampler   = extra.Type == TypeSampler;
            bool isSynthTex  = extra.Name.EndsWith(SynthesizedTextureSuffix, StringComparison.Ordinal);
            (isSampler || isSynthTex).Should().BeTrue(
                because: $"extra parameter '{extra.Name}' (type={extra.Type}) must be either a " +
                         "sampler parameter or a synthesized *_SDTexture companion");
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
