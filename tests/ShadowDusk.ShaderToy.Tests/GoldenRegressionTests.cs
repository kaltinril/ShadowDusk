using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Golden regression over every in-subset shader (<c>corpus/authored/*.glsl</c> and
/// <c>corpus/cc0/*.glsl</c>): conversion must succeed and the emitted <c>.fx</c> must match a
/// committed golden under <c>corpus/golden/&lt;same-relative-name&gt;.fx</c> (line endings normalized
/// to <c>\n</c> on both sides). Set <c>SHADERTOY2FX_UPDATE_GOLDENS=1</c> to (re)write the goldens
/// into the source tree instead of asserting. Also asserts determinism (two converts are identical).
/// </summary>
public sealed class GoldenRegressionTests
{
    public static IEnumerable<object[]> InSubsetShaders()
    {
        foreach (string path in CorpusLocator.GlslFiles(CorpusLocator.AuthoredDir))
        {
            yield return new object[] { Rel("authored", path), path };
        }

        foreach (string path in CorpusLocator.GlslFiles(CorpusLocator.Cc0Dir))
        {
            yield return new object[] { Rel("cc0", path), path };
        }
    }

    [Theory]
    [MemberData(nameof(InSubsetShaders))]
    public void Converts_AndMatchesGolden(string relName, string path)
    {
        string glsl = File.ReadAllText(path);

        ConvertResult result = ShaderToyConverter.Convert(glsl);
        result.Success.ShouldBeTrue(string.Format(
            "'{0}' is in-subset and must convert; diagnostics: {1}",
            relName,
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
        result.Fx.ShouldNotBeNull();

        string actual = CorpusLocator.NormalizeNewlines(result.Fx!);
        string goldenPath = GoldenPathFor(relName);

        if (CorpusLocator.UpdateGoldens)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, actual);
            return;
        }

        File.Exists(goldenPath).ShouldBeTrue(string.Format(
            "golden '{0}' must exist; run the suite once with SHADERTOY2FX_UPDATE_GOLDENS=1 to generate it",
            goldenPath));

        string expected = CorpusLocator.NormalizeNewlines(File.ReadAllText(goldenPath));
        actual.ShouldBe(expected, customMessage: string.Format( "the emitted .fx for '{0}' must match its committed golden", relName));
    }

    [Theory]
    [MemberData(nameof(InSubsetShaders))]
    public void Conversion_IsDeterministic(string relName, string path)
    {
        string glsl = File.ReadAllText(path);

        ConvertResult first = ShaderToyConverter.Convert(glsl);
        ConvertResult second = ShaderToyConverter.Convert(glsl);

        first.Success.ShouldBeTrue();
        second.Success.ShouldBeTrue();
        second.Fx.ShouldBe(first.Fx, customMessage: string.Format( "conversion of '{0}' must be deterministic", relName));
    }

    /// <summary>The relative name (e.g. <c>authored/gradient_uv.glsl</c>) used in the display name.</summary>
    private static string Rel(string subdir, string path) =>
        $"{subdir}/{Path.GetFileName(path)}";

    /// <summary>Map a relative GLSL name to its golden <c>.fx</c> path in the source corpus tree.</summary>
    private static string GoldenPathFor(string relName)
    {
        // relName is "authored/foo.glsl" or "cc0/foo.glsl"; the golden mirrors it under golden/.
        string withoutExt = relName.EndsWith(".glsl", StringComparison.Ordinal)
            ? relName[..^".glsl".Length]
            : relName;
        string[] parts = withoutExt.Split('/');
        return Path.Combine(new[] { CorpusLocator.GoldenDir }.Concat(parts).ToArray()) + ".fx";
    }
}
