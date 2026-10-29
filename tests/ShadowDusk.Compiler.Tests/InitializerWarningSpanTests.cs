#nullable enable

using Shouldly;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.HLSL;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// DXC's "initializer ignored" warning is dropped for a kept default when its location lies in the
/// declaration's span, from column 1 of the declared name's line (the name itself when another
/// declaration precedes it on that line) to the end of the initializer. DXC's SPIR-V path
/// reports the initializer's first token and its DXIL path the name (measured, DXC 1.7.2212.40),
/// so comparing only the name's line left the warning behind for `float4 C =` / `{ 1, 0, 0, 1 };`.
/// </summary>
public sealed class InitializerWarningSpanTests
{
    // `float4 C =` on line 3 (name at column 8), `{ 1, 0, 0, 1 };` on line 4 (closing brace at 14).
    private static GlobalInitializerDefault Def(string file = "Effect.fx") =>
        new("C", 1, 3, 1, 4, 0, [new byte[16]], file, 3, 8) { EndLine = 4, EndColumn = 14 };

    private static ShaderError Warning(int line, int column, string file = "C:/src/Effect.fx") =>
        new(file, line, column, "X0000", "variable 'C' will be placed in $Globals so initializer ignored",
            ShaderErrorSeverity.Warning);

    [Theory]
    [InlineData(4, 1)]   // SPIR-V path: the initializer's first token, on the next line
    [InlineData(3, 8)]   // DXIL path: the declared name
    [InlineData(4, 14)]  // the closing brace, the span's last character
    public void InsideTheSpan_Matches(int line, int column) =>
        CompilationPipeline.InitializerSpanContains(Def(), Warning(line, column)).ShouldBeTrue();

    [Theory]
    [InlineData(3, 1)]   // the type, left of the name: a macro-declared name is reported there (DXIL)
    [InlineData(3, 6)]
    public void LeftOfTheNameOnItsLine_Matches_WhenNothingPrecedesIt(int line, int column) =>
        CompilationPipeline.InitializerSpanContains(Def(), Warning(line, column)).ShouldBeTrue();

    [Fact]
    public void LeftOfTheName_DoesNotMatch_WhenTheSpanStartsAtTheName() =>
        CompilationPipeline.InitializerSpanContains(Def(), Warning(3, 7), startColumn: 8).ShouldBeFalse();

    [Theory]
    [InlineData(4, 15)]  // after the initializer (a second declaration on the same line)
    [InlineData(2, 8)]
    [InlineData(5, 1)]
    public void OutsideTheSpan_DoesNotMatch(int line, int column) =>
        CompilationPipeline.InitializerSpanContains(Def(), Warning(line, column)).ShouldBeFalse();

    [Fact]
    public void AnotherFile_DoesNotMatch() =>
        CompilationPipeline.InitializerSpanContains(Def(), Warning(4, 1, "C:/src/Other.fxh")).ShouldBeFalse();

    [Fact]
    public void ADiagnosticWithoutAColumn_IsPlacedByItsLine()
    {
        CompilationPipeline.InitializerSpanContains(Def(), Warning(4, 0)).ShouldBeTrue();
        CompilationPipeline.InitializerSpanContains(Def(), Warning(5, 0)).ShouldBeFalse();
    }

    [Fact]
    public void AnUnknownEnd_IsTheNameAlone()
    {
        GlobalInitializerDefault nameOnly = Def() with { EndLine = 0, EndColumn = 0 };

        CompilationPipeline.InitializerSpanContains(nameOnly, Warning(3, 8)).ShouldBeTrue();
        CompilationPipeline.InitializerSpanContains(nameOnly, Warning(4, 1)).ShouldBeFalse();
    }
}
