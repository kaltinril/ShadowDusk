using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Legacy HLSL for-scope leak (Phase 47 follow-up). GLSL scopes a for-loop's variable to its own loop, so
/// a function may reuse the same name across sibling/nested loops. HLSL instead leaks the for-init into the
/// enclosing scope, so DXC rejects the reuse as a <c>-Wfor-redefinition</c> error (under <c>-WX</c>),
/// regardless of whether the types match. The converter keeps the first loop's variable and renames the
/// later ones (each scoped to its own loop), emitting a located Warning per rename. A function that never
/// reuses a loop variable is left untouched.
/// </summary>
public sealed class ForLoopScopingTests
{
    [Fact]
    public void ReusedForVar_DifferentTypes_RenamesLaterLoops_AndWarns()
    {
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              float s = 0.0;
              for (int i = 0; i < 3; i++) { s += float(i); }
              for (float i = 0.0; i < 3.0; i += 1.0) { s += i; }
              fragColor = vec4(s, s, s, 1.0);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeTrue(
            "the shader is valid GLSL; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));

        // First loop keeps `i`; the second loop is renamed so the two no longer collide under HLSL scoping.
        r.Fx!.Should().Contain("int i = 0", "the first loop keeps the original name");
        r.Fx!.Should().Contain("i_sd", "the second loop's variable is renamed to avoid the redefinition");
        r.Fx!.Should().NotContain("float i = 0.0", "the float loop's `i` must have been renamed");

        // Exactly one located Warning, attributed to the second loop's line.
        ConvertDiagnostic warn = r.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("for-loop variable", StringComparison.Ordinal)).Subject;
        warn.Line.Should().Be(4, "the rename is reported at the second loop, not the first");
    }

    [Fact]
    public void ReusedForVar_SameType_IsAlsoRenamed()
    {
        // DXC's -Wfor-redefinition fires even when the reused loop variable has the SAME type, so same-type
        // reuse must be renamed too (not only the different-type case).
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              float s = 0.0;
              for (int i = 0; i < 3; i++) { s += float(i); }
              for (int i = 0; i < 5; i++) { s += float(i) * 2.0; }
              fragColor = vec4(s, s, s, 1.0);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeTrue();
        r.Fx!.Should().Contain("i_sd", "same-type reuse is renamed because -Wfor-redefinition still fires");
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("for-loop variable", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedLoops_ReusingName_RenameTheInnerLoop()
    {
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              float s = 0.0;
              for (int i = 0; i < 3; i++) {
                for (int i = 0; i < 3; i++) { s += float(i); }
              }
              fragColor = vec4(s, s, s, 1.0);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeTrue();
        r.Fx!.Should().Contain("i_sd", "the inner loop reuses the outer loop's name and must be renamed");
        // The inner loop's body must reference the renamed variable, not the outer `i`.
        r.Fx!.Should().Contain("float(i_sd)");
    }

    [Fact]
    public void NoReuse_LeavesLoopVariablesUnchanged_AndDoesNotWarn()
    {
        // Distinct loop variable names (i, j) never collide, so nothing is renamed and no warning fires:
        // the converter must not touch a shader that already compiles.
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              float s = 0.0;
              for (int i = 0; i < 3; i++) { s += float(i); }
              for (int j = 0; j < 3; j++) { s += float(j); }
              fragColor = vec4(s, s, s, 1.0);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeTrue();
        r.Fx!.Should().NotContain("_sd", "no collision means no rename");
        r.Diagnostics.Should().NotContain(d => d.Message.Contains("for-loop variable", StringComparison.Ordinal));
    }

    [Fact]
    public void SameName_InDifferentFunctions_IsNotRenamed()
    {
        // The collision is per-FUNCTION (a for-init leaks only within its own function), so two different
        // functions may each use `i` for their first loop without renaming. This guards that the "already
        // seen" set is reset per function rather than shared across the whole shader.
        const string glsl = """
            float fa() { float s = 0.0; for (int i = 0; i < 3; i++) { s += float(i); } return s; }
            float fb() { float s = 0.0; for (int i = 0; i < 3; i++) { s += float(i) * 2.0; } return s; }
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              fragColor = vec4(fa() + fb());
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeTrue();
        r.Fx!.Should().NotContain("_sd", "each function's first loop keeps `i`; there is no cross-function collision");
        r.Diagnostics.Should().NotContain(d => d.Message.Contains("for-loop variable", StringComparison.Ordinal));
    }
}
