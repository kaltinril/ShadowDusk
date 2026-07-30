#nullable enable

using Shouldly;
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

        errors.Count().ShouldBe(1);
        ShaderError e = errors[0];
        e.File.ShouldBe("shader.fx");
        e.Line.ShouldBe(30);
        e.Column.ShouldBe(12);
        e.Code.ShouldBe("X3004");
        e.Message.ShouldBe("undeclared identifier 'foo'");
        e.Severity.ShouldBe(ShaderErrorSeverity.Error);
    }

    [Fact]
    public void ParsesColumnRange()
    {
        const string text = @"C:\path\shader.fx(12,5-9): error X3018: invalid subscript";

        IReadOnlyList<ShaderError> errors =
            D3DCompilerDiagnosticReformatter.Reformat(text, @"C:\path\shader.fx");

        errors.Count().ShouldBe(1);
        errors[0].Line.ShouldBe(12);
        errors[0].Column.ShouldBe(5);
        errors[0].Code.ShouldBe("X3018");
    }

    [Fact]
    public void ParsesWarningSeverity()
    {
        const string text = @"shader.fx(3,1): warning X3206: implicit truncation of vector type";

        IReadOnlyList<ShaderError> errors =
            D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.Count().ShouldBe(1);
        errors[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
    }

    [Fact]
    public void EmptyTextProducesNoErrors()
    {
        D3DCompilerDiagnosticReformatter.Reformat("", "shader.fx").ShouldBeEmpty();
    }

    [Fact]
    public void UnparseableTextIsSurfacedRawNotSwallowed()
    {
        const string text = "internal error: catastrophic failure";

        IReadOnlyList<ShaderError> errors =
            D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.ShouldHaveSingleItem();
        errors[0].RawDiagnostics!.ShouldContain("catastrophic failure", Case.Sensitive);
    }

    // ---- Phase 53: verbatim promotion + primary selection. ----

    [Fact]
    public void UnparseableTextOnly_MessageIsTheVerbatimText()
    {
        const string text = "internal error: catastrophic failure";

        var errors = D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.ShouldHaveSingleItem();
        errors[0].Message.ShouldBe("internal error: catastrophic failure", customMessage: "the compiler's own words are the message — never a generic sentence");
    }

    [Fact]
    public void SelectPrimary_PrefersFirstErrorOverLeadingWarning()
    {
        const string raw = """
            shader.fx(3,1): warning X3206: implicit truncation of vector type
            shader.fx(10,5): error X3004: undeclared identifier 'x'
            """;
        var primary = D3DCompilerDiagnosticReformatter.SelectPrimary(raw, "shader.fx", "no diagnostics");

        primary.Severity.ShouldBe(ShaderErrorSeverity.Error);
        primary.Code.ShouldBe("X3004");
        primary.RawDiagnostics!.ShouldContain("X3206", Case.Sensitive, "the complete text rides on the primary");
    }

    [Fact]
    public void SelectPrimary_EmptyText_UsesFallbackCode_TheVkd3dSd0212Contract()
    {
        // Vkd3dCompileContract.MapCompileFailure delegates here with SD0212 — the
        // code now fires ONLY when vkd3d emitted no text at all.
        var primary = D3DCompilerDiagnosticReformatter.SelectPrimary(
            "", "shader.fx", "vkd3d-shader DXBC compilation failed with no diagnostics",
            fallbackCode: "SD0212");

        primary.Code.ShouldBe("SD0212");
        primary.Message.ShouldContain("no diagnostics", Case.Sensitive);
    }

    [Fact]
    public void ReformatAsWarnings_ParsedWarningStaysLocatedAndVerbatim()
    {
        var warnings = D3DCompilerDiagnosticReformatter.ReformatAsWarnings(
            "shader.fx(3,1): warning X3206: implicit truncation of vector type", "shader.fx");

        warnings.ShouldHaveSingleItem();
        warnings[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warnings[0].Code.ShouldBe("X3206");
        warnings[0].Line.ShouldBe(3);
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

        errors.Count().ShouldBe(1);
        ShaderError e = errors[0];
        e.File.ShouldBe("shader.fx");
        e.Line.ShouldBe(12);
        e.Column.ShouldBe(5);
        e.Code.ShouldBe("E5005");
        e.Message.ShouldBe("Wrong type for argument 1 of 'mul'.");
        e.Severity.ShouldBe(ShaderErrorSeverity.Error);
    }

    [Fact]
    public void ParsesVkd3dColonStyleWCodeAsWarning()
    {
        const string text = "shader.fx:3:1: W5300: Truncating a vector.";

        var errors = D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.ShouldHaveSingleItem();
        errors[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
        errors[0].Code.ShouldBe("W5300");
        errors[0].Line.ShouldBe(3);
        errors[0].Column.ShouldBe(1);
    }

    [Fact]
    public void ParsesColonStyleSeverityWordWithoutCode()
    {
        const string text = "shader.fx:7:2: warning: implicit truncation of vector type";

        var errors = D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.ShouldHaveSingleItem();
        errors[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
        errors[0].Code.ShouldBe("X0000");
        errors[0].Message.ShouldBe("implicit truncation of vector type");
    }

    [Fact]
    public void ParsesColonStyleWithWindowsDriveLetterPath()
    {
        const string text = @"C:\game\Content\shader.fx:12:5: E5005: broken";

        var errors = D3DCompilerDiagnosticReformatter.Reformat(text, "shader.fx");

        errors.ShouldHaveSingleItem();
        errors[0].File.ShouldBe(@"C:\game\Content\shader.fx");
        errors[0].Line.ShouldBe(12);
        errors[0].Column.ShouldBe(5);
    }
}
