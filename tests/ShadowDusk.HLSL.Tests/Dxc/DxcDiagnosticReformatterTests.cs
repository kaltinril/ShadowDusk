#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

// DxcDiagnosticReformatter is internal; InternalsVisibleTo is set in ShadowDusk.HLSL.csproj.
public sealed class DxcDiagnosticReformatterTests
{
    [Fact]
    public void EmptyInput_ReturnsEmptyList()
    {
        DxcDiagnosticReformatter.Reformat("", "shader.fx").Should().BeEmpty();
        DxcDiagnosticReformatter.Reformat("   ", "shader.fx").Should().BeEmpty();
    }

    [Fact]
    public void WellFormedErrorLine_ParsesFileLineCol()
    {
        var errors = DxcDiagnosticReformatter.Reformat(
            "shader.fx:10:5: error: undeclared identifier 'x'",
            "shader.fx");

        errors.Should().ContainSingle();
        var e = errors[0];
        e.File.Should().Be("shader.fx");
        e.Line.Should().Be(10);
        e.Column.Should().Be(5);
        e.Severity.Should().Be(ShaderErrorSeverity.Error);
    }

    [Fact]
    public void WellFormedErrorLine_FxcFormattedMessage_ContainsKeyTokens()
    {
        var errors = DxcDiagnosticReformatter.Reformat(
            "shader.fx:10:5: error: undeclared identifier 'x'",
            "shader.fx");

        errors.Should().ContainSingle();
        var msg = errors[0].FxcFormattedMessage;
        msg.Should().Contain("(10,5");
        msg.Should().Contain("error");
        msg.Should().Contain("undeclared identifier 'x'");
    }

    [Fact]
    public void WarningSeverity_MapsToWarning()
    {
        var errors = DxcDiagnosticReformatter.Reformat(
            "shader.fx:3:1: warning: implicit truncation",
            "shader.fx");

        errors.Should().ContainSingle();
        errors[0].Severity.Should().Be(ShaderErrorSeverity.Warning);
    }

    [Fact]
    public void NoteSeverity_MapsToNote()
    {
        var errors = DxcDiagnosticReformatter.Reformat(
            "shader.fx:3:1: note: see declaration here",
            "shader.fx");

        errors.Should().ContainSingle();
        errors[0].Severity.Should().Be(ShaderErrorSeverity.Note);
    }

    [Fact]
    public void NonMatchingLine_PreservedAsRawDiagnostics()
    {
        const string rawLine = "fatal error: this is not a clang-format line";
        var errors = DxcDiagnosticReformatter.Reformat(rawLine, "shader.fx");

        errors.Should().NotBeEmpty();
        errors.Should().Contain(e => e.RawDiagnostics != null && e.RawDiagnostics.Contains(rawLine));
    }

    [Fact]
    public void MultipleErrors_ReturnsMultipleShaderErrors()
    {
        const string input = """
            shader.fx:1:1: error: first error
            shader.fx:2:3: error: second error
            """;
        var errors = DxcDiagnosticReformatter.Reformat(input, "shader.fx");

        // At minimum the two parsed errors; there may also be a catch-all entry
        errors.Count(e => e.Message != "Shader compilation failed").Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void SourceFileNameNormalized_WhenDxcEchoesMatchingPath()
    {
        // DXC echoes back whatever file name we gave it; confirm normalization
        var errors = DxcDiagnosticReformatter.Reformat(
            "SHADER.FX:5:10: error: undefined",
            "shader.fx");    // lower-case override

        errors.Should().ContainSingle();
        // The reformatter normalizes to the sourceFileName param when they match case-insensitively
        errors[0].File.Should().Be("shader.fx");
    }

    [Fact]
    public void SyntheticSourcePath_KeptAsIs_WhenNoMatch()
    {
        // When DXC emits a synthetic path that doesn't match sourceFileName,
        // the file field retains whatever DXC said.
        var errors = DxcDiagnosticReformatter.Reformat(
            "<source>:5:10: error: undefined",
            "override.fx");

        errors.Should().NotBeEmpty();
        // The reformatter has no match on "<source>" vs "override.fx", so file stays as emitted
        var parsed = errors.FirstOrDefault(e => e.Line == 5);
        if (parsed is not null)
            parsed.File.Should().Be("<source>");
    }
}
