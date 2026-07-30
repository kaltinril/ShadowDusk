#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Pins how <see cref="ShaderValidationReport.ToString"/> renders diagnostics. Printing the
/// report is the whole consumer story for <c>Validate</c>/<c>ValidateAsync</c>, so its text
/// is a contract, not incidental formatting.
/// </summary>
public sealed class ShaderValidationReportRenderingTests
{
    [Fact]
    public void ToString_LineLessDiagnosticWithAFile_NamesTheFile()
    {
        // The GL portability lint (SD0400-SD0402) reads the EMITTED GLSL, so its findings
        // carry a file but no line. The report used to print the file only when Line > 0,
        // leaving a multi-target report full of unattributed
        // "warning SD0401: ... 'MainPS' ..." lines - the same defect the CLI formatter had.
        var warning = new ShaderError(
            File: "Bloom.fx",
            Line: 0,
            Column: 0,
            Code: "SD0401",
            Message: "pixel shader 'MainPS' reads vTexCoord1",
            Severity: ShaderErrorSeverity.Warning);

        var report = new ShaderValidationReport(
            [new ShaderTargetValidation(PlatformTarget.OpenGL, Succeeded: true, [], [warning])]);

        string text = report.ToString();

        text.ShouldContain("Bloom.fx", Case.Sensitive, "a line-less finding must still say which effect it came from");
        text.ShouldContain("SD0401", Case.Sensitive);
    }

    [Fact]
    public void ToString_LocatedDiagnostic_KeepsTheFileLineColumnForm()
    {
        var error = new ShaderError(
            File: "Bloom.fx",
            Line: 12,
            Column: 5,
            Code: "X0000",
            Message: "undeclared identifier 'x'");

        var report = new ShaderValidationReport(
            [new ShaderTargetValidation(PlatformTarget.OpenGL, Succeeded: false, [error], [])]);

        report.ToString().ShouldContain("Bloom.fx(12,5)", Case.Sensitive);
    }

    [Fact]
    public void ToString_FileLessDiagnostic_OmitsTheFileEntirely()
    {
        var error = new ShaderError(
            File: "", Line: 0, Column: 0, Code: "SD0025", Message: "no file for this one");

        var report = new ShaderValidationReport(
            [new ShaderTargetValidation(PlatformTarget.Vulkan, Succeeded: false, [error], [])]);

        var lines = report.ToString().Replace("\r\n", "\n").Split('\n');
        string diagnosticLine = lines.Single(l => l.Contains("SD0025"));

        diagnosticLine.ShouldContain("no file for this one", Case.Sensitive);
        diagnosticLine.ShouldNotContain(" in ", Case.Sensitive, "there is no file to name");
        diagnosticLine.ShouldNotContain(" at ", Case.Sensitive, "there is no location to name");
    }
}
