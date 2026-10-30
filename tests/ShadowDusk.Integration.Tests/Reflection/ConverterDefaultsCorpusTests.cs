#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// Issue #435 corpus sweep: on every <c>.fx</c> fixture (the SkSL and raylib render gates' corpora
/// are subsets of it: XnaFiddle, Gum, the GL corpus, <c>raylib/</c>), every uniform the SkSL and
/// raylib converters report carries exactly the default the MGFX route's OpenGL build reflects for
/// the same parameter, and the converted uniform list matches the MGFX parameter shapes it can be
/// compared with. One test per fixture, so no single test runs long.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class ConverterDefaultsCorpusTests
{
    public static IEnumerable<object[]> Fixtures()
    {
        string root = TestHelpers.FixturePath("");
        return Directory.GetFiles(root, "*.fx", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new object[] { p });
    }

    [Fact]
    public void TheCorpusIsThere() =>
        Fixtures().Count().ShouldBeGreaterThan(100, "the fixture corpus was not copied next to the test assembly");

    // Measured 2026-10-29 on win-x64 over the whole fixture corpus: 38 fixtures had at least one
    // converted uniform (SkSL or raylib) compared with the MGFX OpenGL route's parameter, and 7
    // of the compared uniforms carried a non-null default. The floors sit a little below, so a
    // corpus or converter regression that silently skips the comparison fails here instead of
    // letting every per-fixture case return early and pass.
    private const int MinimumComparedFixtures = 35;
    private const int MinimumComparedDefaults = 6;

    [Fact]
    public async Task TheSweepComparesEnoughOfTheCorpus()
    {
        int fixtures = 0, defaults = 0;
        foreach (object[] row in Fixtures())
        {
            Sweep s = await SweepAsync((string)row[0]);
            if (s.Compared > 0) fixtures++;
            defaults += s.NonNullDefaults;
        }

        fixtures.ShouldBeGreaterThanOrEqualTo(MinimumComparedFixtures,
            "fixtures whose converted uniforms were compared with the MGFX OpenGL route (an early return is not a pass)");
        defaults.ShouldBeGreaterThanOrEqualTo(MinimumComparedDefaults,
            "converted uniforms with a non-null declared default that were compared");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task ConvertedUniformDefaults_EqualTheMgfxOpenGlRoute(string fixture) => await SweepAsync(fixture);

    private readonly record struct Sweep(int Compared, int NonNullDefaults);

    private static async Task<Sweep> SweepAsync(string fixture)
    {
        string path = TestHelpers.FixturePath(fixture);
        string fx = await File.ReadAllTextAsync(path);

        var gl = await new EffectCompiler().CompileAsync(fx, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = path,
            IncludeResolver = new FileSystemIncludeResolver(),
        });
        if (gl.IsFailure)
            return default; // no MGFX OpenGL default to compare against (counted by the floor above)

        int compared = 0, nonNull = 0;
        var sksl = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = path, IncludeResolver = new FileSystemIncludeResolver() });
        if (sksl.IsSuccess)
            Compare(fixture, "SkSL", sksl.Value.Uniforms.Select(u => (u.Name, u.DefaultValue)), sksl.Value.SynthesizedUniforms, gl.Value.Parameters, ref compared, ref nonNull);

        var raylib = RaylibConverter.Convert(fx, new RaylibConvertOptions { SourceName = path, IncludeResolver = new FileSystemIncludeResolver() });
        if (raylib.IsSuccess)
            Compare(fixture, "raylib", raylib.Value.Uniforms.Select(u => (u.Name, u.DefaultValue)), [], gl.Value.Parameters, ref compared, ref nonNull);

        return new Sweep(compared, nonNull);
    }

    private static void Compare(
        string fixture, string route, IEnumerable<(string Name, IReadOnlyList<float>? Default)> uniforms,
        IReadOnlyList<string> synthesized, IReadOnlyList<ParameterReflection> mgfx, ref int compared, ref int nonNull)
    {
        foreach ((string name, IReadOnlyList<float>? value) in uniforms)
        {
            if (synthesized.Contains(name))
            {
                value.ShouldBeNull($"{fixture} ({route}): the synthesized uniform '{name}' has no HLSL default");
                continue;
            }

            // A leading underscore is SPIRV-Cross's reserved-word rename (input -> _input).
            ParameterReflection? p = mgfx.FirstOrDefault(x => x.Name == name)
                ?? (name.StartsWith('_') ? mgfx.FirstOrDefault(x => x.Name == name[1..]) : null);
            if (p is null)
                continue;

            compared++;
            if (p.DefaultValue is null)
                value.ShouldBeNull($"{fixture} ({route}): '{name}' has no MGFX default");
            else
            {
                value.ShouldBe(p.DefaultValue, $"{fixture} ({route}): '{name}'");
                nonNull++;
            }
        }
    }
}
