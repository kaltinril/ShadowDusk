#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Tests.Fx2;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// An anonymous pass (<c>pass { ... }</c>) is written with an EMPTY name on every container, as the
/// reference compilers write it; ShadowDusk used to synthesize <c>P0</c>, <c>P1</c>, ...
///
/// <para>Measured 2026-10-28 on this shader (two anonymous passes, then a named and an anonymous
/// one): <c>mgfxc</c> 3.8.4.1 writes <c>""</c> for every anonymous pass in MGFX on OpenGL and
/// DirectX_11; KNI's own content pipeline (4.2.9001 and 4.3.9001, DesktopGL and Windows) writes
/// <c>""</c> in KNIFX; <c>fxc /T fx_2_0</c> (10.0.19041) writes a present one-byte empty string for
/// the anonymous technique and passes. In the real runtimes MonoGame and KNI report
/// <c>EffectPass.Name == ""</c> for mgfxc's and KNI's builds (KNI EffectTest port, research branch
/// <c>research/kni-effecttest</c>), and passes are applied by index either way.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AnonymousPassNameTests
{
    private static string Source(string ps) => $$"""
        float4 Tint = float4(1, 0, 0, 1);
        float4 PS_Main(float2 uv : TEXCOORD0) : COLOR0 { return Tint; }
        float4 PS_Two(float2 uv : TEXCOORD0) : COLOR0 { return Tint * 0.5; }

        technique
        {
            pass { PixelShader = compile {{ps}} PS_Main(); }
            pass { PixelShader = compile {{ps}} PS_Two(); }
        }

        technique Named
        {
            pass First { PixelShader = compile {{ps}} PS_Main(); }
            pass { PixelShader = compile {{ps}} PS_Two(); }
        }
        """;

    private static async Task<CompiledShader> CompileOk(PlatformTarget target, EffectContainer container = EffectContainer.Mgfx)
    {
        string ps = target switch
        {
            PlatformTarget.OpenGL or PlatformTarget.Fna => "ps_2_0",
            _ => "ps_4_0_level_9_1",
        };
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(Source(ps), new CompilerOptions
        {
            Target = target,
            Container = container,
            SourceFileName = "AnonymousPass.fx",
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        return result.Value;
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task Mgfx_AnonymousPasses_AreWrittenWithAnEmptyName(PlatformTarget target)
    {
        CompiledShader shader = await CompileOk(target);

        var techniques = MgfxBlobReader.Parse(shader.Data).Techniques;
        techniques.Select(t => t.Name).ShouldBe(["", "Named"]);
        techniques[0].Passes.Select(p => p.Name).ShouldBe(["", ""]);
        techniques[1].Passes.Select(p => p.Name).ShouldBe(["First", ""]);
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task Knifx_AnonymousPasses_AreWrittenWithAnEmptyName(PlatformTarget target)
    {
        CompiledShader shader = await CompileOk(target, EffectContainer.Knifx);

        var techniques = KnifxBlobReader.Parse(shader.Data).Techniques;
        techniques.Select(t => t.Name).ShouldBe(["", "Named"]);
        techniques[0].PassNames.ShouldBe(["", ""]);
        techniques[1].PassNames.ShouldBe(["First", ""]);
    }

    [Fact]
    public async Task Fna_AnonymousTechniqueAndPasses_AreFxcsPresentEmptyString()
    {
        CompiledShader shader = await CompileOk(PlatformTarget.Fna);

        var techniques = Fx2BinaryValidator.Parse(shader.Data).Techniques;
        techniques.Select(t => (t.Name, t.NameIsNull)).ShouldBe([("", false), ("Named", false)]);
        techniques[0].Passes.Select(p => (p.Name, p.NameIsNull)).ShouldBe([("", false), ("", false)]);
        techniques[1].Passes.Select(p => (p.Name, p.NameIsNull)).ShouldBe([("First", false), ("", false)]);
    }
}
