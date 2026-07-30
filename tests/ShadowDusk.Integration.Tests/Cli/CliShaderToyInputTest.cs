#nullable enable

using System.Diagnostics;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.ShaderToy;
using Xunit;

namespace ShadowDusk.Integration.Tests.Cli;

// Phase 47: the CLI accepts ShaderToy / GLSL input (.glsl/.frag/.fs, or sniffed) IN ADDITION to .fx,
// converting glsl -> .fx via ShaderToyConverter then feeding the EXISTING pipeline. These tests pin the
// end-user contract: auto-detection (no required flag), MGCB-parseable located diagnostics on the
// ORIGINAL .glsl, the empty-stderr success contract, and CLI output == Convert + pipeline (no added
// behavior). The .fx path is covered (unchanged) by CliIntegrationTest.
[Trait("Category", "Integration")]
public sealed class CliShaderToyInputTest : IClassFixture<CliBinaryFixture>
{
    private readonly CliBinaryFixture _fixture;

    private static readonly string FixturesDir = FindFixturesDir();
    private static string ShaderToyDir => Path.Combine(FixturesDir, "shaders", "shadertoy");

    public CliShaderToyInputTest(CliBinaryFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("OpenGL")]
    [InlineData("DirectX_11")]
    public async Task Glsl_AutoDetected_Compiles_ExitCode0_EmptyStderr(string profile)
    {
        string sourceFile = Path.Combine(ShaderToyDir, "GradientToy.glsl");
        string outputFile = Path.Combine(Path.GetTempPath(), $"Gradient_{Guid.NewGuid():N}.mgfx");

        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(sourceFile, outputFile, $"/Profile:{profile}");

            stdout.ShouldBeEmpty("nothing must be written to stdout (MGCB contract)");
            stderr.ShouldBeEmpty(
                "a successful .glsl compile with no warnings must keep stderr empty " +
                $"(the MGCB empty-stderr contract); actual: {stderr}");
            exitCode.ShouldBe(0, customMessage: "ShaderToy/GLSL input is auto-detected and needs no flag");
            File.Exists(outputFile).ShouldBeTrue("a loadable effect blob must be produced");
            new FileInfo(outputFile).Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task Glsl_Reject_ExitCode1_LocatedMgcbDiagnosticOnGlsl()
    {
        string sourceFile = Path.Combine(ShaderToyDir, "RejectUndeclared.glsl");
        string outputFile = Path.Combine(Path.GetTempPath(), $"Reject_{Guid.NewGuid():N}.mgfx");

        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(sourceFile, outputFile, "/Profile:OpenGL");

            exitCode.ShouldBe(1, customMessage: "an unsupported construct must fail loudly, never silently");
            stdout.ShouldBeEmpty("nothing must ever go to stdout");
            // The MGCB contract, but pointing at the ORIGINAL .glsl with a real line/col (not the
            // synthetic .fx) and a dedicated SD#### convert code.
            stderr.ShouldMatch(@"\.glsl\(\d+,\d+(-\d+)?\): error SD\d+:", customMessage: "convert errors must use the MGCB 'file(line,col-col): error SDxxxx: message' " +
                         $"form with the original .glsl filename; actual stderr: {stderr}");
            File.Exists(outputFile).ShouldBeFalse("no output must be written on a convert failure");
        }
        finally
        {
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task Glsl_PrintUniforms_EmitsNote_DefaultDoesNot()
    {
        string sourceFile = Path.Combine(ShaderToyDir, "GradientToy.glsl");
        string outputFile = Path.Combine(Path.GetTempPath(), $"Uniforms_{Guid.NewGuid():N}.mgfx");

        try
        {
            var (exitWith, _, stderrWith) = await RunCliAsync(
                sourceFile, outputFile, "/Profile:OpenGL", "--print-uniforms");
            exitWith.ShouldBe(0);
            stderrWith.ShouldContain("note", Case.Sensitive, "the drivable-uniforms note prints with --print-uniforms");
            stderrWith.ShouldContain("iResolution", Case.Sensitive, "the gradient references iResolution");

            var (exitOff, _, stderrOff) = await RunCliAsync(sourceFile, outputFile, "/Profile:OpenGL");
            exitOff.ShouldBe(0);
            stderrOff.ShouldBeEmpty("the note is OFF by default, preserving the empty-stderr contract");
        }
        finally
        {
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task ShaderToySavedAsTxt_ForcedGlsl_Compiles()
    {
        // A ShaderToy shader saved with a non-GLSL extension; --input-format glsl forces the route.
        string txt = Path.Combine(Path.GetTempPath(), $"toy_{Guid.NewGuid():N}.txt");
        string outputFile = Path.Combine(Path.GetTempPath(), $"Txt_{Guid.NewGuid():N}.mgfx");
        await File.WriteAllTextAsync(txt, await File.ReadAllTextAsync(Path.Combine(ShaderToyDir, "GradientToy.glsl")));

        try
        {
            var (exitCode, _, stderr) = await RunCliAsync(txt, outputFile, "/Profile:OpenGL", "--input-format", "glsl");
            exitCode.ShouldBe(0, customMessage: $"--input-format glsl forces the converter on a .txt; stderr: {stderr}");
            File.Exists(outputFile).ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(txt)) File.Delete(txt);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task Glsl_LocalShadowingFunction_AutoRenamed_Compiles_WithWarning()
    {
        // F1: a local that shadows a called function (mat3 rot = rot(...)) is auto-renamed at convert
        // time, so the CLI compiles it instead of failing on generated HLSL with an opaque DXC error.
        string sourceFile = Path.Combine(ShaderToyDir, "RotShadow.glsl");
        string outputFile = Path.Combine(Path.GetTempPath(), $"RotShadow_{Guid.NewGuid():N}.mgfx");

        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(sourceFile, outputFile, "/Profile:OpenGL");

            exitCode.ShouldBe(0, customMessage: $"the shadow is auto-renamed and the shader compiles; stderr: {stderr}");
            stdout.ShouldBeEmpty();
            File.Exists(outputFile).ShouldBeTrue();
            // The rename surfaces as a located warning on the ORIGINAL .glsl (not a generated-HLSL line).
            stderr.ShouldMatch(@"RotShadow\.glsl\(\d+,\d+(-\d+)?\): warning SD\d+:");
            stderr.ShouldContain("shadows the function", Case.Sensitive);
        }
        finally
        {
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task Glsl_PipelineError_IsAttributedToGeneratedHlsl_NotTheGlslSource()
    {
        // F2: a shader that converts but fails the pipeline (DXC) compile must NOT stamp the .glsl
        // filename onto generated-HLSL line numbers. It leads with an SD0003 note and attributes the
        // error to "<name>.generated.fx".
        string source = Path.Combine(Path.GetTempPath(), $"badtype_{Guid.NewGuid():N}.glsl");
        string outputFile = Path.Combine(Path.GetTempPath(), $"badtype_{Guid.NewGuid():N}.mgfx");
        await File.WriteAllTextAsync(source,
            """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              vec3 bad = vec3(1.0, fragCoord, 0.0);
              fragColor = vec4(bad, 1.0);
            }
            """);

        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(source, outputFile, "/Profile:OpenGL");

            exitCode.ShouldBe(1);
            stdout.ShouldBeEmpty();
            stderr.ShouldContain("SD0003", Case.Sensitive, "the leading note explains the error is in generated HLSL");
            stderr.ShouldContain(".generated.fx", Case.Sensitive, "pipeline errors are attributed to the generated HLSL, not the user's .glsl");
            // The error line must NOT be stamped onto the original .glsl name.
            stderr.ShouldNotMatch(@"badtype_[0-9a-f]+\.glsl\(\d+,\d+(-\d+)?\): error", customMessage: "a generated-HLSL line number must never carry the original .glsl filename");
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task InvalidInputFormatValue_ExitCode1()
    {
        var (exitCode, stdout, stderr) = await RunCliAsync(
            "Shader.glsl", "Out.mgfx", "--input-format", "nonsense");

        exitCode.ShouldBe(1);
        stdout.ShouldBeEmpty();
        stderr.ShouldContain("X0011", Case.Sensitive, "an invalid --input-format value is a loud parse error");
    }

    [Fact]
    public async Task CliGlslOutput_IsByteIdentical_To_Convert_Plus_Pipeline()
    {
        // Proves the CLI's .glsl route adds NO behavior: its bytes equal ShaderToyConverter.Convert
        // fed through EffectCompiler with the same options the CLI derives.
        string sourceFile = Path.Combine(ShaderToyDir, "GradientToy.glsl");
        string cliOutput  = Path.Combine(Path.GetTempPath(), $"CliBytes_{Guid.NewGuid():N}.mgfx");

        try
        {
            var (exitCode, _, stderr) = await RunCliAsync(sourceFile, cliOutput, "/Profile:OpenGL");
            exitCode.ShouldBe(0, customMessage: $"CLI .glsl compile must succeed; stderr: {stderr}");
            byte[] cliBytes = await File.ReadAllBytesAsync(cliOutput);

            // Replicate the CLI's derivation: EffectName/TechniqueName = sanitized file-name-no-ext.
            string glsl = await File.ReadAllTextAsync(sourceFile);
            ConvertResult convert = ShaderToyConverter.Convert(glsl, new ConvertOptions
            {
                EffectName    = "GradientToy",
                TechniqueName = "GradientToy",
            });
            convert.Success.ShouldBeTrue();

            var compiler = new EffectCompiler();
            var result = await compiler.CompileAsync(convert.Fx!, new CompilerOptions
            {
                Target         = PlatformTarget.OpenGL,
                SourceFileName = sourceFile,
            });
            result.IsSuccess.ShouldBeTrue();

            cliBytes.ShouldBe(result.Value.Data, customMessage: "the CLI must be a thin front-end transform + the existing pipeline, adding no behavior");
        }
        finally
        {
            if (File.Exists(cliOutput)) File.Delete(cliOutput);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers (mirror CliIntegrationTest)
    // -------------------------------------------------------------------------

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        string? sourceFile, string? outputFile, params string[] extraArgs)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var argList = new List<string>();
        if (sourceFile is not null) argList.Add(sourceFile);
        if (outputFile is not null) argList.Add(outputFile);
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
            "Could not locate tests/fixtures directory.");
    }
}
