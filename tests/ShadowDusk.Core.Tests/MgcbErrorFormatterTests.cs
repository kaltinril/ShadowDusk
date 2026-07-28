#nullable enable

using FluentAssertions;
using ShadowDusk.Cli;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

public sealed class MgcbErrorFormatterTests
{
    // -------------------------------------------------------------------------
    // Format — single error
    // -------------------------------------------------------------------------

    [Fact]
    public void Format_FullLocation_EmitsCorrectMgcbLine()
    {
        var error = new ShaderError(
            File: "Foo.fx",
            Line: 11,
            Column: 44,
            Code: "X4502",
            Message: "bad semantic");

        var formatted = MgcbErrorFormatter.Format(error);

        formatted.Should().Be("Foo.fx(11,44-44): error X4502: bad semantic");
    }

    [Fact]
    public void Format_NoLocation_EmitsLocationlessLine()
    {
        var error = new ShaderError(
            File: "",
            Line: 0,
            Column: 0,
            Code: "X0003",
            Message: "message");

        var formatted = MgcbErrorFormatter.Format(error);

        formatted.Should().Be("error X0003: message");
    }

    [Fact]
    public void Format_FileButNoLine_StillLeadsWithTheFilename()
    {
        // The GL portability lint (SD0400-SD0402) reads the EMITTED GLSL, so its findings
        // have a source file but no line mapping back to the .fx. They must still say WHICH
        // effect they came from: an MGCB build compiling many effects previously printed a
        // bare "warning SD0401: ..." with no attribution.
        var warning = new ShaderError(
            File: "Bloom.fx",
            Line: 0,
            Column: 0,
            Code: "SD0401",
            Message: "The pass has no vertex shader, and pixel shader 'MainPS' reads vTexCoord1",
            Severity: ShaderErrorSeverity.Warning);

        var formatted = MgcbErrorFormatter.Format(warning);

        formatted.Should().Be(
            "Bloom.fx: warning SD0401: The pass has no vertex shader, "
            + "and pixel shader 'MainPS' reads vTexCoord1");
    }

    [Fact]
    public void Format_FileButNoLine_KeepsPathAsGiven()
    {
        // INTENTIONAL behavior change (bug-hunt 2026-07-27 N15): the formatter used to
        // strip the directory, which made two same-named includes indistinguishable and
        // broke IDE/MSBuild jump-to-file from the MGCB log. fxc/mgfxc echo the path they
        // were given; so do we now. This test previously pinned the stripping behavior.
        var warning = new ShaderError(
            File: "/abs/path/to/Bloom.fx",
            Line: 0,
            Column: 0,
            Code: "SD0400",
            Message: "gradient in a divergent loop",
            Severity: ShaderErrorSeverity.Warning);

        var formatted = MgcbErrorFormatter.Format(warning);

        formatted.Should().StartWith("/abs/path/to/Bloom.fx: warning SD0400:");
    }

    [Fact]
    public void Format_WarningLevel_UsesWarningKeyword()
    {
        var error = new ShaderError(
            File: "Foo.fx",
            Line: 3,
            Column: 1,
            Code: "X1234",
            Message: "msg",
            Severity: ShaderErrorSeverity.Warning);

        var formatted = MgcbErrorFormatter.Format(error);

        formatted.Should().Be("Foo.fx(3,1-1): warning X1234: msg");
    }

    [Fact]
    public void Format_PathKeptVerbatim_FullPathInOutput()
    {
        // INTENTIONAL behavior change (bug-hunt 2026-07-27 N15): the located form keeps
        // the full path exactly as the compiler was given it — two same-named includes
        // from different directories must stay distinguishable. This test previously
        // pinned the basename-only behavior.
        var error = new ShaderError(
            File: "/abs/path/to/Foo.fx",
            Line: 1,
            Column: 1,
            Code: "X0001",
            Message: "m");

        var formatted = MgcbErrorFormatter.Format(error);

        formatted.Should().StartWith("/abs/path/to/Foo.fx(1,1-1): error X0001:");
    }

    [Fact]
    public void Format_CodeZeroPadded_RawIntegerGetsXPrefix()
    {
        var error = new ShaderError(
            File: "F.fx",
            Line: 1,
            Column: 1,
            Code: "501",
            Message: "m");

        var formatted = MgcbErrorFormatter.Format(error);

        // Raw integer "501" must be formatted as "X0501"
        formatted.Should().Contain("X0501");
    }

    [Fact]
    public void Format_CodeAlreadyFormatted_PassesThroughUnchanged()
    {
        var error = new ShaderError(
            File: "F.fx",
            Line: 1,
            Column: 1,
            Code: "X4502",
            Message: "m");

        var formatted = MgcbErrorFormatter.Format(error);

        // Already-formatted "X4502" must not be double-prefixed or altered
        formatted.Should().Contain("X4502");
        formatted.Should().NotContain("XX4502");
    }

    // -------------------------------------------------------------------------
    // FormatAll — collection overload
    // -------------------------------------------------------------------------

    [Fact]
    public void FormatAll_MultiLineMessage_IndentsEveryContinuationLine()
    {
        // A verbatim compiler blob becomes the Message, so Message CAN be multi-line. Only
        // its first line is the parseable diagnostic; the rest must be indented, which is the
        // contract the CLI reference documents. Without this the continuation lines are
        // emitted flush-left, indistinguishable from a new diagnostic.
        var error = new ShaderError(
            File: "Bloom.fx",
            Line: 0,
            Column: 0,
            Code: "X0000",
            Message: "error: first problem\nerror: second problem\nerror: third problem");

        var lines = MgcbErrorFormatter.FormatAll([error]).ToList();

        lines.Should().HaveCount(3);
        lines[0].Should().Be("Bloom.fx: error X0000: error: first problem");
        lines[1].Should().Be("    error: second problem");
        lines[2].Should().Be("    error: third problem");
    }

    [Fact]
    public void FormatAll_SingleLineMessage_IsNotIndented()
    {
        var error = new ShaderError(
            File: "Bloom.fx", Line: 3, Column: 1, Code: "X0001", Message: "one line");

        var lines = MgcbErrorFormatter.FormatAll([error]).ToList();

        lines.Should().ContainSingle();
        lines[0].Should().NotStartWith(" ", "the parseable diagnostic must stay flush-left");
    }

    [Fact]
    public void FormatAll_EmptyList_ReturnsEmptyEnumerable()
    {
        var result = MgcbErrorFormatter.FormatAll([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FormatAll_MultipleErrors_ReturnsThreeStringsInInputOrder()
    {
        var errors = new[]
        {
            new ShaderError(File: "A.fx", Line: 1, Column: 1, Code: "X0001", Message: "first"),
            new ShaderError(File: "B.fx", Line: 2, Column: 2, Code: "X0002", Message: "second"),
            new ShaderError(File: "C.fx", Line: 3, Column: 3, Code: "X0003", Message: "third"),
        };

        var result = MgcbErrorFormatter.FormatAll(errors).ToList();

        result.Should().HaveCount(3);
        result[0].Should().Contain("first");
        result[1].Should().Contain("second");
        result[2].Should().Contain("third");
    }
}
