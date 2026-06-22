using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Matrix-constructor shapes beyond the scalar-list form. GLSL builds a matrix from a single scalar
/// (diagonal), another matrix (submatrix + identity completion), OR a sequence of vectors/scalars whose
/// components flatten (column-major) to exactly NxN. The flattened-vector form (e.g.
/// <c>mat2(vec4)</c> = the code-golf rotation <c>mat2(cos(a + vec4(0,33,11,0)))</c>) was previously a
/// loud reject; it is now passed straight through to HLSL's <c>floatNxN(...)</c>, which flattens the
/// vector the same way (consistent with the matrix-order trap).
/// </summary>
public sealed class MatrixConstructorTests
{
    [Fact]
    public void Mat2FromVec4_IsAccepted_AndFlattenedToFloat2x2()
    {
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              vec2 uv = fragCoord / iResolution.xy;
              float a = iTime;
              mat2 m = mat2(cos(a + vec4(0.0, 33.0, 11.0, 0.0)));
              fragColor = vec4(m * uv, 0.0, 1.0);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeTrue(
            "mat2(vec4) is a valid GLSL matrix constructor; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));
        r.Fx!.Should().Contain("float2x2(", "the vector is passed through to the HLSL matrix constructor");
        // The reject message for an unsupported single-arg matrix constructor must NOT appear.
        r.Diagnostics.Should().NotContain(d => d.Message.Contains("Single-argument matrix constructor", StringComparison.Ordinal));
    }

    [Fact]
    public void Mat3FromVec4_WrongWidth_RejectsLoudly()
    {
        // A single vector that does NOT supply exactly NxN components is not a valid matrix constructor.
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              mat3 m = mat3(vec4(1.0, 2.0, 3.0, 4.0));
              fragColor = vec4(m[0], 1.0);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeFalse();
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Line > 0 &&
            d.Message.Contains("components", StringComparison.Ordinal));
    }
}
