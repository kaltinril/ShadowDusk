#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #414: pins what the "Annotations as shader UI metadata" guide says. A numeric
/// parameter's annotations reach <see cref="CompiledShader.Parameters"/> verbatim on the MGFX and
/// KNIFX targets, FNA lists none, and a <c>texture</c> annotation compiles but is not reported.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue414AnnotationReflectionTests
{
    private const string Annotated =
        "float Speed < string UIName = \"Speed > fast; really\"; float UIMin = -1.5; float UIMax = 5.0; " +
        "int UISteps = 10; bool UIVisible = true; > = 1.0;\n";

    private const string ModernSource =
        "#if SM6\n#define PS_SHADERMODEL ps_6_0\n#elif SM4\n#define PS_SHADERMODEL ps_4_0\n#else\n" +
        "#define PS_SHADERMODEL ps_3_0\n#endif\n" + Annotated +
        "float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0) : SV_Target0 { return color * Speed; }\n" +
        "technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";

    private const string FnaSource =
        Annotated +
        "float4 MainPS(float4 color : COLOR0) : COLOR0 { return color * Speed; }\n" +
        "technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }\n";

    private const string AnnotatedTextureSource =
        "#define PS_SHADERMODEL ps_3_0\n" +
        "texture Tex < string UIName = \"Base\"; >;\n" +
        "sampler2D Smp = sampler_state { Texture = <Tex>; };\n" +
        "float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0 " +
        "{ return tex2D(Smp, uv) * color; }\n" +
        "technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";

    private static async Task<CompiledShader> CompileAsync(
        string source, PlatformTarget target, EffectContainer container = EffectContainer.Mgfx)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            Container = container,
            SourceFileName = "Annotations.fx",
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))
            : "must compile");
        return result.Value;
    }

    private static void AssertSpeedAnnotations(CompiledShader shader)
    {
        IReadOnlyList<AnnotationReflection> annotations =
            shader.Parameters.Single(p => p.Name == "Speed").Annotations.ShouldNotBeNull();

        annotations.Select(a => (a.Name, a.Value)).ShouldBe(new[]
        {
            ("UIName", "\"Speed > fast; really\""),
            ("UIMin", "-1.5"),
            ("UIMax", "5.0"),
            ("UISteps", "10"),
            ("UIVisible", "true"),
        });
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.Vulkan)]
    [InlineData(PlatformTarget.DirectX12)]
    public async Task NumericParameterAnnotations_AreReportedVerbatim(PlatformTarget target) =>
        AssertSpeedAnnotations(await CompileAsync(ModernSource, target));

    [Fact]
    public async Task NumericParameterAnnotations_AreReportedVerbatim_OnKnifx() =>
        AssertSpeedAnnotations(await CompileAsync(ModernSource, PlatformTarget.OpenGL, EffectContainer.Knifx));

    [FnaFact]
    public async Task NumericParameterAnnotations_AreReportedVerbatim_OnDirectX11() =>
        AssertSpeedAnnotations(await CompileAsync(ModernSource, PlatformTarget.DirectX));

    [FnaFact]
    public async Task Fna_ListsNoAnnotations()
    {
        CompiledShader shader = await CompileAsync(FnaSource, PlatformTarget.Fna);

        shader.Parameters.Single(p => p.Name == "Speed").Annotations.ShouldBeNull();
    }

    [Fact]
    public async Task TextureAnnotation_CompilesButIsNotReported()
    {
        CompiledShader shader = await CompileAsync(AnnotatedTextureSource, PlatformTarget.OpenGL);

        shader.Parameters.Single(p => p.Name == "Tex").Annotations.ShouldBeNull();
    }
}
