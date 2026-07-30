#nullable enable

using Shouldly;
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
        DxcDiagnosticReformatter.Reformat("", "shader.fx").ShouldBeEmpty();
        DxcDiagnosticReformatter.Reformat("   ", "shader.fx").ShouldBeEmpty();
    }

    [Fact]
    public void WellFormedErrorLine_ParsesFileLineCol()
    {
        var errors = DxcDiagnosticReformatter.Reformat(
            "shader.fx:10:5: error: undeclared identifier 'x'",
            "shader.fx");

        errors.ShouldHaveSingleItem();
        var e = errors[0];
        e.File.ShouldBe("shader.fx");
        e.Line.ShouldBe(10);
        e.Column.ShouldBe(5);
        e.Severity.ShouldBe(ShaderErrorSeverity.Error);
    }

    [Fact]
    public void WellFormedErrorLine_FxcFormattedMessage_ContainsKeyTokens()
    {
        var errors = DxcDiagnosticReformatter.Reformat(
            "shader.fx:10:5: error: undeclared identifier 'x'",
            "shader.fx");

        errors.ShouldHaveSingleItem();
        var msg = errors[0].FxcFormattedMessage;
        msg.ShouldContain("(10,5", Case.Sensitive);
        msg.ShouldContain("error", Case.Sensitive);
        msg.ShouldContain("undeclared identifier 'x'", Case.Sensitive);
    }

    [Fact]
    public void WarningSeverity_MapsToWarning()
    {
        var errors = DxcDiagnosticReformatter.Reformat(
            "shader.fx:3:1: warning: implicit truncation",
            "shader.fx");

        errors.ShouldHaveSingleItem();
        errors[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
    }

    [Fact]
    public void NoteSeverity_MapsToNote()
    {
        var errors = DxcDiagnosticReformatter.Reformat(
            "shader.fx:3:1: note: see declaration here",
            "shader.fx");

        errors.ShouldHaveSingleItem();
        errors[0].Severity.ShouldBe(ShaderErrorSeverity.Note);
    }

    [Fact]
    public void NonMatchingLine_PreservedAsRawDiagnostics()
    {
        const string rawLine = "fatal error: this is not a clang-format line";
        var errors = DxcDiagnosticReformatter.Reformat(rawLine, "shader.fx");

        errors.ShouldNotBeEmpty();
        errors.ShouldContain(e => e.RawDiagnostics != null && e.RawDiagnostics.Contains(rawLine));
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
        errors.Count().ShouldBe(2);
        errors[0].Message.ShouldBe("first error");
        errors[1].Message.ShouldBe("second error");
    }

    [Fact]
    public void SourceFileNameNormalized_WhenDxcEchoesMatchingPath()
    {
        // DXC echoes back whatever file name we gave it; confirm normalization
        var errors = DxcDiagnosticReformatter.Reformat(
            "SHADER.FX:5:10: error: undefined",
            "shader.fx");    // lower-case override

        errors.ShouldHaveSingleItem();
        // The reformatter normalizes to the sourceFileName param when they match case-insensitively
        errors[0].File.ShouldBe("shader.fx");
    }

    [Fact]
    public void SyntheticSourcePath_KeptAsIs_WhenNoMatch()
    {
        // When DXC emits a synthetic path that doesn't match sourceFileName,
        // the file field retains whatever DXC said.
        var errors = DxcDiagnosticReformatter.Reformat(
            "<source>:5:10: error: undefined",
            "override.fx");

        errors.ShouldNotBeEmpty();
        // The reformatter has no match on "<source>" vs "override.fx", so file stays as emitted
        var parsed = errors.FirstOrDefault(e => e.Line == 5);
        if (parsed is not null)
            parsed.File.ShouldBe("<source>");
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

        errors.ShouldHaveSingleItem();
        errors[0].Message.ShouldContain("Internal Compiler error: llvm-ir verification failed", Case.Sensitive);
        errors[0].Message.ShouldContain("module has invalid SPIR-V", Case.Sensitive);
        errors[0].Message.ShouldNotBe("Shader compilation failed", customMessage: "the compiler's own words are the message now — never a generic sentence");
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

        errors.ShouldHaveSingleItem();
        errors[0].Line.ShouldBe(10);
    }

    [Fact]
    public void SelectPrimary_PrefersFirstErrorOverLeadingWarning()
    {
        const string raw = """
            shader.fx:3:1: warning: implicit truncation of vector type
            shader.fx:10:5: error: undeclared identifier 'x'
            """;
        var primary = DxcDiagnosticReformatter.SelectPrimary(raw, "shader.fx", "no diagnostics");

        primary.Severity.ShouldBe(ShaderErrorSeverity.Error, customMessage: "a warning must never masquerade as the failure");
        primary.Line.ShouldBe(10);
    }

    [Fact]
    public void SelectPrimary_CarriesTheCompleteRawText()
    {
        const string raw = """
            shader.fx:3:1: warning: implicit truncation of vector type
            shader.fx:10:5: error: undeclared identifier 'x'
            """;
        var primary = DxcDiagnosticReformatter.SelectPrimary(raw, "shader.fx", "no diagnostics");

        primary.RawDiagnostics!.ShouldContain("implicit truncation", Case.Sensitive);
        primary.RawDiagnostics!.ShouldContain(
            "undeclared identifier", Case.Sensitive,
            "the single-error backend contract must not drop the other diagnostics");
    }

    [Fact]
    public void SelectPrimary_EmptyText_UsesFallbackMessageAndCode()
    {
        var primary = DxcDiagnosticReformatter.SelectPrimary(
            "", "shader.fx", "compile failed with no diagnostics", fallbackCode: "SD9999");

        primary.Message.ShouldBe("compile failed with no diagnostics");
        primary.Code.ShouldBe("SD9999");
        primary.RawDiagnostics.ShouldBeNull();
    }

    [Fact]
    public void ReformatAsWarnings_NormalizesUnlocatedVerbatimEntryToWarning()
    {
        // On a SUCCESSFUL compile any unlocated verbatim entry cannot be an error.
        var warnings = DxcDiagnosticReformatter.ReformatAsWarnings(
            "note-ish free text the compiler printed on success", "shader.fx");

        warnings.ShouldHaveSingleItem();
        warnings[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warnings[0].Message.ShouldContain("note-ish free text", Case.Sensitive);
    }

    [Fact]
    public void ReformatAsWarnings_KeepsParsedWarningsVerbatim()
    {
        var warnings = DxcDiagnosticReformatter.ReformatAsWarnings(
            "shader.fx:3:1: warning: implicit truncation of vector type", "shader.fx");

        warnings.ShouldHaveSingleItem();
        warnings[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warnings[0].Line.ShouldBe(3);
        warnings[0].Message.ShouldBe("implicit truncation of vector type");
    }
}
