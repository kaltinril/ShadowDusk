using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Top-level <c>const</c> globals, including the comma-separated multi-declarator form
/// (<c>const float PI = 3.14159, TAU = 2.*PI;</c>). Each declarator must become its own const in source
/// order, and a later one may reference an earlier one. The parser previously swallowed the commas as the
/// sequence operator, registering only the first name and rejecting the rest with a misleading
/// "Undeclared identifier" error.
/// </summary>
public sealed class ConstGlobalTests
{
    [Fact]
    public void MultiDeclaratorConstGlobals_EachBecomesItsOwnConst_LaterReferencesEarlier()
    {
        const string glsl = """
            const float PI = 3.14159, TAU = 2.0 * PI, HALF_PI = 0.5 * PI;
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              float a = TAU * fragCoord.x + HALF_PI;
              fragColor = vec4(sin(a), 0.0, 0.0, 1.0);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeTrue(
            "all three const declarators are valid; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));

        // Each declarator emits its own const, in source order (PI before the consts that use it).
        r.Fx!.Should().Contain("static const float PI");
        r.Fx!.Should().Contain("static const float TAU");
        r.Fx!.Should().Contain("static const float HALF_PI");
        r.Fx!.IndexOf("float PI", StringComparison.Ordinal).Should().BeLessThan(
            r.Fx!.IndexOf("float TAU", StringComparison.Ordinal),
            "PI must be declared before TAU, which references it");

        // No "Undeclared identifier" misfire on the later declarators.
        r.Diagnostics.Should().NotContain(d => d.Message.Contains("Undeclared", StringComparison.Ordinal));
    }

    [Fact]
    public void SingleConstGlobal_StillConverts()
    {
        // A single-declarator const global (no comma) must be unaffected by the multi-declarator path.
        const string glsl = """
            const float K = 0.75;
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              fragColor = vec4(K, K, K, 1.0);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeTrue();
        r.Fx!.Should().Contain("static const float K = 0.75");
    }
}
