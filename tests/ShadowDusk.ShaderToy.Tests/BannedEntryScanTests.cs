using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// The up-front banned-entry-point scan (<c>mainSound</c>/<c>mainVR</c>/<c>mainCubemap</c>) runs on
/// the raw pre-preprocess text — but with comments blanked out (bug-hunt N20): a token that only
/// appears in a <c>//</c> or <c>/* … */</c> comment must not hard-fail an otherwise-convertible
/// shader, while a REAL banned entry still rejects with a located error pointing at the original
/// source.
/// </summary>
public sealed class BannedEntryScanTests
{
    [Fact]
    public void MainSound_InLineComment_DoesNotReject()
    {
        const string glsl = """
        // removed the old mainSound experiment
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.Should().BeTrue(
            "a banned token inside a comment is not a banned entry point; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));
    }

    [Fact]
    public void MainVrAndMainCubemap_InBlockComment_DoNotReject()
    {
        const string glsl = """
        /* this shader once shipped mainVR and
           mainCubemap variants; both are gone */
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.Should().BeTrue(
            "banned tokens inside a block comment are not banned entry points; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));
    }

    [Fact]
    public void RealMainSound_StillRejects_WithLocatedError()
    {
        const string glsl = """
        // audio experiment kept below
        vec2 mainSound(int samp, float time)
        {
            return vec2(0.0);
        }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeFalse("a real mainSound definition is still a banned entry point");
        ConvertDiagnostic error = r.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Error).Subject;
        error.Construct.Should().Be("mainSound");
        // Blanking comments preserves positions: the error points at the REAL definition (line 2),
        // not the comment mention on line 1.
        error.Line.Should().Be(2);
        error.Column.Should().Be(6);
    }
}
