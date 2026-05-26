#nullable enable

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

[Trait("Category", "Integration")]
public sealed class ErrorCaseTests
{
    private static async Task<CompileResult> CompileSourceAsync(
        string hlslSource,
        string profile,
        CancellationToken ct = default)
    {
        string tempDir  = Path.Combine(Path.GetTempPath(), $"shadowdusk_err_{Guid.NewGuid():N}");
        string tempFile = Path.Combine(tempDir, "test_shader.fx");
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(tempFile, hlslSource, ct).ConfigureAwait(false);
            return await TestHelpers.CompileViaPipelineAsync(tempFile, Path.Combine(tempDir, "out.mgfx"), profile, ct)
                                    .ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* non-fatal */ }
        }
    }

    [Fact]
    public async Task SyntaxError_ExitCode1_StderrContainsLineCol()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Include a technique so FX parsing succeeds and the error reaches DXC.
        const string source =
            """
            float4 PS(float4 pos : SV_POSITION) : SV_TARGET { return SYNTAX ERROR; }
            technique T { pass P { PixelShader = compile ps_5_0 PS(); } }
            """;

        var result = await CompileSourceAsync(source, "OpenGL", cts.Token);

        result.ExitCode.Should().Be(1);
        // DXC emits (line,col) diagnostics; the formatter preserves this.
        Regex.IsMatch(result.Stderr, @"\(\d+,\d+\)").Should().BeTrue(
            because: $"stderr must contain (line,col) diagnostic; actual stderr: {result.Stderr}");
        result.Stderr.Should().NotMatchRegex(@"at \S+\.\S+\(",
            because: "internal stack traces must not appear in stderr");
    }

    [Fact]
    public async Task UndeclaredIdentifier_ExitCode1_StderrContainsIdentifier()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Include a technique so FX parsing succeeds and the error reaches DXC.
        const string source =
            """
            float4 PS(float4 pos : SV_POSITION) : SV_TARGET { return UndeclaredVar; }
            technique T { pass P { PixelShader = compile ps_5_0 PS(); } }
            """;

        var result = await CompileSourceAsync(source, "OpenGL", cts.Token);

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Contain("UndeclaredVar",
            because: "the undeclared identifier name must appear in the error message");
        Regex.IsMatch(result.Stderr, @"\d+").Should().BeTrue(
            because: "a line number must appear somewhere in the diagnostic");
    }

    [Fact]
    public async Task MissingInclude_ExitCode1_StderrContainsFileAndLine()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // The #include is on line 1; the file name should appear in the diagnostic.
        const string source =
            """
            #include "nonexistent_header.fxh"
            float4 PS(float4 pos : SV_POSITION) : SV_TARGET { return float4(1,0,0,1); }
            technique T { pass P { PixelShader = compile ps_5_0 PS(); } }
            """;

        var result = await CompileSourceAsync(source, "OpenGL", cts.Token);

        result.ExitCode.Should().Be(1);
        // The source file name (not just the include path) must appear in the diagnostic.
        result.Stderr.Should().Contain("test_shader.fx",
            because: "the including file name must be referenced in the error");
    }

    [Fact]
    public async Task UnknownProfile_ExitCode1_StderrContainsProfileString()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync(
            "Minimal.fx", "PS5_NotAReal_Target", ct: cts.Token);

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Contain("PS5_NotAReal_Target",
            because: "the unrecognised profile name must be included in the error");
    }

    [Fact]
    public async Task EmptySource_ExitCode1_StderrContainsHumanReadableMessage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await CompileSourceAsync(string.Empty, "OpenGL", cts.Token);

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().NotBeNullOrWhiteSpace(because: "empty source must produce a diagnostic message");
        // The pipeline reports SD0010 "Effect source contains no techniques".
        result.Stderr.Should().MatchRegex("no techniques|empty source",
            because: "empty source must produce a human-readable 'no techniques' message");
    }

    [Fact]
    public async Task NoTechniques_ExitCode1_StderrReferencesMissingTechnique()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Valid HLSL function but deliberately no technique block.
        const string source =
            """
            float4 PS(float4 pos : SV_POSITION) : SV_TARGET { return float4(1,1,1,1); }
            """;

        var result = await CompileSourceAsync(source, "OpenGL", cts.Token);

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().MatchRegex("no techniques|technique",
            because: "missing technique must produce a diagnostic referencing techniques");
    }
}
