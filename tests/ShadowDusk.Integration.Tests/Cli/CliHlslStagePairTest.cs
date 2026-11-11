#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Cli;

// The CLI accepts a vertex and a pixel shader as two files (--vertex/--pixel) and compiles them
// through HlslStageFrontend and the unchanged pipeline. These tests pin the end-user contract: the
// output equals the library result byte for byte, the mgfxc empty-stderr success contract, and
// errors that point at the ORIGINAL file and line.
[Trait("Category", "Integration")]
public sealed class CliHlslStagePairTest : IClassFixture<CliBinaryFixture>
{
    private readonly CliBinaryFixture _fixture;

    private static readonly string StagesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "hlsl-stages");

    public CliHlslStagePairTest(CliBinaryFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("/Profile:OpenGL", PlatformTarget.OpenGL)]
    [InlineData("/Profile:DirectX_11", PlatformTarget.DirectX)]
    [InlineData("/Profile:Vulkan", PlatformTarget.Vulkan)]
    public async Task Pair_Compiles_ExitCode0_EmptyStderr_AndEqualsTheLibraryResult(string profileFlag, PlatformTarget target)
    {
        string vertex = Path.Combine(StagesDir, "Textured.vs.hlsl");
        string pixel = Path.Combine(StagesDir, "Textured.ps.hlsl");
        string output = TempOutput("Textured");

        try
        {
            var (exitCode, stdout, stderr) = await RunAsync(
                "--vertex", vertex, "--pixel", pixel, "--vertex-entry", "VSMain", "--pixel-entry", "PSMain",
                output, profileFlag);

            stdout.ShouldBeEmpty("nothing must be written to stdout (MGCB contract)");
            stderr.ShouldBeEmpty($"a clean compile keeps stderr empty; actual: {stderr}");
            exitCode.ShouldBe(0);

            // The same pair through the library, with the CLI's own source name for the embedded string.
            var library = await HlslStageCompiler.CompileAsync(
                new EffectCompiler(),
                new HlslStageSources(
                    await File.ReadAllTextAsync(vertex), await File.ReadAllTextAsync(pixel),
                    vertex, pixel, "VSMain", "PSMain", TechniqueName: Path.GetFileNameWithoutExtension(output)),
                new CompilerOptions
                {
                    Target = target,
                    SourceFileName = Path.GetFileNameWithoutExtension(output) + ".generated.fx",
                    AdditionalIncludePaths = [StagesDir],
                });
            library.IsSuccess.ShouldBeTrue();
            (await File.ReadAllBytesAsync(output)).ShouldBe(library.Value.Data);
        }
        finally
        {
            Cleanup(output);
        }
    }

    [Fact]
    public async Task EqualEntryNames_DefaultToMain_AndAreRenamedApart()
    {
        string output = TempOutput("SharedMain");

        try
        {
            var (exitCode, _, stderr) = await RunAsync(
                "--vertex", Path.Combine(StagesDir, "SharedMain.vs.hlsl"),
                "--pixel", Path.Combine(StagesDir, "SharedMain.ps.hlsl"),
                output, "/Profile:OpenGL");

            exitCode.ShouldBe(0, stderr);
            new FileInfo(output).Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            Cleanup(output);
        }
    }

    [Fact]
    public async Task ACompileErrorPointsAtTheOriginalPixelFileAndLine()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string output = Path.Combine(dir, "Out.mgfx");

        try
        {
            string vertex = Path.Combine(dir, "v.hlsl");
            string pixel = Path.Combine(dir, "p.hlsl");
            await File.WriteAllTextAsync(vertex, await File.ReadAllTextAsync(Path.Combine(StagesDir, "Textured.vs.hlsl")));
            string text = (await File.ReadAllTextAsync(Path.Combine(StagesDir, "Textured.ps.hlsl"))).Replace("* Tint;", "* Tintt;");
            await File.WriteAllTextAsync(pixel, text);
            int line = 1 + text.AsSpan(0, text.IndexOf("Tintt", StringComparison.Ordinal)).Count('\n');

            var (exitCode, stdout, stderr) = await RunAsync(
                "--vertex", vertex, "--pixel", pixel, "--vertex-entry", "VSMain", "--pixel-entry", "PSMain",
                output, "/Profile:OpenGL");

            exitCode.ShouldBe(1);
            stdout.ShouldBeEmpty();
            stderr.ShouldContain($"p.hlsl({line},", Case.Sensitive);
            stderr.ShouldContain("Tintt", Case.Sensitive);
            File.Exists(output).ShouldBeFalse("no output is written on a compile failure");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AMergeConflict_FailsLoudlyWithItsCode_AndWritesNothing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string output = Path.Combine(dir, "Out.mgfx");

        try
        {
            string vertex = Path.Combine(dir, "v.hlsl");
            string pixel = Path.Combine(dir, "p.hlsl");
            await File.WriteAllTextAsync(vertex, "float4 Tint;\nfloat4 main() : SV_Position { return Tint; }\n");
            await File.WriteAllTextAsync(pixel, "float3 Tint;\nfloat4 main() : SV_Target0 { return 0; }\n");

            var (exitCode, _, stderr) = await RunAsync("--vertex", vertex, "--pixel", pixel, output, "/Profile:OpenGL");

            exitCode.ShouldBe(1);
            stderr.ShouldContain("error SD0665", Case.Sensitive);
            stderr.ShouldContain("p.hlsl(1,1", Case.Sensitive);
            File.Exists(output).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AMissingInputFile_IsX0001()
    {
        string output = TempOutput("Missing");

        var (exitCode, _, stderr) = await RunAsync(
            "--vertex", Path.Combine(StagesDir, "Textured.vs.hlsl"), "--pixel", Path.Combine(StagesDir, "nope.hlsl"), output);

        exitCode.ShouldBe(1);
        stderr.ShouldContain("X0001", Case.Sensitive);
        stderr.ShouldContain("nope.hlsl", Case.Sensitive);
        File.Exists(output).ShouldBeFalse();
    }

    [Theory]
    [InlineData("--vertex", "v.hlsl", "Out.mgfx")]                       // pair without a pixel file
    [InlineData("--pixel", "p.hlsl", "Out.mgfx")]                        // pair without a vertex file
    public async Task AnIncompletePair_IsX0012(params string[] args)
    {
        var (exitCode, _, stderr) = await RunAsync(args);

        exitCode.ShouldBe(1);
        stderr.ShouldContain("X0012", Case.Sensitive);
    }

    [Fact]
    public async Task EntryFlagsWithoutAPair_AreX0012()
    {
        var (exitCode, _, stderr) = await RunAsync("In.fx", "Out.mgfx", "--vertex-entry", "VS");

        exitCode.ShouldBe(1);
        stderr.ShouldContain("X0012", Case.Sensitive);
    }

    private static string TempOutput(string stem) => Path.Combine(Path.GetTempPath(), $"{stem}_{Guid.NewGuid():N}.mgfx");

    private static void Cleanup(string output)
    {
        if (File.Exists(output))
            File.Delete(output);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        ChildProcessResult run = await CliProcess.RunAsync(_fixture.ExecutablePath, args, TestBudget.Compile);
        return (run.ExitCode, run.Stdout, run.Stderr);
    }
}
