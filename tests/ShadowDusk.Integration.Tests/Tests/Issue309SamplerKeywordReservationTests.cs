#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// GitHub issue #309: on OpenGL a sampler with an explicit <c>register(sN)</c> RESERVES unit N
/// for the combined samplers fxc synthesizes around it (issue #189), and ShadowDusk's matcher
/// only recognised the exact keyword <c>SamplerState</c>. Measured against the pinned
/// <c>mgfxc</c> 3.8.4.1 <c>/Profile:OpenGL</c> (2026-10-02): fxc reserves for EVERY sampler
/// type keyword (<c>sampler</c>, <c>sampler2D</c>, <c>samplerCUBE</c>,
/// <c>SamplerComparisonState</c>), whether or not anything reads the sampler, and for every
/// entry point of the effect. Before the fix every lowercase-keyword shape below was
/// <c>ps_s0</c> (the texture on SpriteBatch's unit).
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue309SamplerKeywordReservationTests
{
    private static CancellationTokenSource Cts() => new(TimeSpan.FromSeconds(120));

    private const string Header = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif

        """;

    private const string ModernPs = """

        float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return Tex.Sample(S, uv); }
        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    private static async Task<MgfxBlobReader> CompileGl(string source, IIncludeResolver? includes = null)
    {
        using var cts = Cts();
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "Issue309.fx",
            IncludeResolver = includes,
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        return MgfxBlobReader.Parse(result.Value.Data);
    }

    private static string Units(MgfxBlobReader mgfx, string? entryPoint = null)
    {
        var shaders = mgfx.Shaders.Where(s => !s.IsVertex).ToList();
        MgfxShaderRecord ps = entryPoint is null
            ? shaders.Single()
            : shaders.Single(s => System.Text.Encoding.UTF8.GetString(s.Bytecode).Contains(entryPoint, StringComparison.Ordinal));
        return string.Join(" ", mgfx.Samplers.Where(s => s.ShaderIndex == ps.Index).OrderBy(s => s.TextureSlot).Select(s => s.Name));
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("SamplerState S : register(s0);")]
    [InlineData("sampler S : register(s0);")]
    [InlineData("sampler S : register(s0) = sampler_state { Texture = <Tex>; };")]
    [InlineData("#define REG s0\nsampler S : register(REG);")]
    [InlineData("#define SLOT(n) : register(n)\nsampler S SLOT(s0);")]
    public async Task OpenGl_SamplerReadThroughSample_ReservesItsRegister_WhateverTheKeyword(string declaration)
    {
        Units(await CompileGl(Header + "Texture2D Tex;\n" + declaration + ModernPs)).ShouldBe("ps_s1",
            customMessage: "mgfxc emits ps_s1: register(s0) is reserved, the pair is allocated around it");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_SamplerReadThroughSampleByTwoTextures_ReservesOnce()
    {
        const string source = Header + """
            Texture2D A; Texture2D B;
            sampler S : register(s1);
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return A.Sample(S, uv) + B.Sample(S, uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        Units(await CompileGl(source)).ShouldBe("ps_s0 ps_s2", customMessage: "mgfxc: s1 is skipped, not shifted");
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("sampler U : register(s0);")]
    [InlineData("sampler2D U : register(s0);")]
    [InlineData("samplerCUBE U : register(s0);")]
    [InlineData("sampler2D U : register(s0) = sampler_state { Texture = <Tex>; };")]
    [InlineData("SamplerComparisonState U : register(s0);")]
    public async Task OpenGl_AnUnusedSamplerWithARegister_StillReserves(string declaration)
    {
        Units(await CompileGl(Header + "Texture2D Tex;\nSamplerState S;\n" + declaration + ModernPs)).ShouldBe("ps_s1",
            customMessage: "mgfxc emits ps_s1: fxc keeps an explicitly bound register out of circulation even for an unused object");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_ReservationDeclaredInAnInclude_Counts()
    {
        var includes = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["s.fxh"] = "SamplerComparisonState C : register(s0);",
        });

        Units(await CompileGl(Header + "Texture2D Tex;\nSamplerState S;\n#include \"s.fxh\"" + ModernPs, includes)).ShouldBe("ps_s1");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_RegisterOnlyInTheInactiveBranch_ReservesNothing()
    {
        Units(await CompileGl(Header + "Texture2D Tex;\n#if OPENGL\nsampler S;\n#else\nsampler S : register(s0);\n#endif" + ModernPs)).ShouldBe("ps_s0");
    }

    /// <summary>
    /// A legacy sampler one entry point reads through <c>tex2D</c> is PINNED there and RESERVED
    /// everywhere: mgfxc compiles each entry point on its own, and the register stays bound in
    /// the one that never reads it. Before the fix PSB's sampler took the vacant unit 0.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_LegacySamplerPinnedByAnotherEntryPoint_StillReservesHere()
    {
        const string source = Header + """
            sampler X : register(s0);
            sampler2D Y;
            sampler2D Z;
            float4 PSA(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(X, uv); }
            float4 PSB(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(Y, uv) + tex2D(Z, uv); }
            technique A { pass P { PixelShader = compile PS_SHADERMODEL PSA(); } }
            technique B { pass P { PixelShader = compile PS_SHADERMODEL PSB(); } }
            """;

        MgfxBlobReader mgfx = await CompileGl(source);

        // The two pixel shaders are told apart by the uniform they sample: PSA reads X only.
        var shaders = mgfx.Shaders.Where(s => !s.IsVertex).ToList();
        shaders.Count.ShouldBe(2);
        var tables = shaders
            .Select(s => string.Join(" ", mgfx.Samplers.Where(r => r.ShaderIndex == s.Index).OrderBy(r => r.TextureSlot).Select(r => r.Name)))
            .OrderBy(t => t.Length)
            .ToList();
        tables[0].ShouldBe("ps_s0", customMessage: "PSA: X pinned to its register");
        tables[1].ShouldBe("ps_s1 ps_s2", customMessage: "PSB: mgfxc keeps s0 reserved for X although PSB never reads it");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_SamplerReadBothWays_IsPinnedForTex2DAndReservedForSample()
    {
        const string source = Header + """
            sampler S : register(s1);
            Texture2D Tex;
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv) + Tex.Sample(S, uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        MgfxBlobReader mgfx = await CompileGl(source);

        string table = string.Join(" ", mgfx.Samplers.OrderBy(s => s.TextureSlot).Select(s => $"{s.Name}={mgfx.Parameters[s.Parameter].Name}"));
        table.ShouldBe("ps_s0=Tex ps_s1=S_SDTexture", customMessage: "mgfxc: the legacy combined sampler sits on s1, the (Tex, S) pair takes the lowest free unit");
    }

    /// <summary>A shape mgfxc rejects must not start compiling here: the keyword stays DXC's error, plus SD0016.</summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_Sampler2DReadThroughSample_StillFails()
    {
        using var cts = Cts();
        var result = await new EffectCompiler().CompileAsync(
            Header + "Texture2D Tex;\nsampler2D S : register(s0);" + ModernPs,
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Issue309.fx" }, cts.Token);

        result.IsFailure.ShouldBeTrue("mgfxc rejects it too (X3013: 'Sample': no matching 2 parameter intrinsic method)");
        result.Error.ShouldContain(e => e.Code == "SD0016");
    }
}
