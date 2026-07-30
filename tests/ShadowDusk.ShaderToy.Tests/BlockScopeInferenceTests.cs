using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Brace-block scoping in the type-inference pass (bug-hunt N20). A nested block is a real scope in
/// both languages, so a block-local that shadows an outer variable must stop influencing inference
/// once the block closes — e.g. a <c>vec2</c> shadowed by a <c>mat2</c> inside a block must not turn
/// a later <c>*</c> on the outer <c>vec2</c> into a <c>mul()</c> matrix product.
/// </summary>
public sealed class BlockScopeInferenceTests
{
    private static string ConvertOk(string glsl)
    {
        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.ShouldBeTrue(string.Format(
            "the shader is in-subset; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
        r.Fx.ShouldNotBeNull();
        return r.Fx!;
    }

    [Fact]
    public void BlockShadowedVariable_DoesNotPoisonInferenceAfterTheBlock()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 p = fragCoord / iResolution.xy;
            {
                mat2 p = mat2(0.0, 1.0, 1.0, 0.0);
                vec2 t = p * fragCoord;
                fragColor = vec4(t, 0.0, 1.0);
            }
            vec2 q = p * vec2(2.0, 3.0);
            fragColor += vec4(q, 0.0, 0.0);
        }
        """;

        string fx = ConvertOk(glsl);

        // Inside the block, `p` is the mat2: the matrix-order trap applies (GLSL M*v -> mul(v, M)).
        fx.ShouldContain("mul(fragCoord, p)", Case.Sensitive, "inside the block p is the shadowing mat2");

        // After the block, `p` is the OUTER vec2 again: componentwise multiply, NOT mul().
        fx.ShouldContain("float2 q = (p * float2(2.0, 3.0));", Case.Sensitive, "after the block the outer vec2 declaration wins again");
        fx.ShouldNotContain("mul(float2(2.0, 3.0), p)", Case.Sensitive, "the block-local mat2 must not leak into inference after the block");
    }

    [Fact]
    public void IfBlockShadow_AlsoScopedToItsBlock()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 v = fragCoord;
            if (fragCoord.x > 100.0)
            {
                mat2 v = mat2(1.0, 0.0, 0.0, 1.0);
                fragColor = vec4(v * fragCoord, 0.0, 1.0);
            }
            vec2 w = v * vec2(0.5, 0.5);
            fragColor = vec4(w, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("float2 w = (v * float2(0.5, 0.5));", Case.Sensitive, "the if-block mat2 shadow must not survive past its block");
    }
}
