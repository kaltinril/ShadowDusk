#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #202: a diagnostic from the FNA (<c>fx_2_0</c>, vkd3d SM ≤ 3) path must name the
/// author's file, line, and column. vkd3d 1.17's own coordinates drift three ways (a
/// prelude ShadowDusk prepends and then blanks the <c>#line</c> for, skipped <c>#if</c> arms
/// that vanish from its count, and intrinsic templates it lexes against the user's counter:
/// +20 per <c>atan2</c>), so the reporter's 3009 came out as 3586. These tests pin the
/// location on the real fixtures, by asserting the construct that actually sits on the
/// reported line — every earlier FNA test asserted only the file and the message
/// (<c>plan/DONE/ISSUE-202-fna-error-line-numbers.md</c> §7).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "FNA")]
public sealed class FnaDiagnosticLocationTests
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(60);

    private static string IssueFixturePath(int issue, string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "issues", issue.ToString(), fileName);

    private static async Task<ShaderError> CompileForFnaExpectingOneLocatedErrorAsync(string path, CancellationToken ct)
    {
        string source = await File.ReadAllTextAsync(path, ct);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.Fna,
            SourceFileName = path,
            AdditionalIncludePaths = [Path.GetDirectoryName(path)!],
        }, ct);

        result.IsFailure.ShouldBeTrue($"'{Path.GetFileName(path)}' is a documented vkd3d 1.17 SM <= 3 rejection");
        ShaderError error = result.Error.Single(e => e.Severity == ShaderErrorSeverity.Error);
        error.File.ShouldBe(path, customMessage: "the path exactly as the compiler was given it");
        return error;
    }

    private static async Task AssertLandsOnAsync(string path, string constructOnThatLine, string vkd3dMessage, int? column, CancellationToken ct)
    {
        ShaderError error = await CompileForFnaExpectingOneLocatedErrorAsync(path, ct);
        string[] lines = (await File.ReadAllTextAsync(path, ct)).Split('\n');

        error.Code.ShouldBe("E5017");
        error.Message.ShouldContain(vkd3dMessage, Case.Sensitive, "vkd3d's own message, verbatim");
        error.Line.ShouldBeInRange(1, lines.Length, "the line must exist in the file (the reporter's did not)");
        lines[error.Line - 1].ShouldContain(constructOnThatLine, Case.Sensitive,
            $"line {error.Line} must be the construct vkd3d rejected, not a line {error.Line - lines.Length} past the end");
        if (column is { } c)
            error.Column.ShouldBe(c);
    }

    // The reporter's exact file (3235 upstream lines + a 17-line provenance header):
    // vkd3d itself says 3590; the construct is upstream line 3009 = fixture line 3026,
    // and vkd3d's column 26 (its re-spaced text) is the author's column 29, the '<='.
    [FnaFact]
    public async Task Issue202_AposShapesCurrentUpstream_LandsOnTheBoolTernary()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        string path = IssueFixturePath(202, "apos-shapes.fx");
        string[] lines = (await File.ReadAllTextAsync(path, cts.Token)).Split('\n');
        lines.Length.ShouldBeGreaterThan(3200, "this must be the current upstream, not the 523-line vendored one");

        await AssertLandsOnAsync(path,
            "if (isGlyph ? glyphFade <= 0.0 : d >= aaSize * (1.0 - aaBias))",
            "SM1 cmp expression of type int", column: 29, cts.Token);

        ShaderError error = await CompileForFnaExpectingOneLocatedErrorAsync(path, cts.Token);
        error.Line.ShouldBe(3026);
    }

    // The vendored 523-line upstream fails on a different vkd3d 1.17 gap: a 'for' whose trip
    // count is a runtime int (EllipseSDF's Newton iteration). vkd3d reports 205; it is 204.
    [FnaFact]
    public async Task VendoredAposShapes_LandsOnTheRuntimeTripCountLoop()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        await AssertLandsOnAsync(TestHelpers.FixturePath("third-party/Apos.Shapes/apos-shapes.fx"),
            "for (i = 0; i < newton_steps; i++)", "Instruction type HLSL_IR_LOOP", column: 5, cts.Token);
    }

    // The two small corpus members of the same class were off by the prelude minus their
    // skipped '#if OPENGL' arm (+3 and +2): small enough to read as right, never asserted.
    [FnaTheory]
    [InlineData("DeferredSprite.fx", "clip( ( output.color.a < _alphaCutoff ) ? -1 : 1 );")]
    [InlineData("ForwardLighting.fx", "clip( ( color.a < 0.2 ) ? -1 : 1 );")]
    public async Task IntTernaryClipFixtures_LandOnTheClipLine(string fx, string construct)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        await AssertLandsOnAsync(TestHelpers.FixturePath(fx), construct, "SM1 cmp expression of type int", column: null, cts.Token);
    }
}
