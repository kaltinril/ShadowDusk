using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// F1 (Phase 47) identifier-safety pass: GLSL allows a local to shadow a function it calls, and allows
/// identifiers that are HLSL reserved keywords; HLSL allows neither. The converter renames the offending
/// declaration (and its references) and warns, so a shader that is valid GLSL produces valid HLSL instead
/// of failing on generated HLSL with an opaque DXC error. These cases reproduce real third-party shaders
/// (mrange's "Let's self reflect" uses <c>mat3 rot = rot(...)</c>).
/// </summary>
public sealed class Phase47IdentifierSafetyTests
{
    private static ConvertResult Convert(string glsl) => ShaderToyConverter.Convert(glsl);

    [Fact]
    public void LocalShadowingCalledFunction_IsRenamed_CallStillResolves()
    {
        const string glsl = """
            mat3 rot(vec3 d, vec3 z) {
              vec3 v = cross(z, d);
              return mat3(v.x, v.y, v.z, d.x, d.y, d.z, z.x, z.y, z.z);
            }
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              vec2 p = fragCoord / iResolution.xy;
              mat3 rot = rot(normalize(vec3(p, 1.0)), normalize(vec3(1.0, p)));
              vec3 col = rot * vec3(p, 1.0);
              fragColor = vec4(col, 1.0);
            }
            """;

        ConvertResult r = Convert(glsl);

        r.Success.Should().BeTrue(Because(r));
        // The local was renamed...
        r.Fx!.Should().Contain("rot_sd", "the shadowing local 'rot' is renamed");
        // ...its value reference too (g_rot-style use)...
        r.Fx!.Should().Contain("mul(", "the 'rot * v' use is preserved (matrix-order trap), referencing the local");
        // ...but the FUNCTION decl + call head keep the original name so the call still resolves.
        r.Fx!.Should().Contain("rot(", "the call head and function decl stay bound to the 'rot' function");

        // A located warning explains the rename, pointing at the original GLSL.
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Construct == "rot" && d.Line > 0 && d.Column > 0 &&
            d.Message.Contains("shadows the function", StringComparison.Ordinal));
    }

    [Fact]
    public void ReservedKeywordLocals_AreRenamed_WithLocatedWarning()
    {
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              vec2 matrix = fragCoord / iResolution.xy;
              float sample = matrix.x + matrix.y;
              fragColor = vec4(matrix, sample, 1.0);
            }
            """;

        ConvertResult r = Convert(glsl);

        r.Success.Should().BeTrue(Because(r));
        r.Fx!.Should().Contain("matrix_sd").And.Contain("sample_sd");
        // The bare reserved words must not survive as HLSL identifiers (a declarator or use).
        r.Fx!.Should().NotContain("float2 matrix ").And.NotContain("float sample ");

        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Construct == "matrix" &&
            d.Line > 0 && d.Message.Contains("reserved word", StringComparison.Ordinal));
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Construct == "sample");
    }

    [Fact]
    public void ReservedKeywordFunction_IsRenamed_AtDeclAndEveryCall()
    {
        const string glsl = """
            float linear(float t) { return t; }
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              vec2 p = fragCoord / iResolution.xy;
              fragColor = vec4(linear(p.x), linear(p.y), 0.0, 1.0);
            }
            """;

        ConvertResult r = Convert(glsl);

        r.Success.Should().BeTrue(Because(r));
        r.Fx!.Should().Contain("linear_sd(", "the function decl and every call use the safe name");
        r.Fx!.Should().NotContain("linear(", "the reserved 'linear' name must not survive as a callable");

        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Construct == "linear" && d.Line > 0);
    }

    [Fact]
    public void LocalNamedLikeUncalledFunction_IsNotRenamed_NoWarning()
    {
        // No false positive: 'helper' is a function, and a local is named 'helper', but the local's
        // function never CALLS helper(), so HLSL is happy with the shadow and nothing is renamed.
        const string glsl = """
            float helper(float t) { return t * 2.0; }
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              float helper = fragCoord.x / iResolution.x;
              fragColor = vec4(helper, helper, helper, 1.0);
            }
            """;

        ConvertResult r = Convert(glsl);

        r.Success.Should().BeTrue(Because(r));
        r.Fx!.Should().NotContain("helper_sd", "an uncalled shadow does not break HLSL, so no rename");
        r.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Warning && d.Construct == "helper");
    }

    [Fact]
    public void CleanShader_HasNoRenameWarnings()
    {
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              vec2 uv = fragCoord / iResolution.xy;
              fragColor = vec4(uv, 0.0, 1.0);
            }
            """;

        ConvertResult r = Convert(glsl);

        r.Success.Should().BeTrue(Because(r));
        r.Diagnostics.Should().BeEmpty("a shader with no collisions triggers no identifier-safety renames");
    }

    private static string Because(ConvertResult r) =>
        "conversion must succeed; diagnostics: " +
        string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"));
}
