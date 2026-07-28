#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using Xunit;

namespace ShadowDusk.HLSL.Tests.D3DCompiler;

// D3DCompilerDiagnosticReformatter is internal; InternalsVisibleTo is set in
// ShadowDusk.HLSL.csproj. These are pure unit tests (no native interop), so they
// run on every platform.
public sealed class D3DCompilerDiagnosticReformatterTests
{
    [Fact]
    public void ParsesFxcStyleErrorWithLineColumnAndCode()
    {
        const string text = @"shader.fx(30,12): error X3004: undeclared identifier 'foo'";

        IReadOnlyList<ShaderError> errors =
            D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.Should().HaveCount(1);
        ShaderError e = errors[0];
        e.File.Should().Be("shader.fx");
        e.Line.Should().Be(30);
        e.Column.Should().Be(12);
        e.Code.Should().Be("X3004");
        e.Message.Should().Be("undeclared identifier 'foo'");
        e.Severity.Should().Be(ShaderErrorSeverity.Error);
    }

    [Fact]
    public void ParsesColumnRange()
    {
        const string text = @"C:\path\shader.fx(12,5-9): error X3018: invalid subscript";

        IReadOnlyList<ShaderError> errors =
            D3DCompilerDiagnosticReformatter.Reformat(text, @"C:\path\shader.fx");

        errors.Should().HaveCount(1);
        errors[0].Line.Should().Be(12);
        errors[0].Column.Should().Be(5);
        errors[0].Code.Should().Be("X3018");
    }

    [Fact]
    public void ParsesWarningSeverity()
    {
        const string text = @"shader.fx(3,1): warning X3206: implicit truncation of vector type";

        IReadOnlyList<ShaderError> errors =
            D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.Should().HaveCount(1);
        errors[0].Severity.Should().Be(ShaderErrorSeverity.Warning);
    }

    [Fact]
    public void EmptyTextProducesNoErrors()
    {
        D3DCompilerDiagnosticReformatter.Reformat("", "shader.fx").Should().BeEmpty();
    }

    [Fact]
    public void UnparseableTextIsSurfacedRawNotSwallowed()
    {
        const string text = "internal error: catastrophic failure";

        IReadOnlyList<ShaderError> errors =
            D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.Should().ContainSingle();
        errors[0].RawDiagnostics.Should().Contain("catastrophic failure");
    }

    // ---- Phase 53: verbatim promotion + primary selection. ----

    [Fact]
    public void UnparseableTextOnly_MessageIsTheVerbatimText()
    {
        const string text = "internal error: catastrophic failure";

        var errors = D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.Should().ContainSingle();
        errors[0].Message.Should().Be("internal error: catastrophic failure",
            "the compiler's own words are the message — never a generic sentence");
    }

    [Fact]
    public void SelectPrimary_PrefersFirstErrorOverLeadingWarning()
    {
        const string raw = """
            shader.fx(3,1): warning X3206: implicit truncation of vector type
            shader.fx(10,5): error X3004: undeclared identifier 'x'
            """;
        var primary = D3DCompilerDiagnosticReformatter.SelectPrimary(raw, "shader.fx", "no diagnostics");

        primary.Severity.Should().Be(ShaderErrorSeverity.Error);
        primary.Code.Should().Be("X3004");
        primary.RawDiagnostics.Should().Contain("X3206", "the complete text rides on the primary");
    }

    [Fact]
    public void SelectPrimary_EmptyText_UsesFallbackCode_TheVkd3dSd0212Contract()
    {
        // Vkd3dCompileContract.MapCompileFailure delegates here with SD0212 — the
        // code now fires ONLY when vkd3d emitted no text at all.
        var primary = D3DCompilerDiagnosticReformatter.SelectPrimary(
            "", "shader.fx", "vkd3d-shader DXBC compilation failed with no diagnostics",
            fallbackCode: "SD0212");

        primary.Code.Should().Be("SD0212");
        primary.Message.Should().Contain("no diagnostics");
    }

    [Fact]
    public void ReformatAsWarnings_ParsedWarningStaysLocatedAndVerbatim()
    {
        var warnings = D3DCompilerDiagnosticReformatter.ReformatAsWarnings(
            "shader.fx(3,1): warning X3206: implicit truncation of vector type", "shader.fx");

        warnings.Should().ContainSingle();
        warnings[0].Severity.Should().Be(ShaderErrorSeverity.Warning);
        warnings[0].Code.Should().Be("X3206");
        warnings[0].Line.Should().Be(3);
    }

    // -------------------------------------------------------------------------
    // vkd3d-shader colon-style diagnostics (bug-hunt 2026-07-27 N9): these used
    // to collapse into one line-less X0000 entry, losing file/line/column.
    // -------------------------------------------------------------------------

    [Fact]
    public void ParsesVkd3dColonStyleWithErrorCode()
    {
        const string text = "shader.fx:12:5: E5005: Wrong type for argument 1 of 'mul'.";

        IReadOnlyList<ShaderError> errors =
            D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.Should().HaveCount(1);
        ShaderError e = errors[0];
        e.File.Should().Be("shader.fx");
        e.Line.Should().Be(12);
        e.Column.Should().Be(5);
        e.Code.Should().Be("E5005");
        e.Message.Should().Be("Wrong type for argument 1 of 'mul'.");
        e.Severity.Should().Be(ShaderErrorSeverity.Error);
    }

    [Fact]
    public void ParsesVkd3dColonStyleWCodeAsWarning()
    {
        const string text = "shader.fx:3:1: W5300: Truncating a vector.";

        var errors = D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.Should().ContainSingle();
        errors[0].Severity.Should().Be(ShaderErrorSeverity.Warning);
        errors[0].Code.Should().Be("W5300");
        errors[0].Line.Should().Be(3);
        errors[0].Column.Should().Be(1);
    }

    [Fact]
    public void ParsesColonStyleSeverityWordWithoutCode()
    {
        const string text = "shader.fx:7:2: warning: implicit truncation of vector type";

        var errors = D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.Should().ContainSingle();
        errors[0].Severity.Should().Be(ShaderErrorSeverity.Warning);
        errors[0].Code.Should().Be("X0000");
        errors[0].Message.Should().Be("implicit truncation of vector type");
    }

    [Fact]
    public void ParsesColonStyleWithWindowsDriveLetterPath()
    {
        const string text = @"C:\game\Content\shader.fx:12:5: E5005: broken";

        var errors = D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.Should().ContainSingle();
        errors[0].File.Should().Be(@"C:\game\Content\shader.fx");
        errors[0].Line.Should().Be(12);
        errors[0].Column.Should().Be(5);
    }
}
