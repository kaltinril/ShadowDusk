#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Issue #299 corpus sweep. The register an explicit <c>register(sN)</c> on a legacy
/// <c>sampler</c> pins its texture to is now read from the managed preprocessed view of every
/// effect, so on every fixture that does not contain one of the shapes the fix exists for it must
/// agree with the old raw-token reading. The allow-list names those shapes; anything else that
/// moved would be an unintended OpenGL output change.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue299ExplicitSlotCorpusSweepTests(ITestOutputHelper output)
{
    /// <summary>
    /// Fixtures whose explicit-slot map is EXPECTED to change. The two
    /// <c>SamplerLegacyRegister*</c> fixtures are the issue's own goldens. The other three write
    /// <c>SamplerState X : register(sN)</c> in their <c>#if SM6</c> arm and declare the same name
    /// through the legacy syntax in the arm OpenGL compiles, so the raw reading mapped the SM6
    /// arm's register onto the legacy arm's texture. Their OpenGL <c>.mgfx</c> bytes were measured
    /// IDENTICAL before and after the fix (2026-10-02), and their sampler tables are pinned by
    /// <c>Issue299PreprocessedLegacySamplerRegisterTests</c>.
    /// </summary>
    private static readonly HashSet<string> ExpectedToMove = new(StringComparer.Ordinal)
    {
        "SamplerLegacyRegisterIfBranch.fx",
        "SamplerLegacyRegisterMacro.fx",
        "VsTransformColorTexture.fx",
        "VsWaveQuadIntrinsics.fx",
        "apos-shapes-sm6.fx",
        // Issue #368: the vendored XnaFiddle examples, same `#if SM6` arm shape as apos-shapes-sm6.
        "XnaFiddle-Fading.fx",
        "XnaFiddle-Grayscale.fx",
        "XnaFiddle-Invert.fx",
        "XnaFiddle-Mask.fx",
        "XnaFiddle-Pixelated.fx",
        "XnaFiddle-Tint.fx",
    };

    [Fact]
    [Trait("Platform", "OpenGL")]
    public void EveryFixture_KeepsItsExplicitSlots_ExceptTheIssueShapes()
    {
        string root = TestHelpers.FixturePath("");
        string[] files = Directory.GetFiles(root, "*.fx", SearchOption.AllDirectories);
        files.Length.ShouldBeGreaterThan(100, "the fixture corpus was not copied next to the test assembly");

        MacroSet macros = PlatformMacros.For(PlatformTarget.OpenGL);
        var moved = new List<string>();
        var seenExpected = new HashSet<string>(StringComparer.Ordinal);
        int swept = 0;

        foreach (string path in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetRelativePath(root, path).Replace('\\', '/');
            string raw = File.ReadAllText(path);

            // A fixture whose includes do not resolve, or that the pre-parser rejects, never
            // reaches the sampler-slot step in a compile either.
            var flattened = new ShadowDusk.Core.Preprocessor.Preprocessor().Flatten(raw, path, macros, new FileSystemIncludeResolver(), []);
            var parsed = FxPreParser.Parse(raw, path);
            if (flattened.IsFailure || parsed.IsFailure)
                continue;

            // A view that cannot be built is Issue283ReservationCorpusSweepTests' subject.
            var view = FxPreParser.CollectGlSamplerSlots(flattened.Value.Text, path, parsed.Value);
            if (view.IsFailure)
                continue;
            swept++;

            string before = Describe(parsed.Value.ExplicitGlSamplerSlots);
            string after = Describe(view.Value.Explicit);
            if (before != after)
            {
                string line = $"{name}: raw [{before}] -> preprocessed [{after}]";
                output.WriteLine(line);
                if (ExpectedToMove.Contains(Path.GetFileName(path)))
                    seenExpected.Add(Path.GetFileName(path));
                else
                    moved.Add(line);
            }
        }

        output.WriteLine($"swept {swept} of {files.Length} fixtures");
        swept.ShouldBeGreaterThan(100);
        moved.ShouldBeEmpty();

        // A stale allow-list entry would hide the day one of these stops moving for a new reason.
        seenExpected.OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(ExpectedToMove.OrderBy(x => x, StringComparer.Ordinal));
    }

    private static string Describe(IReadOnlyDictionary<string, int> slots) =>
        string.Join(", ", slots.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}=s{e.Value}"));
}
