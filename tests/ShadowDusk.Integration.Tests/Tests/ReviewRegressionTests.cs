#nullable enable

using Shouldly;
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

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        string glsl = System.Text.Encoding.UTF8.GetString(ps.Bytecode);

        int declared = System.Text.RegularExpressions.Regex
            .Matches(glsl, @"^\s*uniform\s+sampler\w+\s+\w+\s*;", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Count;
        declared.ShouldBe(2, customMessage: "both sampler states are used, so SPIRV-Cross emits two combined samplers");

        var names = reader.Samplers.Where(s => s.ShaderIndex == ps.Index).Select(s => s.Name).ToList();
        names.ShouldBe(new[] { "ps_s0", "ps_s1" }, ignoreOrder: true, customMessage: "every sampler uniform the GLSL declares must be named by a record, or it never gets a texture unit");
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

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var pixelSamplers = reader.Samplers
            .Where(s => s.ShaderIndex == reader.Shaders.Single(sh => !sh.IsVertex).Index)
            .ToList();

        pixelSamplers.Count().ShouldBe(2, customMessage: "two textures are sampled, so two texture slots must be bindable even though they share one SamplerState");
        pixelSamplers.Select(s => s.TextureSlot).ShouldBe(new byte[] { 0, 1 }, ignoreOrder: true);
        pixelSamplers.Select(s => s.Parameter).ShouldBeUnique(
            "each record must point at its OWN texture parameter, not both at the first");
    }

    // ── FNA sampler_state must accept the same numeric spellings as the MGFX path ────

    // The SM4-gated profile header is load-bearing for the DirectX arm (Phase 51 A10):
    // MonoGame's DirectX_11 profile refuses ps_3_0 outright (SD0015, matching mgfxc), and
    // the FNA target refuses anything above SM3, so a single literal profile cannot serve
    // both. The subject here is sampler-state numeric-literal parsing, not the profile.
    private const string SuffixedSamplerStateShader = """
        #if SM4
            #define PS_SHADERMODEL ps_4_0_level_9_1
        #else
            #define PS_SHADERMODEL ps_3_0
        #endif

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
            pass P { PixelShader = compile PS_SHADERMODEL PS(); }
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

        result.IsSuccess.ShouldBeTrue(
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

        result.IsSuccess.ShouldBeTrue(
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
    public async Task OpenGl_SparseSamplerRegisters_Compile_BecauseRecordIndexIsNotTheBindSlot()
    {
        // A sampler parked at s3 with no s0-s2 used to be rejected (SD0215) because the GL
        // record was named ps_s{samp.BindSlot} — naming a uniform the GLSL never declares.
        // Since Phase 51 A7 the record index is the pair's DECLARATION position, so bind slots
        // are never consulted for GL naming and this ordinary HLSL compiles, as mgfxc does.
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(SparseSamplerRegisterShader, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "SparseSamplerRegister.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        var records = reader.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();

        records.ShouldHaveSingleItem("one texture through one sampler is exactly one pair");
        records[0].Name.ShouldBe("ps_s0", customMessage: "the record must name the uniform the GLSL declares, which is numbered from 0 " +
            "regardless of the s3 register");
        records[0].TextureSlot.ShouldBe((byte)(0));
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_SharedSamplerAcrossTextures_EmitsOneRecordPerPair()
    {
        // Two textures read through ONE shared SamplerState is ordinary HLSL that mgfxc
        // compiles: its own OpenGL golden for this shape
        // (tests/fixtures/golden/OpenGL/PenumbraTexture.mgfx) carries TWO records, ps_s0 and
        // ps_s1, on texture slots 0 and 1 pointing at DIFFERENT texture parameters. ShadowDusk
        // used to reject it (SD0216) because its GL table was keyed on the reflected SAMPLERS,
        // of which there is one. Keyed on the (texture, sampler) PAIRS it now matches mgfxc.
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(SharedSamplerShader, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "SharedSamplerGl.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        var records = reader.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();

        records.Select(s => s.Name).ShouldBe(new[] {"ps_s0", "ps_s1"});
        // Each pair needs its own GL texture unit even though they share one SamplerState.
        records.Select(s => s.TextureSlot).ShouldBe(new byte[] { 0, 1 });
        records.Select(s => s.Parameter).ShouldBeUnique(
            "each record must point at its OWN texture parameter, not both at the first");

        // DiffuseMap is sampled first, so it is the first-declared combined sampler.
        string ParamName(byte index) => reader.Parameters[index].Name;
        ParamName(records[0].Parameter).ShouldBe("DiffuseMap");
        ParamName(records[1].Parameter).ShouldBe("Lightmap");
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("t0", "s0")]
    [InlineData("t1", "s0")]
    [InlineData("t3", "s0")]
    [InlineData("t0", "s2")]
    [InlineData("t3", "s3")]
    public async Task OpenGl_ExplicitSamplerRegisters_StillCompile(string texReg, string sampReg)
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

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
    }

    // ── GL records must follow HLSL DECLARATION order, and agree with the emitted GLSL ──
    //
    // Two separate bug classes are pinned here.
    //
    // Phase 51 A7: SD0216's count check could never see a table keyed on bind slots silently
    // pairing each ps_s{k} with the WRONG texture once the counts already agreed.
    //
    // GitHub issue #189: having fixed the PAIRING, the NUMBERING was still SPIRV-Cross's
    // first-use order, while fxc — and therefore mgfxc — allocates OpenGL sampler slots in
    // HLSL declaration order. A `register(s0)` sampler that was not also sampled first landed
    // on unit 1, and under SpriteBatch (which forces the sprite onto unit 0 right after
    // EffectPass.Apply) that silently swapped textures. Render-proved against the reference
    // compiler by validation/SamplerRegisterOrderGl.
    //
    // So these tests now assert DECLARATION order, and deliberately keep sampling out of
    // declaration order, which is what makes the two orders distinguishable at all.

    /// <summary>
    /// Two textures with DIFFERENT dimensions, sampled in reverse declaration order. The
    /// dimensions make a mis-pairing unambiguous: <c>DiffuseMap</c> is declared first, so it
    /// owns unit 0 and <c>ps_s0</c>'s record must carry the 2D sampler-type byte and point at
    /// it, even though the cube is sampled first. Two historical bugs are covered: the old
    /// slot-keyed table paired <c>ps_s0</c> with the wrong texture entirely (A7), and the
    /// first-use numbering that replaced it put the wrong texture on unit 0 (#189).
    /// </summary>
    private const string ReverseUseOrderShader = """
        Texture2D DiffuseMap;
        TextureCube EnvMap;
        SamplerState SamplerA;
        SamplerState SamplerB;

        float4 PS(float3 uv : TEXCOORD0) : SV_Target0
        {
            return EnvMap.Sample(SamplerB, uv) * DiffuseMap.Sample(SamplerA, uv.xy);
        }

        technique T
        {
            pass P { PixelShader = compile ps_4_0 PS(); }
        }
        """;

    // MonoGame SamplerType byte: Sampler2D = 0, SamplerCube = 1, SamplerVolume = 2.
    private const byte SamplerType2D     = 0;
    private const byte SamplerTypeCube   = 1;
    private const byte SamplerTypeVolume = 2;

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_SamplingOutOfDeclarationOrder_PairsEachRecordWithItsOwnTexture()
    {
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(ReverseUseOrderShader, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "ReverseUseOrder.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        var records = reader.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();
        string glsl = System.Text.Encoding.UTF8.GetString(ps.Bytecode);

        records.Select(s => s.Name).ShouldBe(new[] {"ps_s0", "ps_s1"});

        // DECLARATION order, not first-use order (GitHub issue #189). EnvMap is sampled first,
        // so SPIRV-Cross folds its combined sampler first, but fxc — and therefore mgfxc —
        // allocates OpenGL sampler slots in declaration order, so DiffuseMap owns unit 0. The
        // emitted GLSL is still the authority on which uniform is which kind, and it must agree
        // with the record table: both are numbered from the same ResolveSlots result.
        glsl.ShouldMatch(@"uniform\s+sampler2D\s+ps_s0\s*;", customMessage: "DiffuseMap is declared first, so it owns texture unit 0 the way fxc allocates");
        glsl.ShouldMatch(@"uniform\s+samplerCube\s+ps_s1\s*;");

        records[0].Type.ShouldBe(SamplerType2D, customMessage: "ps_s0 is the 2D pair, so its sampler-type byte must say 2D or the texture will not bind");
        records[1].Type.ShouldBe(SamplerTypeCube);

        reader.Parameters[records[0].Parameter].Name.ShouldBe("DiffuseMap");
        reader.Parameters[records[1].Parameter].Name.ShouldBe("EnvMap");

        // The slots are what the runtime actually binds with, so assert them directly rather
        // than inferring them from the names.
        records[0].TextureSlot.ShouldBe((byte)0);
        records[1].TextureSlot.ShouldBe((byte)1);
    }

    /// <summary>
    /// One texture read through two <c>SamplerState</c>s with DIFFERENT baked state, sampled
    /// with the second-declared sampler first. Both records point at the one texture, but each
    /// must carry the state of ITS OWN sampler half — in declaration (first-use) order.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_OneTextureTwoSamplers_BakesEachPairsOwnSamplerState()
    {
        const string source = """
            Texture2D CurrentTexture;
            SamplerState LinearSampler { MinFilter = Linear; MagFilter = Linear; MipFilter = Linear; };
            SamplerState PointSampler  { MinFilter = Point;  MagFilter = Point;  MipFilter = Point;  };

            float4 PS(float2 uv : TEXCOORD0) : SV_Target0
            {
                return CurrentTexture.Sample(PointSampler, uv) * 0.5
                     + CurrentTexture.Sample(LinearSampler, uv) * 0.5;
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
            SourceFileName = "OneTextureTwoSamplers.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        var records = reader.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();

        records.Select(s => s.Name).ShouldBe(new[] {"ps_s0", "ps_s1"});
        // One texture bound to two units, so each pair can carry its own sampler state.
        records.Select(s => s.TextureSlot).ShouldBe(new byte[] { 0, 1 });
        records.Select(s => reader.Parameters[s.Parameter].Name)
            .ShouldBe(new[] {"CurrentTexture", "CurrentTexture"});

        // MonoGame TextureFilter: Point = 1, Linear = 0. PointSampler is sampled first.
        records[0].State.ShouldNotBeNull();
        records[1].State.ShouldNotBeNull();
        records[0].State!.Filter.ShouldBe((byte)(1), customMessage: "ps_s0 is the PointSampler pair (sampled first)");
        records[1].State!.Filter.ShouldBe((byte)(0), customMessage: "ps_s1 is the LinearSampler pair");
    }

    /// <summary>
    /// A legacy <c>sampler2D</c> declaration mixed with a modern
    /// <c>Texture2D</c>+<c>SamplerState</c> pair. The pre-parser rewrites the legacy form to the
    /// modern one before DXC sees it, so both become combined-sampler pairs and both are slotted
    /// by DECLARATION order — here the modern pair is sampled first but declared second, so it
    /// gets unit 1 (issue #189). The rewrite must not perturb that ordering.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_MixedLegacyAndModernSamplerDeclarations_KeepDeclarationOrder()
    {
        const string source = """
            Texture2D LegacyTex;
            sampler2D LegacySampler = sampler_state { Texture = <LegacyTex>; };

            Texture2D ModernTex;
            SamplerState ModernSampler;

            float4 PS(float2 uv : TEXCOORD0) : SV_Target0
            {
                return ModernTex.Sample(ModernSampler, uv) * tex2D(LegacySampler, uv);
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
            SourceFileName = "MixedSamplerDeclarations.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        var records = reader.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();

        records.Select(s => s.Name).ShouldBe(new[] {"ps_s0", "ps_s1"});
        reader.Parameters[records[0].Parameter].Name.ShouldBe("LegacyTex", customMessage: "LegacyTex is declared first, so it owns texture unit 0 the way fxc allocates (issue #189) — even though ModernTex is sampled first");
        reader.Parameters[records[1].Parameter].Name.ShouldBe("ModernTex");
    }

    /// <summary>
    /// The pair identity is only recoverable through the caller's argument remapping when the
    /// sampling happens inside a called function. DXC does not inline these, so this exercises
    /// the <c>OpFunctionCall</c> parameter-remapping branch of the traversal.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_SamplingInsideCalledFunctions_ResolvesPairsThroughParameters()
    {
        const string source = """
            Texture2D TexA;
            TextureCube TexB;
            SamplerState Samp;

            float4 FetchCube(TextureCube t, SamplerState s, float3 uv) { return t.Sample(s, uv); }
            float4 Fetch2D(Texture2D t, SamplerState s, float2 uv)     { return t.Sample(s, uv); }

            float4 PS(float3 uv : TEXCOORD0) : SV_Target0
            {
                return FetchCube(TexB, Samp, uv) * Fetch2D(TexA, Samp, uv.xy);
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
            SourceFileName = "SamplingInFunctions.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        var records = reader.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();

        records.Select(s => s.Name).ShouldBe(new[] {"ps_s0", "ps_s1"});
        records[0].Type.ShouldBe(SamplerType2D, customMessage: "TexA is declared first, so it owns unit 0 (issue #189) — even though FetchCube(TexB) is called first");
        records[1].Type.ShouldBe(SamplerTypeCube);
        reader.Parameters[records[0].Parameter].Name.ShouldBe("TexA");
        reader.Parameters[records[1].Parameter].Name.ShouldBe("TexB");
    }

    /// <summary>
    /// The load-bearing invariant, on a shape with enough structure that a wrong model cannot
    /// pass by luck: three textures of three DIFFERENT dimensions crossed with two samplers,
    /// sampled so the pairs come out in an order matching neither declaration order nor bind
    /// slots, with one texture reused under both samplers (exercising the dedup). The emitted
    /// GLSL's declaration kinds are checked position by position against the record type bytes,
    /// so the sequence 2D/Cube/3D/2D is a four-way discriminator on the ordering model.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_CombinedSamplerOrder_MatchesTheEmittedGlslPositionByPosition()
    {
        const string source = """
            Texture2D    DiffuseMap;
            TextureCube  EnvMap;
            Texture3D    VolumeMap;
            SamplerState SampA;
            SamplerState SampB;

            float4 PS(float3 uv : TEXCOORD0) : SV_Target0
            {
                // Pair order must come out: (EnvMap,SampA) (DiffuseMap,SampA)
                //                          (VolumeMap,SampB) (DiffuseMap,SampB)
                // -> declared kinds Cube, 2D, 3D, 2D. DiffuseMap appears twice under different
                //    samplers, so a dedup keyed on the texture alone would drop the last pair.
                return EnvMap.Sample(SampA, uv)
                     * DiffuseMap.Sample(SampA, uv.xy)
                     * VolumeMap.Sample(SampB, uv)
                     * DiffuseMap.Sample(SampB, uv.xy);
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
            SourceFileName = "CombinedSamplerOrder.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        var records = reader.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();
        string glsl = System.Text.Encoding.UTF8.GetString(ps.Bytecode);

        // Four distinct (texture, sampler) pairs are sampled. (Equal's params overload would
        // swallow a `because` string as a fifth expected element, so the reason stays a comment.)
        records.Select(s => s.Name).ShouldBe(new[] {"ps_s0", "ps_s1", "ps_s2", "ps_s3"});

        // The emitted GLSL is the oracle: read the declared kind for each ps_s{k} out of it and
        // require the record's sampler-type byte and texture parameter to agree.
        var expected = new[]
        {
            ("samplerCube", SamplerTypeCube,   "EnvMap"),
            ("sampler2D",   SamplerType2D,     "DiffuseMap"),
            ("sampler3D",   SamplerTypeVolume, "VolumeMap"),
            ("sampler2D",   SamplerType2D,     "DiffuseMap"),
        };

        for (int k = 0; k < expected.Length; k++)
        {
            var (glslKind, typeByte, textureName) = expected[k];
            glsl.ShouldMatch($@"uniform\s+{glslKind}\s+ps_s{k}\s*;", customMessage: $"ps_s{k} must be the {textureName} pair in SPIRV-Cross's declaration order");
            records[k].Type.ShouldBe(typeByte, customMessage: $"ps_s{k}'s sampler-type byte must match its declaration");
            reader.Parameters[records[k].Parameter].Name.ShouldBe(textureName);
            records[k].TextureSlot.ShouldBe((byte)k, customMessage: "the record index is the GL texture unit");
        }
    }

    /// <summary>
    /// The DirectX 12 mirror of <see cref="SharedSamplerState_EmitsOneSamplerRecordPerTexture"/>.
    /// DX12 was not in that theory and fell through to the sampler-keyed branch, so this shape
    /// emitted ONE record and <c>Lightmap</c> was never bound — silently, with no diagnostic.
    /// </summary>
    [Fact]
    [Trait("Platform", "DirectX12")]
    public async Task DirectX12_SharedSamplerState_EmitsOneRecordPerTexture()
    {
        using var cts = Cts();

        var result = await new EffectCompiler().CompileAsync(SharedSamplerShader, new CompilerOptions
        {
            Target = PlatformTarget.DirectX12,
            SourceFileName = "SharedSamplerDx12.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var ps = reader.Shaders.Single(s => !s.IsVertex);
        var records = reader.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();

        records.Count().ShouldBe(2, customMessage: "two textures are sampled, so both texture slots must be bindable even though they " +
            "share one SamplerState");
        records.Select(s => s.TextureSlot).ShouldBe(new[] {(byte)0, (byte)1});
        records.Select(s => reader.Parameters[s.Parameter].Name).ShouldBe(new[] {"DiffuseMap", "Lightmap"});
    }
}
