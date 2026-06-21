#nullable enable

using System.Diagnostics;
using FluentAssertions;
using ShadowDusk.ShaderToy.Multipass;
using Xunit;

namespace ShadowDusk.Integration.Tests.Cli;

// Phase 47 (Decision F): the multipass batch converter's per-pass .fx must COMPILE on OpenGL through the
// real ShadowDusk CLI. This is the single child-process assertion that used to live in the (otherwise
// pure, in-memory) ShadowDusk.ShaderToy.Tests suite; it was relocated here so that project stays
// child-process-free while this cross-compile coverage is preserved in the Integration suite where the
// CLI-shell-out tests already live (CliBinaryFixture). The converter's pure unit/golden/reject coverage
// of the same fixtures remains in ShadowDusk.ShaderToy.Tests/MultipassTests.cs.
[Trait("Category", "Integration")]
public sealed class CliMultipassCompileTest : IClassFixture<CliBinaryFixture>
{
    private readonly CliBinaryFixture _fixture;

    public CliMultipassCompileTest(CliBinaryFixture fixture) => _fixture = fixture;

    public static IEnumerable<object[]> CompileCases()
    {
        foreach (string fixture in new[] { "chain2", "feedback" })
        {
            string json = File.ReadAllText(MultipassFixturePath(fixture));
            MultipassResult result = MultipassConverter.Convert(ShaderToyProject.Parse(json));
            foreach (MultipassPassResult pass in result.Passes)
                yield return new object[] { fixture, pass.OutputFileName, pass.Fx! };
        }
    }

    [Theory]
    [MemberData(nameof(CompileCases))]
    public async Task EmittedFx_CompilesOnOpenGL(string fixture, string fileName, string fx)
    {
        string fxPath   = Path.Combine(Path.GetTempPath(), $"sd_mp_{Guid.NewGuid():N}.fx");
        string mgfxPath = Path.ChangeExtension(fxPath, ".mgfx");
        await File.WriteAllTextAsync(fxPath, fx);

        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(fxPath, mgfxPath, "/Profile:OpenGL");

            exitCode.Should().Be(0,
                "the converted .fx for {0}/{1} must compile on OpenGL via the real ShadowDusk CLI; stderr: {2}{3}",
                fixture, fileName, stderr, stdout);
            File.Exists(mgfxPath).Should().BeTrue("the CLI must produce a .mgfx on success");
            new FileInfo(mgfxPath).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            if (File.Exists(fxPath))   File.Delete(fxPath);
            if (File.Exists(mgfxPath)) File.Delete(mgfxPath);
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        string sourceFile, string outputFile, params string[] extraArgs)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var argList = new List<string> { sourceFile, outputFile };
        argList.AddRange(extraArgs);
        string arguments = string.Join(" ", argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        var psi = new ProcessStartInfo(_fixture.ExecutablePath)
        {
            Arguments              = arguments,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = Path.GetTempPath(),
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start CLI process.");

        string stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        string stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, stdout, stderr);
    }

    // The multipass export fixtures are authored in the converter's test corpus; locate them from the
    // repo root (they are not copied into this project's output).
    private static string MultipassFixturePath(string fixture)
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (dir.Parent is not null)
        {
            string candidate = Path.Combine(
                dir.FullName, "tests", "ShadowDusk.ShaderToy.Tests", "corpus", "multipass", fixture + ".json");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate the multipass fixture '{fixture}.json' under tests/ShadowDusk.ShaderToy.Tests/corpus/multipass.");
    }
}
