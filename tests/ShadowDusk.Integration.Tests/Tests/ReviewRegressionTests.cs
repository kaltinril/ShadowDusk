#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Regression guards for the compile-path defects found in the 2026-07-27 full-project
/// review. Each pins a shape that previously produced a silently wrong <c>.mgfx</c>, or
/// hard-failed a compile that <c>mgfxc</c> accepts.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReviewRegressionTests
{
    private static CancellationTokenSource Cts() => new(TimeSpan.FromSeconds(120));

    // ── One SamplerState shared by N textures must emit N sampler records ────────────

    private const string SharedSamplerShader = """
        Texture2D DiffuseMap;
        Texture2D Lightmap;
        SamplerState TextureSampler;

        struct VOut { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

        VOut VS(float4 pos : POSITION0, float2 uv : TEXCOORD0)
        {
            VOut o; o.Pos = pos; o.UV = uv; return o;
        }

        float4 PS(VOut i) : SV_Target0
        {
            return DiffuseMap.Sample(TextureSampler, i.UV)
                 * Lightmap.Sample(TextureSampler, i.UV);
        }

        technique T
        {
            pass P { VertexShader = compile vs_4_0 VS(); PixelShader = compile ps_4_0 PS(); }
        }
        """;

    // One texture read through TWO SamplerStates: the mirror of the shape above, and the
    // one a texture-driven GL table silently breaks.
    private const string TwoSamplersOneTextureShader = """
        Texture2D CurrentTexture;
        SamplerState LinearSampler { MinFilter = Linear; };
        SamplerState PointSampler  { MinFilter = Point;  };

        float4 PS(float2 uv : TEXCOORD0) : SV_Target0
        {
            return CurrentTexture.Sample(LinearSampler, uv) * 0.5
                 + CurrentTexture.Sample(PointSampler,  uv) * 0.5;
        }

        technique T
        {
            pass P { PixelShader = compile ps_4_0 PS(); }
        }
        """;

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_OneTextureTwoSamplers_NamesEverySamplerUniformTheGlslDeclares()
    {
        // SPIRV-Cross emits one COMBINED sampler per (texture, sampler) PAIR and the
        // rewriter numbers them ps_s0, ps_s1, ... in declaration order. A table keyed on
        // TEXTURES would emit a single record here, leaving ps_s1 unassigned at its default
        // texture unit 0 with unit 0's state, so the Point sampler silently vanishes.
        // Every declared uniform must have a record naming it.
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(TwoSamplersOneTextureShader, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "TwoSamplersOneTexture.fx",
        }, cts.Token);

        result.IsSuccess.Should().BeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        string glsl = System.Text.Encoding.UTF8.GetString(ps.Bytecode);

        int declared = System.Text.RegularExpressions.Regex
            .Matches(glsl, @"^\s*uniform\s+sampler\w+\s+\w+\s*;", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Count;
        declared.Should().Be(2, "both sampler states are used, so SPIRV-Cross emits two combined samplers");

        var names = reader.Samplers.Where(s => s.ShaderIndex == ps.Index).Select(s => s.Name).ToList();
        names.Should().BeEquivalentTo(new[] { "ps_s0", "ps_s1" },
            "every sampler uniform the GLSL declares must be named by a record, or it never gets a texture unit");
    }

    [Theory]
    [Trait("Platform", "DirectX")]
    [InlineData(PlatformTarget.DirectX)]
    public async Task SharedSamplerState_EmitsOneSamplerRecordPerTexture(PlatformTarget target)
    {
        // The .mgfx sampler table is what MonoGame's ApplySamplers iterates to bind
        // device.Textures[slot]. Building it from the reflected SAMPLERS emitted ONE
        // record for this classic diffuse+lightmap shape, so Lightmap was never bound and
        // `Parameters["Lightmap"].SetValue(tex)` silently did nothing — exit 0, wrong
        // render. mgfxc's own golden for this shape carries TWO records
        // (tests/fixtures/golden/DirectX_11/PenumbraTexture.mgfx).
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(SharedSamplerShader, new CompilerOptions
        {
            Target = target,
            SourceFileName = "SharedSampler.fx",
        }, cts.Token);

        result.IsSuccess.Should().BeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var pixelSamplers = reader.Samplers
            .Where(s => s.ShaderIndex == reader.Shaders.Single(sh => !sh.IsVertex).Index)
            .ToList();

        pixelSamplers.Should().HaveCount(2,
            "two textures are sampled, so two texture slots must be bindable even though they share one SamplerState");
        pixelSamplers.Select(s => s.TextureSlot).Should().BeEquivalentTo(new byte[] { 0, 1 });
        pixelSamplers.Select(s => s.Parameter).Should().OnlyHaveUniqueItems(
            "each record must point at its OWN texture parameter, not both at the first");
    }

    // ── FNA sampler_state must accept the same numeric spellings as the MGFX path ────

    private const string SuffixedSamplerStateShader = """
        texture BaseTexture;
        sampler2D BaseSampler = sampler_state
        {
            Texture = <BaseTexture>;
            MipMapLodBias = -2.0f;
            MaxAnisotropy = 4.0;
            MinFilter = Linear;
            MagFilter = Linear;
        };

        float4 PS(float2 uv : TEXCOORD0) : COLOR0
        {
            return tex2D(BaseSampler, uv);
        }

        technique T
        {
            pass P { PixelShader = compile ps_3_0 PS(); }
        }
        """;

    [Theory]
    [InlineData(PlatformTarget.Fna)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task SamplerStateNumericLiterals_ParseIdenticallyOnEveryTarget(PlatformTarget target)
    {
        // mgfxc's ParseTreeTools strips a trailing f/F and floors a float-spelled int, and
        // the FxLexer keeps the suffix in the token. The FNA fx_2_0 builder used a raw
        // float/int.TryParse, so this block compiled for OpenGL and DirectX but hard-failed
        // for FNA — a target-dependent rejection of source fxc /T fx_2_0 accepts.
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(SuffixedSamplerStateShader, new CompilerOptions
        {
            Target = target,
            SourceFileName = "SuffixedSamplerState.fx",
        }, cts.Token);

        result.IsSuccess.Should().BeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
    }

    // ── Pass render states must accept HLSL float suffixes ──────────────────────────

    private const string SuffixedRenderStateShader = """
        struct VOut { float4 Pos : SV_POSITION; };

        VOut VS(float4 pos : POSITION0) { VOut o; o.Pos = pos; return o; }
        float4 PS(VOut i) : SV_Target0 { return float4(1, 1, 1, 1); }

        technique T
        {
            pass P
            {
                DepthBias = 0.0001f;
                SlopeScaleDepthBias = 2.0f;
                ColorWriteEnable = None;
                VertexShader = compile vs_4_0 VS();
                PixelShader = compile ps_4_0 PS();
            }
        }
        """;

    [Fact]
    [Trait("Platform", "DirectX")]
    public async Task PassRenderStates_AcceptFloatSuffixAndSymbolicNone()
    {
        // `DepthBias = 0.0001f;` and `ColorWriteEnable = None;` are both ordinary HLSL that
        // mgfxc compiles; both used to abort the whole compile with SD0011.
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(SuffixedRenderStateShader, new CompilerOptions
        {
            Target = PlatformTarget.DirectX,
            SourceFileName = "SuffixedRenderState.fx",
        }, cts.Token);

        result.IsSuccess.Should().BeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
    }

    // ── OpenGL: sparse sampler registers must fail LOUDLY (SD0215) ──────────────────

    private const string SparseSamplerRegisterShader = """
        Texture2D Detail : register(t3);
        SamplerState DetailSampler : register(s3);

        float4 PS(float2 uv : TEXCOORD0) : SV_Target0
        {
            return Detail.Sample(DetailSampler, uv);
        }

        technique T
        {
            pass P { PixelShader = compile ps_4_0 PS(); }
        }
        """;

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_SparseSamplerRegisters_FailWithSd0215()
    {
        // GL binds samplers by POSITIONAL name (ps_s0, ps_s1, …), so a sampler parked at s3
        // with no s0-s2 would have the .mgfx name a uniform the emitted GLSL never declares:
        // glGetUniformLocation misses and the shader silently samples a stale texture unit.
        // This is the under-fire direction of the guard — nothing pinned that it fires at
        // all, so a reflection change that compacts slots could disarm it invisibly.
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(SparseSamplerRegisterShader, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "SparseSamplerRegister.fx",
        }, cts.Token);

        result.IsFailure.Should().BeTrue("a sparse register layout cannot be named positionally");
        result.Error.Should().Contain(e => e.Code == "SD0215");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_SharedSamplerAcrossTextures_FailsWithSd0216_NotSilently()
    {
        // The GL table is keyed on SAMPLERS (it must name the ps_s{k} uniforms the GLSL
        // declares), so this shape yields two declared uniforms and one record. There is no
        // pair list to build from at this layer, so the honest answer is a compile error,
        // not a table that leaves ps_s1 sampling texture unit 0. The DirectX target has no
        // such constraint and compiles this fine (see the DirectX test above).
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(SharedSamplerShader, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "SharedSamplerGl.fx",
        }, cts.Token);

        result.IsFailure.Should().BeTrue(
            "a sampler table that cannot bind every declared uniform must not ship silently");
        result.Error.Should().Contain(e => e.Code == "SD0216");
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    // POSITIVE CONTROL for the guard above: the over-fire direction. Without these, a change
    // that made SD0215 fire unconditionally would leave the test above green.
    [InlineData("t0", "s0")]
    // The guard keys on SAMPLER registers, because that is what the ps_s{k} record name is
    // derived from. A lone texture parked at a non-zero t-register still emits exactly one
    // `uniform sampler2D ps_s0;`, so keying the guard on TEXTURE registers instead would
    // reject source that compiles into perfectly correct output.
    [InlineData("t1", "s0")]
    [InlineData("t3", "s0")]
    public async Task OpenGl_ContiguousSamplerRegisters_StillCompile(string texReg, string sampReg)
    {
        string source = $$"""
            Texture2D Base : register({{texReg}});
            SamplerState BaseSampler : register({{sampReg}});

            float4 PS(float2 uv : TEXCOORD0) : SV_Target0
            {
                return Base.Sample(BaseSampler, uv);
            }

            technique T
            {
                pass P { PixelShader = compile ps_4_0 PS(); }
            }
            """;
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "ContiguousSamplerRegister.fx",
        }, cts.Token);

        result.IsSuccess.Should().BeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
    }
}
