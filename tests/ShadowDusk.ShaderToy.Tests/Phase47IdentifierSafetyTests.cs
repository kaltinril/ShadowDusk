using Shouldly;
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

        r.Success.ShouldBeTrue(Because(r));
        // The local was renamed...
        r.Fx!.ShouldContain("rot_sd", Case.Sensitive, "the shadowing local 'rot' is renamed");
        // ...its value reference too (g_rot-style use)...
        r.Fx!.ShouldContain("mul(", Case.Sensitive, "the 'rot * v' use is preserved (matrix-order trap), referencing the local");
        // ...but the FUNCTION decl + call head keep the original name so the call still resolves.
        r.Fx!.ShouldContain("rot(", Case.Sensitive, "the call head and function decl stay bound to the 'rot' function");

        // A located warning explains the rename, pointing at the original GLSL.
        r.Diagnostics.ShouldContain(d =>
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

        r.Success.ShouldBeTrue(Because(r));
        r.Fx!.ShouldContain("matrix_sd", Case.Sensitive);
        r.Fx!.ShouldContain("sample_sd", Case.Sensitive);
        // The bare reserved words must not survive as HLSL identifiers (a declarator or use).
        r.Fx!.ShouldNotContain("float2 matrix ", Case.Sensitive);
        r.Fx!.ShouldNotContain("float sample ");

        r.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Construct == "matrix" &&
            d.Line > 0 && d.Message.Contains("reserved word", StringComparison.Ordinal));
        r.Diagnostics.ShouldContain(d =>
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

        r.Success.ShouldBeTrue(Because(r));
        r.Fx!.ShouldContain("linear_sd(", Case.Sensitive, "the function decl and every call use the safe name");
        r.Fx!.ShouldNotContain("linear(", Case.Sensitive, "the reserved 'linear' name must not survive as a callable");

        r.Diagnostics.ShouldContain(d =>
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

        r.Success.ShouldBeTrue(Because(r));
        r.Fx!.ShouldNotContain("helper_sd", Case.Sensitive, "an uncalled shadow does not break HLSL, so no rename");
        r.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Warning && d.Construct == "helper");
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

        r.Success.ShouldBeTrue(Because(r));
        r.Diagnostics.ShouldBeEmpty("a shader with no collisions triggers no identifier-safety renames");
    }

    // ── converter-introduced names (intrinsic rename targets + harness symbols, bug-hunt N20) ──

    [Fact]
    public void LocalNamedFrac_IsRenamed_SoTheEmittedFracIntrinsicStillResolves()
    {
        // `float frac = fract(x);` is valid GLSL (frac is not a GLSL builtin), but the converter
        // emits fract() as HLSL frac(), which the local would capture ("call the variable").
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              float frac = fract(fragCoord.x);
              fragColor = vec4(frac, frac, frac, 1.0);
            }
            """;

        ConvertResult r = Convert(glsl);

        r.Success.ShouldBeTrue(Because(r));
        r.Fx!.ShouldContain("float frac_sd = frac(", Case.Sensitive, "the local is renamed; the intrinsic call is not");
        r.Fx!.ShouldNotContain("float frac =", Case.Sensitive, "the bare local name would shadow the frac intrinsic");

        r.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Construct == "frac" &&
            d.Line > 0 && d.Message.Contains("collides", StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionNamedLerp_IsRenamed_WhileTheMixIntrinsicKeepsEmittingLerp()
    {
        const string glsl = """
            float lerp(float t) { return t * 2.0; }
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              float a = lerp(0.25);
              float b = mix(0.0, 1.0, a);
              fragColor = vec4(a, b, 0.0, 1.0);
            }
            """;

        ConvertResult r = Convert(glsl);

        r.Success.ShouldBeTrue(Because(r));
        r.Fx!.ShouldContain("float lerp_sd(float t)", Case.Sensitive, "the user function is renamed at its declaration");
        r.Fx!.ShouldContain("lerp_sd(0.25)", Case.Sensitive, "calls to the user function follow the rename");
        r.Fx!.ShouldContain("lerp(0.0, 1.0, a)", Case.Sensitive, "mix() still emits the real lerp intrinsic");
    }

    [Fact]
    public void LocalNamedGlslMod_IsRenamed_SoTheEmittedHelperStillResolves()
    {
        // The mod() rewrite emits calls to the generated glsl_mod helper; a local of that name
        // in the same function would capture them.
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              float glsl_mod = mod(fragCoord.x, 4.0);
              fragColor = vec4(glsl_mod, 0.0, 0.0, 1.0);
            }
            """;

        ConvertResult r = Convert(glsl);

        r.Success.ShouldBeTrue(Because(r));
        r.Fx!.ShouldContain("float glsl_mod_sd = glsl_mod(", Case.Sensitive);
        r.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Construct == "glsl_mod");
    }

    [Fact]
    public void GlobalNamedPSMain_IsRenamed_AwayFromTheHarnessEntry()
    {
        const string glsl = """
            float PSMain = 0.5;
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              fragColor = vec4(PSMain, PSMain, PSMain, 1.0);
            }
            """;

        ConvertResult r = Convert(glsl);

        r.Success.ShouldBeTrue(Because(r));
        r.Fx!.ShouldContain("static float PSMain_sd", Case.Sensitive, "the user global must not collide with the harness PS");
        r.Fx!.ShouldContain("float4 PSMain(VSOutput input)", Case.Sensitive, "the harness entry keeps its name");
        r.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Construct == "PSMain");
    }

    private static string Because(ConvertResult r) =>
        "conversion must succeed; diagnostics: " +
        string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"));
}
