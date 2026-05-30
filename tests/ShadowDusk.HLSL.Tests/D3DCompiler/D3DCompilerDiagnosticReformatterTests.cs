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
}
