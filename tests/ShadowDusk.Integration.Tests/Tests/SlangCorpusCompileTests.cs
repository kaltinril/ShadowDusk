#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// The in-suite half of the Slang corpus gate (Phase 61): <b>every</b> shader under
/// <c>tests/fixtures/shaders/slang/</c> converts through the frontend and compiles to real
/// <c>.mgfx</c> bytes on both a rung-4-proven GL target and a rung-4-proven DX target. Ungated
/// and enumeration-driven — dropping a new <c>.slang</c> into the corpus directory enrolls it
/// automatically, and a shader that stops converting turns the suite red with its diagnostics.
///
/// <para>The other half — proof these are <b>genuine Slang</b> (the real <c>slangc</c> accepts
/// every one) and that ShadowDusk's route renders the <b>same pixels</b> as slangc's own HLSL
/// emission — needs the Slang oracle binary and a GL context, so it lives out-of-band in
/// <c>validation/SlangCorpus</c> (slangc is a TEST oracle there, exactly as `fxc`/`mgcb` are
/// elsewhere; it is never shipped and never invoked by the product).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangCorpusCompileTests
{
    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.EnumerateFiles(CorpusDirectory(), "*.slang"))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Fact]
    public void Corpus_IsNotVacuous()
    {
        // The user-set bar was "10-50 comparable real Slang shaders"; the corpus starts at 17.
        // A refactor that silently empties the directory must fail here, not pass 0 theories.
        Directory.EnumerateFiles(CorpusDirectory(), "*.slang").Count()
            .ShouldBeGreaterThanOrEqualTo(17);
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public async Task CorpusShader_ConvertsAndCompiles_OnOpenGlAndDirectX(string fileName)
    {
        string source = await File.ReadAllTextAsync(Path.Combine(CorpusDirectory(), fileName));

        var converted = SlangFrontend.ConvertToFx(source, new SlangConvertOptions
        {
            SourceName    = fileName,
            TechniqueName = Path.GetFileNameWithoutExtension(fileName),
        });
        converted.IsSuccess.ShouldBeTrue(fileName + ": " +
            (converted.IsFailure ? string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}")) : ""));

        var compiler = new EffectCompiler();
        foreach (PlatformTarget target in new[] { PlatformTarget.OpenGL, PlatformTarget.DirectX })
        {
            var compiled = await compiler.CompileAsync(converted.Value.FxText, new CompilerOptions
            {
                Target = target,
                SourceFileName = Path.GetFileNameWithoutExtension(fileName) + ".generated.fx",
            });

            compiled.IsSuccess.ShouldBeTrue($"{fileName} on {target}: " +
                (compiled.IsFailure ? string.Join(" | ", compiled.Error.Select(e => $"{e.Code}: {e.Message}")) : ""));
            compiled.Value.Data.Length.ShouldBeGreaterThan(0);
        }
    }

    private static string CorpusDirectory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "shaders", "slang");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("tests/fixtures/shaders/slang not found");
    }
}
