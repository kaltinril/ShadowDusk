#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// GitHub issue #283: on the <c>.fx</c> route the OpenGL sampler-register reservation (a modern
/// <c>SamplerState X : register(sN)</c> takes register N out of circulation, so the combined
/// sampler fxc synthesizes is allocated around it) was decided on the raw source. <c>mgfxc</c>
/// decides it on the preprocessed source. Every expected slot below was measured against the
/// pinned <c>mgfxc</c> 3.8.4.1 <c>/Profile:OpenGL</c> (2026-10-02), and the committed goldens
/// <c>SamplerReservationIfBranch</c> / <c>SamplerReservationMacro</c> carry the same answers.
/// Before the fix ShadowDusk emitted <c>ps_s1</c>, <c>ps_s0</c>/<c>ps_s1</c> and <c>ps_s0</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue283PreprocessedSamplerReservationTests
{
    private static CancellationTokenSource Cts() => new(TimeSpan.FromSeconds(120));

    private const string Header = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif

        """;

    private const string Technique = """

        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    private static async Task<List<MgfxSamplerRecord>> GlPixelSamplers(
        string source, IIncludeResolver? includeResolver = null, IReadOnlyList<UserDefine>? defines = null)
    {
        using var cts = Cts();
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "Issue283.fx",
            IncludeResolver = includeResolver,
            Defines = defines ?? [],
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        return reader.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_RegisterOnlyInTheInactiveBranch_DoesNotPushThePairOffUnitZero()
    {
        string source = Header + """
            Texture2D SpriteTexture;
            #if OPENGL
            SamplerState SpriteSampler;
            #else
            SamplerState SpriteSampler : register(s0);
            #endif
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
            {
                return SpriteTexture.Sample(SpriteSampler, uv);
            }
            """ + Technique;

        var records = await GlPixelSamplers(source);

        records.Single().Name.ShouldBe("ps_s0",
            customMessage: "mgfxc emits ps_s0: the s0 register is in the branch OpenGL does not compile");
        records.Single().TextureSlot.ShouldBe((byte)0);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_RegistersSpelledThroughAMacro_AreReserved()
    {
        string source = Header + """
            #define SLOT(n) : register(n)
            Texture2D MaskA;
            Texture2D MaskB;
            SamplerState SampA SLOT(s0);
            SamplerState SampB SLOT(s1);
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
            {
                return float4(MaskA.Sample(SampA, uv).r, MaskB.Sample(SampB, uv).g, 0, 1);
            }
            """ + Technique;

        var records = await GlPixelSamplers(source);

        records.Select(r => r.Name).ShouldBe(new[] { "ps_s2", "ps_s3" },
            customMessage: "mgfxc emits ps_s2/ps_s3, the same as the directly written register(s0)/register(s1)");
        records.Select(r => r.TextureSlot).ShouldBe(new byte[] { 2, 3 });
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_RegisterInAnIncludedFile_IsReserved()
    {
        string source = Header + """
            Texture2D SpriteTexture;
            #include "samplers.fxh"
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
            {
                return SpriteTexture.Sample(SpriteSampler, uv);
            }
            """ + Technique;

        var records = await GlPixelSamplers(source, new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["samplers.fxh"] = "SamplerState SpriteSampler : register(s0);\n",
        }));

        records.Single().Name.ShouldBe("ps_s1",
            customMessage: "mgfxc emits ps_s1: the included SamplerState occupies s0 exactly as if written inline");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_RegisterGatedOnAUserDefine_FollowsTheDefine()
    {
        string source = Header + """
            Texture2D SpriteTexture;
            #if PIN_SAMPLER
            SamplerState SpriteSampler : register(s0);
            #else
            SamplerState SpriteSampler;
            #endif
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
            {
                return SpriteTexture.Sample(SpriteSampler, uv);
            }
            """ + Technique;

        (await GlPixelSamplers(source)).Single().Name.ShouldBe("ps_s0");
        (await GlPixelSamplers(source, defines: [new UserDefine("PIN_SAMPLER")]))
            .Single().Name.ShouldBe("ps_s1");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_MalformedConditional_ReportsDxcsOwnDiagnosticNotSd0009()
    {
        // DXC rejects this itself. The reservation view cannot be built either, but its SD0009
        // is deferred until DXC has accepted the source, so the user sees the compiler's message.
        string source = Header + """
            #if 1 +
            #endif
            Texture2D SpriteTexture;
            SamplerState SpriteSampler;
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
            {
                return SpriteTexture.Sample(SpriteSampler, uv);
            }
            """ + Technique;

        using var cts = Cts();
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "Malformed.fx",
        }, cts.Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.Select(e => e.Code).ShouldNotContain("SD0009");
    }
}
