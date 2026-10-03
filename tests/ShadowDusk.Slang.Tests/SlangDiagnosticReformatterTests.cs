#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Pure tests (no disk, no process) for <see cref="SlangDiagnosticReformatter"/>, using
/// literal stderr text shaped exactly like real slangc output (Phase 66 A3: the exact shape
/// pinned here was captured from a real slangc v2026.14.1 run against a deliberately broken
/// source; see <c>SlangCompilerTests</c> for the live-process version of the same case).
/// </summary>
public sealed class SlangDiagnosticReformatterTests
{
    private const string RealSlangcStderr = """
        error[E30015]: undefined identifier
         --> broken.slang:4:12
          |
        4 | return bogus_call(1,2,3);
          |        ^^^^^^^^^^ undefined identifier 'bogus_call'.
        --'

        """;

    [Fact]
    public void Reformat_LocatesFileLineColumn_FromTheArrowLine()
    {
        var errors = SlangDiagnosticReformatter.Reformat(RealSlangcStderr, "MyShader.slang");

        var error = errors.ShouldHaveSingleItem();
        error.File.ShouldBe("MyShader.slang");
        error.Line.ShouldBe(4);
        error.Column.ShouldBe(12);
        error.Code.ShouldBe("E30015");
        error.Severity.ShouldBe(ShaderErrorSeverity.Error);
    }

    [Fact]
    public void Reformat_KeepsTheCompleteBlockVerbatim_InMessageAndRawDiagnostics()
    {
        var error = SlangDiagnosticReformatter.Reformat(RealSlangcStderr, "s.slang").Single();

        // Never reword the compiler's own text: the code frame (the "4 | return..." /
        // "^^^^^^^^^^" lines) must survive, not just the one-line summary.
        error.Message.ShouldContain("undefined identifier", Case.Sensitive);
        error.Message.ShouldContain("bogus_call", Case.Sensitive);
        error.Message.ShouldContain("^^^^^^^^^^", Case.Sensitive);
        error.RawDiagnostics.ShouldNotBeNull();
        error.RawDiagnostics!.ShouldContain("bogus_call", Case.Sensitive);
    }

    [Fact]
    public void Reformat_AlwaysUsesTheCallersSourceName_NeverSlangcsEchoedPath()
    {
        // slangc echoes back the temp/"<stdin>" name it was invoked with — SlangCompiler
        // always pipes source over stdin, so the real echoed name is "<stdin>", never the
        // caller's logical file name. There is exactly one input per compile, so the
        // caller's name always wins, unambiguously.
        var error = SlangDiagnosticReformatter.Reformat(RealSlangcStderr, "Logical.slang").Single();

        error.File.ShouldBe("Logical.slang");
    }

    [Fact]
    public void Reformat_MultipleDiagnostics_SplitsIntoSeparateErrors()
    {
        const string twoErrors = """
            warning[W1234]: unused variable
             --> f.slang:2:5
              |
            2 | float unused;
              |       ^^^^^^
            --'
            error[E30015]: undefined identifier
             --> f.slang:4:12
              |
            4 | return bogus_call();
              |        ^^^^^^^^^^
            --'

            """;

        var errors = SlangDiagnosticReformatter.Reformat(twoErrors, "f.slang");

        errors.Count.ShouldBe(2);
        errors[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
        errors[0].Line.ShouldBe(2);
        errors[1].Severity.ShouldBe(ShaderErrorSeverity.Error);
        errors[1].Line.ShouldBe(4);
    }

    [Fact]
    public void SelectPrimary_PrefersTheErrorOverALeadingWarning()
    {
        const string warningThenError = """
            warning[W1234]: unused variable
             --> f.slang:2:5
              |
            2 | float unused;
              |       ^^^^^^
            --'
            error[E30015]: undefined identifier
             --> f.slang:4:12
              |
            4 | return bogus_call();
              |        ^^^^^^^^^^
            --'

            """;

        ShaderError primary = SlangDiagnosticReformatter.SelectPrimary(
            warningThenError, "f.slang", "MainPS", "fragment");

        primary.Severity.ShouldBe(ShaderErrorSeverity.Error);
        primary.Line.ShouldBe(4);
    }

    [Fact]
    public void Reformat_EmptyText_ReturnsNoErrors()
    {
        SlangDiagnosticReformatter.Reformat("", "f.slang").ShouldBeEmpty();
        SlangDiagnosticReformatter.Reformat("   \n  ", "f.slang").ShouldBeEmpty();
    }

    [Fact]
    public void Reformat_UnrecognizedShape_FallsBackToVerbatimTextWithSD0622_NeverInventingAMessage()
    {
        const string unstructuredCrash = "Segmentation fault (core dumped)";

        var error = SlangDiagnosticReformatter.Reformat(unstructuredCrash, "f.slang").Single();

        error.Code.ShouldBe("SD0622");
        error.Message.ShouldBe(unstructuredCrash);
        error.RawDiagnostics.ShouldBe(unstructuredCrash);
    }

    [Fact]
    public void SelectPrimary_NoStderrAtAll_NamesTheEntryPointAndStage()
    {
        ShaderError primary = SlangDiagnosticReformatter.SelectPrimary("", "f.slang", "MainVS", "vertex");

        primary.Code.ShouldBe("SD0622");
        primary.Message.ShouldContain("MainVS", Case.Sensitive);
        primary.Message.ShouldContain("vertex", Case.Sensitive);
    }

    [Theory]
    // Windows reports a crash as its NTSTATUS (0xC0000005, an access violation, measured for
    // issue #323's same-named constant-buffer members); Unix as 128 + the signal (SIGSEGV).
    [InlineData(-1073741819, "0xC0000005")]
    [InlineData(139, "0x0000008B")]
    public void SelectPrimary_CrashExitCodeWithNoStderr_NamesTheCrashAndItsKnownTrigger(int exitCode, string hex)
    {
        ShaderError primary = SlangDiagnosticReformatter.SelectPrimary("", "f.slang", "MainPS", "fragment", exitCode);

        primary.Code.ShouldBe("SD0622");
        primary.Message.ShouldContain("terminated abnormally", Case.Sensitive);
        primary.Message.ShouldContain($"exit code {exitCode}, {hex}", Case.Sensitive);
        primary.Message.ShouldContain("'MainPS' (fragment)", Case.Sensitive);
        primary.Message.ShouldContain("issue #323", Case.Sensitive);
        primary.Message.ShouldContain("SD0643", Case.Sensitive);
    }

    [Fact]
    public void SelectPrimary_PlainFailureExitCodeWithNoStderr_IsNotCalledACrash()
    {
        ShaderError primary = SlangDiagnosticReformatter.SelectPrimary("", "f.slang", "MainPS", "fragment", 1);

        primary.Code.ShouldBe("SD0622");
        primary.Message.ShouldContain("(exit code 1)", Case.Sensitive);
        primary.Message.ShouldNotContain("crash", Case.Sensitive);
    }

    [Fact]
    public void SelectPrimary_CrashWithUnstructuredStderr_KeepsSlangcsOwnWordsVerbatim_AfterNamingTheCrash()
    {
        const string words = "Segmentation fault (core dumped)";

        ShaderError primary = SlangDiagnosticReformatter.SelectPrimary(words, "f.slang", "MainPS", "fragment", 139);

        primary.Code.ShouldBe("SD0622");
        primary.Message.ShouldContain("terminated abnormally", Case.Sensitive);
        primary.Message.ShouldEndWith("\n" + words, Case.Sensitive);
        primary.RawDiagnostics.ShouldBe(words);
    }

    [Fact]
    public void SelectPrimary_CrashAfterWarningsOnly_IsTheCrash_NotAWarningPromotedToAnError()
    {
        // Measured (issue #323): slangc prints its 'implicit global shader parameter' warnings for
        // both declarations and then dies; the first warning must not become the failure.
        const string warnings = """
            warning[E39019]: implicit global shader parameter
             --> <stdin>:1:22
              |
            1 | namespace A { float4 Tint; }
              |                      ^^^^ 'Tint' is implicitly a global shader parameter, not a global variable.
            --'

            """;

        ShaderError primary = SlangDiagnosticReformatter.SelectPrimary(warnings, "f.slang", "MainPS", "fragment", -1073741819);

        primary.Code.ShouldBe("SD0622");
        primary.Severity.ShouldBe(ShaderErrorSeverity.Error);
        primary.Message.ShouldContain("terminated abnormally", Case.Sensitive);
        primary.Message.ShouldContain("after the output below", Case.Sensitive);
        primary.Message.ShouldContain("warning[E39019]: implicit global shader parameter", Case.Sensitive);
    }

    [Fact]
    public void SelectPrimary_CrashAfterAnError_IsSlangcsOwnError()
    {
        const string stderr = "error[E30015]: undefined identifier\n --> <stdin>:5:85\n";

        ShaderError primary = SlangDiagnosticReformatter.SelectPrimary(stderr, "f.slang", "MainPS", "fragment", 139);

        primary.Code.ShouldBe("E30015");
    }
}
