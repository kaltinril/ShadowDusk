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

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task ConvertedUniformDefaults_EqualTheMgfxOpenGlRoute(string fixture)
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
            return; // no MGFX OpenGL default to compare against

        var sksl = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = path, IncludeResolver = new FileSystemIncludeResolver() });
        if (sksl.IsSuccess)
            Compare(fixture, "SkSL", sksl.Value.Uniforms.Select(u => (u.Name, u.DefaultValue)), sksl.Value.SynthesizedUniforms, gl.Value.Parameters);

        var raylib = RaylibConverter.Convert(fx, new RaylibConvertOptions { SourceName = path, IncludeResolver = new FileSystemIncludeResolver() });
        if (raylib.IsSuccess)
            Compare(fixture, "raylib", raylib.Value.Uniforms.Select(u => (u.Name, u.DefaultValue)), [], gl.Value.Parameters);
    }

    private static void Compare(
        string fixture, string route, IEnumerable<(string Name, IReadOnlyList<float>? Default)> uniforms,
        IReadOnlyList<string> synthesized, IReadOnlyList<ParameterReflection> mgfx)
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

            if (p.DefaultValue is null)
                value.ShouldBeNull($"{fixture} ({route}): '{name}' has no MGFX default");
            else
                value.ShouldBe(p.DefaultValue, $"{fixture} ({route}): '{name}'");
        }
    }
}
