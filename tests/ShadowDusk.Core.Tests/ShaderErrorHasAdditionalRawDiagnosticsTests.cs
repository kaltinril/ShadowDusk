#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Regression coverage for <see cref="ShaderError.HasAdditionalRawDiagnostics"/>: the
/// single shared rule every diagnostic-printing surface (CLI, <see cref="ShaderValidationReport"/>,
/// ShaderFiddle) uses to decide whether to print <see cref="ShaderError.RawDiagnostics"/> a
/// second time under the one-line summary. Must stay false for the ordinary single-located-
/// error compile failure (the common case — printing the raw compiler line again there is
/// pure noise) and true whenever the raw text carries information the one-liner does not
/// (another diagnostic, source-echo/caret context).
/// </summary>
public sealed class ShaderErrorHasAdditionalRawDiagnosticsTests
{
    [Fact]
    public void NoRawDiagnostics_False()
    {
        var error = new ShaderError(File: "s.fx", Line: 1, Column: 1, Code: "X0001", Message: "m");

        error.HasAdditionalRawDiagnostics.Should().BeFalse();
    }

    [Fact]
    public void SingleLocatedDiagnostic_RawIsJustTheSameLineReformatted_False()
    {
        // The ordinary case: one compiler diagnostic, already fully represented by
        // File/Line/Column/Message. Printing the raw line again below the formatted
        // summary would be pure duplication.
        var error = new ShaderError(
            File: "shader.fx",
            Line: 10,
            Column: 5,
            Code: "X0000",
            Message: "undeclared identifier 'x'",
            RawDiagnostics: "shader.fx:10:5: error: undeclared identifier 'x'");

        error.HasAdditionalRawDiagnostics.Should().BeFalse(
            "the raw line adds nothing beyond what Message/File/Line/Column already say");
    }

    [Fact]
    public void UnlocatedVerbatimText_MessageEqualsRaw_False()
    {
        const string verbatim = "Internal Compiler error: llvm-ir verification failed";
        var error = new ShaderError(
            File: "shader.fx", Line: 0, Column: 0, Code: "X0000",
            Message: verbatim, RawDiagnostics: verbatim);

        error.HasAdditionalRawDiagnostics.Should().BeFalse(
            "Message already IS the complete raw text for the no-location path");
    }

    [Fact]
    public void MultiDiagnosticRawText_LeadingWarningPlusTheSelectedError_True()
    {
        // SelectPrimary's shape: Message is just the primary error; RawDiagnostics
        // carries the whole original text, including a warning the summary line
        // does not mention. That extra line must still reach the user.
        var error = new ShaderError(
            File: "shader.fx",
            Line: 10,
            Column: 5,
            Code: "X0000",
            Message: "undeclared identifier 'x'",
            RawDiagnostics: "shader.fx:3:1: warning: implicit truncation of vector type\n" +
                            "shader.fx:10:5: error: undeclared identifier 'x'");

        error.HasAdditionalRawDiagnostics.Should().BeTrue(
            "the leading warning line is information the one-liner never shows");
    }

    [Fact]
    public void SourceEchoAndCaretContext_True()
    {
        var error = new ShaderError(
            File: "shader.fx",
            Line: 10,
            Column: 5,
            Code: "X0000",
            Message: "undeclared identifier 'x'",
            RawDiagnostics: "shader.fx:10:5: error: undeclared identifier 'x'\n" +
                            "    float y = x;\n" +
                            "              ^");

        error.HasAdditionalRawDiagnostics.Should().BeTrue(
            "the source line + caret is context the one-liner does not carry");
    }
}
