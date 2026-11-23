#nullable enable
using System.Text;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// Issue #524: an OpenGL stage using <c>SV_VertexID</c>, <c>uint</c> or an integer operator carries
/// Rule 19's guarded <c>GL_EXT_gpu_shader4</c> prelude through the whole pipeline, and a stage that
/// does not stays free of it. The real-engine renders are <c>validation/VertexSemanticsGl</c> and
/// <c>validation/VertexSemanticsKniGl</c> (the <c>integer dialect</c> rows).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class GlIntegerDialectTests
{
    private const string Prelude =
        "#if !defined(GL_ES) && __VERSION__ < 130 && defined(GL_EXT_gpu_shader4)\n" +
        "#extension GL_EXT_gpu_shader4 : enable\n" +
        "#define uint unsigned int\n" +
        "#endif\n";

    // The issue's reproduction: the vertex stage needs the prelude, the pixel stage must not get it.
    private const string VertexIdShader = """
struct VSOut { float4 pos : SV_Position; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    o.pos = float4(float((id << 1) & 2) * 2 - 1, float(id & 2) * 2 - 1, 0, 1);
    return o;
}

float4 PS(VSOut i) : SV_Target { return float4(1, 0, 0, 1); }

technique T { pass P { VertexShader = compile vs_6_0 VS(); PixelShader = compile ps_6_0 PS(); } }
""";

    [Fact]
    public async Task VertexIdShader_OnlyTheVertexStageCarriesThePrelude()
    {
        var result = await new EffectCompiler().CompileAsync(VertexIdShader, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "Issue524.fx",
        });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => e.Message)) : "");
        string text = Encoding.Latin1.GetString(result.Value.Data);

        int first = text.IndexOf(Prelude, StringComparison.Ordinal);
        first.ShouldBeGreaterThanOrEqualTo(0);
        text.IndexOf(Prelude, first + 1, StringComparison.Ordinal).ShouldBe(-1);
        text.IndexOf("gl_VertexID", first, StringComparison.Ordinal).ShouldBeGreaterThan(first);
        result.Value.Warnings.ShouldContain(w => w.Code == "SD0403" && w.Message.Contains("gl_VertexID", StringComparison.Ordinal));
    }
}
