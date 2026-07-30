#nullable enable

using System.Diagnostics;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Cli;

[Trait("Category", "Integration")]
public sealed class CliIntegrationTest : IClassFixture<CliBinaryFixture>
{
    private readonly CliBinaryFixture _fixture;

    // Resolve the fixtures directory relative to the solution root at test startup.
    private static readonly string FixturesDir = FindFixturesDir();

    public CliIntegrationTest(CliBinaryFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Compile_MinimalFx_OpenGL_ExitCode0()
    {
        string sourceFile = Path.Combine(FixturesDir, "shaders", "Minimal.fx");
        string outputFile = Path.Combine(Path.GetTempPath(), $"Minimal_{Guid.NewGuid():N}.mgfx");

        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(
                sourceFile, outputFile, "/Profile:OpenGL");

            stdout.ShouldBeEmpty("nothing must be written to stdout");
            stderr.ShouldBeEmpty("successful compile must produce no stderr output");
            exitCode.ShouldBe(0);
            File.Exists(outputFile).ShouldBeTrue("output file must be created");
            new FileInfo(outputFile).Length.ShouldBeGreaterThan(0, customMessage: "output file must not be empty");
        }
        finally
        {
            if (File.Exists(outputFile))
                File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task Compile_InvalidFx_ExitCode1_StderrContainsError()
    {
        string invalidSource = Path.Combine(Path.GetTempPath(), $"Invalid_{Guid.NewGuid():N}.fx");
        string outputFile    = Path.Combine(Path.GetTempPath(), $"Invalid_{Guid.NewGuid():N}.mgfx");

        try
        {
            await File.WriteAllTextAsync(invalidSource,
                """
                // Deliberately invalid HLSL to trigger a compile error.
                float4 PS() : SV_TARGET { this_does_not_compile; }
                technique T { pass P { PixelShader = compile ps_4_0 PS(); } }
                """);

            var (exitCode, stdout, stderr) = await RunCliAsync(
                invalidSource, outputFile, "/Profile:OpenGL");

            exitCode.ShouldBe(1);
            stdout.ShouldBeEmpty("nothing must ever go to stdout");
            stderr.ShouldNotBeEmpty("error diagnostics must appear on stderr");
            // THE MGCB CONTRACT (Core Design Constraint 2/5): diagnostics on stderr in
            // mgfxc-compatible `file(line,col-col): error CODE: message` form, which
            // MGCB parses. Pin the full shape at the CLI boundary — "stderr non-empty"
            // alone would let the format silently regress to something MGCB can't read.
            stderr.ShouldMatch(@"\.fx\(\d+,\d+(-\d+)?\): error [A-Z]+\d+:", customMessage: "stderr must carry the MGCB-parseable " +
                         "'file(line,col-col): error CODE: message' diagnostic form " +
                         $"(MgcbErrorFormatter); actual stderr: {stderr}");
        }
        finally
        {
            if (File.Exists(invalidSource)) File.Delete(invalidSource);
            if (File.Exists(outputFile))    File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task Compile_MissingSourceFile_ExitCode1_UsageOnStderr()
    {
        // Invoke with only a flag — no positional arguments.
        var (exitCode, stdout, stderr) = await RunCliAsync(null, null, "/Profile:OpenGL");

        exitCode.ShouldBe(1);
        stdout.ShouldBeEmpty("nothing must ever go to stdout");
        stderr.ShouldContain("Usage:", Case.Sensitive, "usage text must appear on stderr when arguments are missing");
    }

    [Fact]
    public async Task Compile_UnsupportedPlatform_PS4_ExitCode1()
    {
        // Source/output files do not need to exist for this test — argument parsing
        // rejects the platform before attempting file I/O.
        var (exitCode, stdout, stderr) = await RunCliAsync(
            "Shader.fx", "Out.mgfx", "/Profile:PlayStation4");

        exitCode.ShouldBe(1);
        stdout.ShouldBeEmpty("nothing must ever go to stdout");
        stderr.ShouldContain("X0010", Case.Sensitive, "unsupported platform must produce error code X0010");
    }

    [Fact]
    public async Task Compile_DebugFlag_ExitCode0()
    {
        string sourceFile = Path.Combine(FixturesDir, "shaders", "Minimal.fx");
        string outputFile = Path.Combine(Path.GetTempPath(), $"MinimalDebug_{Guid.NewGuid():N}.mgfx");

        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(
                sourceFile, outputFile, "/Profile:OpenGL", "/Debug");

            stdout.ShouldBeEmpty("nothing must be written to stdout");
            stderr.ShouldBeEmpty("debug flag alone must not cause failure");
            exitCode.ShouldBe(0);
        }
        finally
        {
            if (File.Exists(outputFile))
                File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task Compile_IncludePathFlag_ResolvesHeader()
    {
        string sourceFile  = Path.Combine(FixturesDir, "shaders", "MinimalWithInclude.fx");
        string includeDir  = Path.Combine(FixturesDir, "shaders", "includes");
        string outputFile  = Path.Combine(Path.GetTempPath(), $"MinimalInclude_{Guid.NewGuid():N}.mgfx");

        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(
                sourceFile, outputFile, "/Profile:OpenGL", "/I", includeDir);

            stdout.ShouldBeEmpty("nothing must be written to stdout");
            stderr.ShouldBeEmpty($"include-path compile should succeed; stderr: {stderr}");
            exitCode.ShouldBe(0);
        }
        finally
        {
            if (File.Exists(outputFile))
                File.Delete(outputFile);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        string? sourceFile,
        string? outputFile,
        params string[] extraArgs)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var argList = new List<string>();
        if (sourceFile is not null) argList.Add(sourceFile);
        if (outputFile is not null) argList.Add(outputFile);
        argList.AddRange(extraArgs);

        string arguments = string.Join(" ",
            argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

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

    private static string FindFixturesDir()
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);

        while (dir.Parent is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate tests/fixtures directory. " +
            "Ensure the test is running from a directory under the repository root.");
    }
}
