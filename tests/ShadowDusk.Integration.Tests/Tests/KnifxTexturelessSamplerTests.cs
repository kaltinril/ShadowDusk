#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// A legacy sampler that binds no texture (<c>sampler s0;</c>, <c>sampler2D S : register(s0);</c>,
/// <c>sampler2D S = sampler_state { MagFilter = POINT; };</c>) is NO effect parameter in KNIFX, and
/// its sampler record binds parameter -1, exactly as KNI's own pipeline writes it. MGFX keeps
/// <c>mgfxc</c>'s shape: one Texture2D parameter named after the sampler.
///
/// <para>Measured 2026-10-28 with KNI's content pipeline 4.2.9001 and 4.3.9001
/// (<c>/platform:DesktopGL</c> and <c>/platform:Windows</c>) on these exact shapes: no parameter,
/// sampler <c>param -1</c>, and a textured sampler beside it keeps its texture parameter. In KNI's
/// runtime (4.2.9001 and 4.3.9001, SDL2.GL and WinForms.DX11) a parameter for such a sampler made
/// <c>EffectPass.Apply</c> overwrite <c>GraphicsDevice.Textures[n]</c> with the never-set parameter's
/// null, so a texture the game bound directly (KNI EffectTest <c>OldSyntax_x</c> / <c>OldSyntax_s0s1</c>)
/// was lost; with KNI's shape Apply leaves it bound.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class KnifxTexturelessSamplerTests
{
    private static string Source(string ps, string declarations, string body) => $$"""
        {{declarations}}
        float4 PS_Main(float2 uv : TEXCOORD0) : COLOR0 { return {{body}}; }
        technique { pass { PixelShader = compile {{ps}} PS_Main(); } }
        """;

    // KNI EffectTest's OldSyntax_x / OldSyntax_s0s1: sampler_state blocks with no Texture.
    private const string SamplerStateNoTexture = """
        sampler2D Sampler0 = sampler_state { };
        sampler2D Sampler1 = sampler_state { MAGFILTER = POINT; MINFILTER = POINT; AddressU = CLAMP; };
        """;

    private const string Bare = "sampler s0;";

    private const string Registered = "sampler2D S : register(s0);\nsampler2D T : register(s1);";

    private const string Mixed = """
        Texture2D Tex;
        sampler2D WithTex = sampler_state { Texture = (Tex); };
        sampler2D NoTex = sampler_state { MagFilter = POINT; };
        float4 Tint = float4(1, 1, 1, 1);
        """;

    public static TheoryData<string, string, string, string[]> Shapes() => new()
    {
        // name, declarations, body, the texture-less samplers
        { "SamplerStateNoTexture", SamplerStateNoTexture, "tex2D(Sampler0, uv) * tex2D(Sampler1, uv)", ["Sampler0", "Sampler1"] },
        { "Bare", Bare, "tex2D(s0, uv)", ["s0"] },
        { "Registered", Registered, "tex2D(S, uv) * tex2D(T, uv)", ["S", "T"] },
        { "Mixed", Mixed, "tex2D(WithTex, uv) * tex2D(NoTex, uv) * Tint", ["NoTex"] },
    };

    private static async Task<CompiledShader> CompileOk(string source, PlatformTarget target, EffectContainer container)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            Container = container,
            SourceFileName = "TexturelessSampler.fx",
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        return result.Value;
    }

    private static string Ps(PlatformTarget target) => target == PlatformTarget.OpenGL ? "ps_2_0" : "ps_4_0_level_9_1";

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Knifx_TexturelessLegacySampler_HasNoParameter_AndBindsMinusOne_OnOpenGl(
        string name, string declarations, string body, string[] textureless)
    {
        CompiledShader shader = await CompileOk(Source(Ps(PlatformTarget.OpenGL), declarations, body), PlatformTarget.OpenGL, EffectContainer.Knifx);
        AssertKniShape(name, shader, textureless);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Knifx_TexturelessLegacySampler_HasNoParameter_AndBindsMinusOne_OnDirectX(
        string name, string declarations, string body, string[] textureless)
    {
        CompiledShader shader = await CompileOk(Source(Ps(PlatformTarget.DirectX), declarations, body), PlatformTarget.DirectX, EffectContainer.Knifx);
        AssertKniShape(name, shader, textureless);
    }

    private static void AssertKniShape(string name, CompiledShader shader, string[] textureless)
    {
        var knifx = KnifxBlobReader.Parse(shader.Data);

        foreach (string sampler in textureless)
        {
            knifx.ParameterNames.ShouldNotContain(sampler, $"{name}: KNI writes no parameter for texture-less sampler '{sampler}'");
            knifx.ParameterNames.ShouldNotContain(sampler + "_SDTexture", name);
            shader.Parameters.ShouldNotContain(p => p.Name == sampler, $"{name}: reflection matches the container");
        }

        var records = knifx.ShaderSamplers.ShouldHaveSingleItem();
        records.Count.ShouldBe(name == "Bare" ? 1 : 2, name);
        records.Count(r => r.Parameter == -1).ShouldBe(textureless.Length, $"{name}: one unbound record per texture-less sampler");

        if (name == "Mixed")
        {
            // The textured sampler still binds its texture, by the texture's name.
            records.Where(r => r.Parameter >= 0).ShouldHaveSingleItem().Parameter
                .ShouldBe(knifx.ParameterNames.ToList().IndexOf("Tex"));
            knifx.ParameterNames.ShouldContain("Tint");
        }
        else
        {
            records.ShouldAllBe(r => r.Parameter == -1);
        }
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task Mgfx_TexturelessLegacySampler_KeepsMgfxcsParameterNamedAfterTheSampler(PlatformTarget target)
    {
        // mgfxc 3.8.4.1 writes a Texture2D parameter named after the sampler on every profile;
        // MonoGame binds it, so MGFX is unchanged.
        CompiledShader shader = await CompileOk(Source(Ps(target), SamplerStateNoTexture, "tex2D(Sampler0, uv) * tex2D(Sampler1, uv)"), target, EffectContainer.Mgfx);

        var mgfx = MgfxBlobReader.Parse(shader.Data);
        mgfx.ParameterNames.ShouldBe(["Sampler0", "Sampler1"]);
        mgfx.Samplers.Select(s => mgfx.ParameterNames[s.Parameter]).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["Sampler0", "Sampler1"]);
    }
}
