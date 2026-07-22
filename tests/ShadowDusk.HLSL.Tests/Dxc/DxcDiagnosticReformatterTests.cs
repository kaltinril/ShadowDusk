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
    public void MultipleErrors_ReturnsExactlyTheParsedErrors()
    {
        const string input = """
            shader.fx:1:1: error: first error
            shader.fx:2:3: error: second error
            """;
        var errors = DxcDiagnosticReformatter.Reformat(input, "shader.fx");

        // Exactly the two parsed errors — Phase 53 removed the fabricated
        // catch-all entry that used to ride along.
        errors.Should().HaveCount(2);
        errors[0].Message.Should().Be("first error");
        errors[1].Message.Should().Be("second error");
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

    // ---- Phase 53: verbatim promotion + primary selection (the "shader
    // compilation failed with no detail" field-report class). ----

    [Fact]
    public void UnparseableTextOnly_MessageIsTheVerbatimText_NeverAGenericSentence()
    {
        const string raw = """
            Internal Compiler error: llvm-ir verification failed
            module has invalid SPIR-V
            """;
        var errors = DxcDiagnosticReformatter.Reformat(raw, "shader.fx");

        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("Internal Compiler error: llvm-ir verification failed");
        errors[0].Message.Should().Contain("module has invalid SPIR-V");
        errors[0].Message.Should().NotBe("Shader compilation failed",
            "the compiler's own words are the message now — never a generic sentence");
    }

    [Fact]
    public void ParsedPlusUnmatchedContext_NoFabricatedExtraEntry()
    {
        // DXC prints the source line + caret under each diagnostic; that context
        // must not become a fake standalone error.
        const string raw = """
            shader.fx:10:5: error: undeclared identifier 'x'
                float y = x;
                          ^
            """;
        var errors = DxcDiagnosticReformatter.Reformat(raw, "shader.fx");

        errors.Should().ContainSingle();
        errors[0].Line.Should().Be(10);
    }

    [Fact]
    public void SelectPrimary_PrefersFirstErrorOverLeadingWarning()
    {
        const string raw = """
            shader.fx:3:1: warning: implicit truncation of vector type
            shader.fx:10:5: error: undeclared identifier 'x'
            """;
        var primary = DxcDiagnosticReformatter.SelectPrimary(raw, "shader.fx", "no diagnostics");

        primary.Severity.Should().Be(ShaderErrorSeverity.Error,
            "a warning must never masquerade as the failure");
        primary.Line.Should().Be(10);
    }

    [Fact]
    public void SelectPrimary_CarriesTheCompleteRawText()
    {
        const string raw = """
            shader.fx:3:1: warning: implicit truncation of vector type
            shader.fx:10:5: error: undeclared identifier 'x'
            """;
        var primary = DxcDiagnosticReformatter.SelectPrimary(raw, "shader.fx", "no diagnostics");

        primary.RawDiagnostics.Should().Contain("implicit truncation")
            .And.Contain("undeclared identifier",
                "the single-error backend contract must not drop the other diagnostics");
    }

    [Fact]
    public void SelectPrimary_EmptyText_UsesFallbackMessageAndCode()
    {
        var primary = DxcDiagnosticReformatter.SelectPrimary(
            "", "shader.fx", "compile failed with no diagnostics", fallbackCode: "SD9999");

        primary.Message.Should().Be("compile failed with no diagnostics");
        primary.Code.Should().Be("SD9999");
        primary.RawDiagnostics.Should().BeNull();
    }

    [Fact]
    public void ReformatAsWarnings_NormalizesUnlocatedVerbatimEntryToWarning()
    {
        // On a SUCCESSFUL compile any unlocated verbatim entry cannot be an error.
        var warnings = DxcDiagnosticReformatter.ReformatAsWarnings(
            "note-ish free text the compiler printed on success", "shader.fx");

        warnings.Should().ContainSingle();
        warnings[0].Severity.Should().Be(ShaderErrorSeverity.Warning);
        warnings[0].Message.Should().Contain("note-ish free text");
    }

    [Fact]
    public void ReformatAsWarnings_KeepsParsedWarningsVerbatim()
    {
        var warnings = DxcDiagnosticReformatter.ReformatAsWarnings(
            "shader.fx:3:1: warning: implicit truncation of vector type", "shader.fx");

        warnings.Should().ContainSingle();
        warnings[0].Severity.Should().Be(ShaderErrorSeverity.Warning);
        warnings[0].Line.Should().Be(3);
        warnings[0].Message.Should().Be("implicit truncation of vector type");
    }
}
