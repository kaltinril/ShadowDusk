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
    public void Format_PathStrippedToFilename_OnlyBasenameInOutput()
    {
        var error = new ShaderError(
            File: "/abs/path/to/Foo.fx",
            Line: 1,
            Column: 1,
            Code: "X0001",
            Message: "m");

        var formatted = MgcbErrorFormatter.Format(error);

        // The filename segment must be just "Foo.fx", not the full absolute path
        formatted.Should().StartWith("Foo.fx(");
        formatted.Should().NotContain("/abs/path/to/");
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
