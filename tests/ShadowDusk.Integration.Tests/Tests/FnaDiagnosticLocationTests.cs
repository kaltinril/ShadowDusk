#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #202: a diagnostic from the FNA (<c>fx_2_0</c>, vkd3d SM ≤ 3) path must name the
/// author's file, line, and column. vkd3d's own coordinates drift three ways (a prelude
/// ShadowDusk prepends and then blanks the <c>#line</c> for, skipped <c>#if</c> arms that
/// vanish from its count, and intrinsic templates it lexes against the user's counter:
/// +20 per <c>atan2</c>), so the reporter's 3009 came out as 3586. These tests pin the
/// location on the real fixtures, by asserting the construct that actually sits on the
/// reported line — every earlier FNA test asserted only the file and the message
/// (<c>plan/DONE/ISSUE-202-fna-error-line-numbers.md</c> §7).
/// <para>
/// The fixtures are chosen for the vkd3d gaps that are still open, so they move whenever
/// the pin does: vkd3d 2.1 closed the SM3 loop and int-ternary-clip gaps the 1.17-era
/// cases used, so the cases here are a runtime-indexed vector store and SM2/SM3 register
/// pressure. What is under test is the locator, never the particular gap.
/// </para>
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

        result.IsFailure.ShouldBeTrue($"'{Path.GetFileName(path)}' is a documented vkd3d SM <= 3 rejection");
        ShaderError error = result.Error.Single(e => e.Severity == ShaderErrorSeverity.Error);
        error.File.ShouldBe(path, customMessage: "the path exactly as the compiler was given it");
        return error;
    }

    private static async Task AssertLandsOnAsync(
        string path, string expectedCode, string constructOnThatLine, string vkd3dMessage, int? column, CancellationToken ct)
    {
        ShaderError error = await CompileForFnaExpectingOneLocatedErrorAsync(path, ct);
        string[] lines = (await File.ReadAllTextAsync(path, ct)).Split('\n');

        error.Code.ShouldBe(expectedCode);
        error.Message.ShouldContain(vkd3dMessage, Case.Sensitive, "vkd3d's own message, verbatim");
        error.Line.ShouldBeInRange(1, lines.Length, "the line must exist in the file (the reporter's did not)");
        lines[error.Line - 1].ShouldContain(constructOnThatLine, Case.Sensitive,
            $"line {error.Line} must be the construct vkd3d rejected, not a line {error.Line - lines.Length} past the end");
        if (column is { } c)
            error.Column.ShouldBe(c);
    }

    // The reporter's exact file (3235 upstream lines + a 17-line provenance header).
    // vkd3d 2.1 compiles further than 1.17 did and then runs out of SM3 temporaries;
    // it reports line 1132, the construct is fixture line 1000.
    [FnaFact]
    public async Task Issue202_AposShapesCurrentUpstream_LandsOnTheRegisterLimit()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        string path = IssueFixturePath(202, "apos-shapes.fx");
        string[] lines = (await File.ReadAllTextAsync(path, cts.Token)).Split('\n');
        lines.Length.ShouldBeGreaterThan(3200, "this must be the current upstream, not the 523-line vendored one");

        await AssertLandsOnAsync(path, "E9015",
            "pt = float2(b.x - r.y, b.y - r.y);",
            "Register r32 exceeds limits", column: 25, cts.Token);

        ShaderError error = await CompileForFnaExpectingOneLocatedErrorAsync(path, cts.Token);
        error.Line.ShouldBe(1000);
    }

    // A vector store through a runtime index: still unimplemented for SM <= 3 on 2.1.
    // vkd3d reports 137; the construct is 146.
    [FnaFact]
    public async Task NonConstantVectorStore_LandsOnTheIndexedAssignment()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        await AssertLandsOnAsync(TestHelpers.FixturePath("third-party/MonoGame/ParameterTypes.fx"), "E5017",
            "sampleDir[axis] = 1.0f;",
            "Non-constant vector addressing on store", column: 23, cts.Token);
    }

    // SM2 register pressure, the SD0305 class: vkd3d 2.1's register allocator gets
    // SkinnedEffect down to r12 but the vs_2_0 file is 12 registers, so it still fails
    // loudly. vkd3d reports 344; the construct is 64.
    [FnaFact]
    public async Task Sm2RegisterPressure_LandsOnTheSkinningAccumulation()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        await AssertLandsOnAsync(TestHelpers.FixturePath("SkinnedEffect.fx"), "E9015",
            "skinning += Bones[vin.Indices[i]] * vin.Weights[i];",
            "Register r12 exceeds limits", column: 26, cts.Token);
    }
}
