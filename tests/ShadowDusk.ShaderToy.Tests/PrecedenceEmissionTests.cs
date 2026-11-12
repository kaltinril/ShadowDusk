using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// The emitter writes only the parentheses that precedence, associativity, or a reader needs. Each case
/// converts one GLSL expression and asserts the exact HLSL spelling of the declaration that holds it, so
/// a dropped paren that changes meaning and a redundant paren that comes back both fail.
/// </summary>
public sealed class PrecedenceEmissionTests
{
    private static string ConvertDecl(string glslType, string glslExpr)
    {
        string glsl = $$"""
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float a = fragCoord.x;
            float b = fragCoord.y;
            float c = 0.25;
            float d = 0.75;
            int i = 3;
            int j = 5;
            int k = 6;
            bool p = a > 0.5;
            bool q = b > 0.5;
            bool s = c > 0.5;
            vec2 v = fragCoord;
            vec2 w = vec2(1.0, 2.0);
            mat2 m = mat2(1.0, 2.0, 3.0, 4.0);
            {{glslType}} r = {{glslExpr}};
            fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult result = ShaderToyConverter.Convert(glsl);
        result.Success.ShouldBeTrue(string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        return result.Fx!;
    }

    [Theory]
    // Rule 1, precedence: a looser child keeps its parens.
    [InlineData("float", "(a + b) * c", "float r = (a + b) * c;")]
    [InlineData("float", "a * (b + c)", "float r = a * (b + c);")]
    [InlineData("float", "-(a + b)", "float r = -(a + b);")]
    [InlineData("float", "(a + b) / (c - d)", "float r = (a + b) / (c - d);")]
    [InlineData("float", "(p ? a : b) + c", "float r = (p ? a : b) + c;")]
    [InlineData("bool", "(p || q) && s", "bool r = (p || q) && s;")]
    [InlineData("float", "(v + w).x", "float r = (v + w).x;")]
    // Rule 2, associativity: the right operand at the same level keeps its parens.
    [InlineData("float", "a - (b - c)", "float r = a - (b - c);")]
    [InlineData("float", "a / (b * c)", "float r = a / (b * c);")]
    [InlineData("float", "a + (b + c)", "float r = a + (b + c);")]
    [InlineData("float", "a * (b / c)", "float r = a * (b / c);")]
    [InlineData("bool", "p && (q && s)", "bool r = p && (q && s);")]
    // Rule 3, clarity: mixed logical, bitwise, shift and comparison chains.
    [InlineData("bool", "p || (q && s)", "bool r = p || (q && s);")]
    [InlineData("bool", "(p && q) || s", "bool r = (p && q) || s;")]
    [InlineData("int", "i | (j & k)", "int r = i | (j & k);")]
    [InlineData("int", "(i ^ j) | k", "int r = (i ^ j) | k;")]
    [InlineData("int", "i & (j + k)", "int r = i & (j + k);")]
    [InlineData("bool", "(i & j) == k", "bool r = (i & j) == k;")]
    [InlineData("int", "i << (j + k)", "int r = i << (j + k);")]
    [InlineData("int", "(i << j) + k", "int r = (i << j) + k;")]
    [InlineData("bool", "(i << j) < k", "bool r = (i << j) < k;")]
    [InlineData("bool", "(a < b) == p", "bool r = (a < b) == p;")]
    // Rule 4: a conditional nested in a conditional keeps its parens.
    [InlineData("float", "p ? (q ? a : b) : c", "float r = p ? (q ? a : b) : c;")]
    [InlineData("float", "p ? a : (q ? b : c)", "float r = p ? a : (q ? b : c);")]
    // Unary and cast operands.
    [InlineData("float", "-(-a)", "float r = -(-a);")]
    [InlineData("vec3", "vec3(a + b)", "float3 r = (float3)(a + b);")]
    [InlineData("vec3", "vec3(-a)", "float3 r = (float3)(-a);")]
    [InlineData("vec2", "vec3(a).xy", "float2 r = ((float3)a).xy;")]
    public void RequiredParentheses_AreKept(string glslType, string glslExpr, string expectedHlsl) =>
        ConvertDecl(glslType, glslExpr).ShouldContain(expectedHlsl, Case.Sensitive);

    [Theory]
    [InlineData("float", "a + b * c", "float r = a + b * c;")]
    [InlineData("float", "a * b + c", "float r = a * b + c;")]
    [InlineData("float", "a - b - c", "float r = a - b - c;")]
    [InlineData("float", "a * b / c", "float r = a * b / c;")]
    [InlineData("float", "-a * b", "float r = -a * b;")]
    [InlineData("float", "a + (b * c)", "float r = a + b * c;")]
    [InlineData("float", "(a + b) + c", "float r = a + b + c;")]
    [InlineData("float", "((a))", "float r = a;")]
    [InlineData("bool", "p && q && s", "bool r = p && q && s;")]
    [InlineData("bool", "a > 0.0 && b < 1.0", "bool r = a > 0.0 && b < 1.0;")]
    [InlineData("int", "i | j | k", "int r = i | j | k;")]
    [InlineData("float", "p ? a + b : c", "float r = p ? a + b : c;")]
    [InlineData("float", "sin(a + b)", "float r = sin(a + b);")]
    [InlineData("vec3", "vec3(a)", "float3 r = (float3)a;")]
    [InlineData("vec2", "m * (v + w)", "float2 r = mul(v + w, m);")]
    [InlineData("float", "(m * v).x", "float r = mul(v, m).x;")]
    public void RedundantParentheses_AreDropped(string glslType, string glslExpr, string expectedHlsl) =>
        ConvertDecl(glslType, glslExpr).ShouldContain(expectedHlsl, Case.Sensitive);

    [Fact]
    public void Condition_HasNoRedundantParentheses()
    {
        string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float a = fragCoord.x;
            float b = fragCoord.y;
            if ((a > 0.0) && (b > 0.0) || !(a < b))
            {
                a = 1.0;
            }
            fragColor = vec4(a, 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult result = ShaderToyConverter.Convert(glsl);
        result.Success.ShouldBeTrue();
        result.Fx!.ShouldContain("if ((a > 0.0 && b > 0.0) || !(a < b))", Case.Sensitive);
    }

    [Fact]
    public void MatrixCompMult_OfBinaryOperands_KeepsTheirParentheses()
    {
        string fx = ConvertDecl("mat2", "matrixCompMult(m + m, m)");
        fx.ShouldContain("float2x2 r = (m + m) * m;", Case.Sensitive);
    }
}
