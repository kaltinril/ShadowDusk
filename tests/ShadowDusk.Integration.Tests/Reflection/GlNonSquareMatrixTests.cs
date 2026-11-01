#nullable enable
using System.Text;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// Issue #453 and the MonoGame 3.8.6 DesktopGL4 gap: valid HLSL that truncates a matrix
/// (<c>mul(float3, float4x4)</c>, a <c>(float3x3)</c> cast) made DXC emit a non-square
/// <c>mat3x4</c> local, which the versionless GL dialect cannot declare. Mesa refused KNI's
/// <c>DeferredBasicEffect</c> at Effect load ("syntax error, unexpected NEW_IDENTIFIER") and
/// DesktopGL4 aborted the process; the compile warned SD0403. Rule 17 holds every non-square matrix
/// as a zero-padded square <c>matN</c>, so neither stage carries one and nothing warns. The
/// real-engine renders are <c>validation/KniEffectTestGl</c> / <c>KniEffectTestKniGl</c>
/// (<c>DeferredBasicEffect/bind</c>) and <c>validation/VertexSemanticsGl</c> on DesktopGL4.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class GlNonSquareMatrixTests
{
    // mul(float3, float4x4) truncates World to its upper 3x4 (a mat3x4 local in SPIRV-Cross's GLSL);
    // the (float3x3) cast, a (float4x3) matrix-times-vector and a product of two truncations are the
    // other shapes. (A non-square UNIFORM is refused upstream with SD0210; these are all locals.)
    private const string Truncations = """
float4x4 World;
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; float3 N : TEXCOORD0; float3 T : TEXCOORD1; float3 B : TEXCOORD2; };
VSOut VS(VSIn i)
{
    VSOut o;
    o.Position = mul(i.Position, World);
    o.N = mul(i.Normal, World);
    o.T = mul(i.Tangent, (float3x3)World);
    o.B = mul((float4x3)World, i.Normal).xyz;
    float3x3 M = mul((float3x4)World, (float4x3)World);
    o.B += mul(i.Tangent, M);
    return o;
}
float4 PS(VSOut i) : COLOR0 { return float4(normalize(i.N) * 0.5 + i.T * 0.25 + i.B * 0.125, 1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
""";

    [Theory]
    [InlineData(EffectContainer.Mgfx)]
    [InlineData(EffectContainer.Knifx)]
    public async Task KniDeferredBasicEffect_HasNoNonSquareMatrix(EffectContainer container)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", "third-party", "KNI", "DeferredBasicEffect.fx");
        byte[] data = await CompileAsync(await File.ReadAllTextAsync(path), "DeferredBasicEffect.fx", container);

        if (container == EffectContainer.Mgfx)
            AssertNoNonSquareGlsl(data);
    }

    [Fact]
    public async Task TruncatingMultiplies_HaveNoNonSquareMatrix()
    {
        byte[] data = await CompileAsync(Truncations, "Truncations.fx", EffectContainer.Mgfx);

        string vs = AssertNoNonSquareGlsl(data);
        // The truncation local is held as a padded mat4, and the float3 operand padded to vec4.
        vs.ShouldContain("vec4(0.0))", Case.Sensitive);
        vs.ShouldContain(", 0.0))", Case.Sensitive);
    }

    private static string AssertNoNonSquareGlsl(byte[] data)
    {
        var reader = MgfxBlobReader.Parse(data);
        foreach (var shader in reader.Shaders)
            Encoding.ASCII.GetString(shader.Bytecode).ShouldNotMatch(@"\bmat[234]x[234]\b");
        return Encoding.ASCII.GetString(reader.Shaders.First(s => s.IsVertex).Bytecode);
    }

    private static async Task<byte[]> CompileAsync(string source, string name, EffectContainer container)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(
            source,
            new CompilerOptions { Target = PlatformTarget.OpenGL, Container = container, SourceFileName = name },
            cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : "");
        // SD0403 was the registered warning for a surviving non-square type; it must not fire.
        result.Value.Warnings.ShouldNotContain(w => w.Code == "SD0403");
        return result.Value.Data;
    }
}
