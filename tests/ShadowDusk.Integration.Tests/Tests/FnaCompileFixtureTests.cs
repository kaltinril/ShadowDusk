#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Tests.Fx2;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 39 integration coverage for the FNA fx_2_0 target: <c>.fx</c> source in,
/// D3D9 Effects Framework binary (<c>0xFEFF0901</c>, <c>.fxb</c>) out, via the real
/// pipeline (vkd3d-shader SM ≤ 3 backend + <c>Fx2EffectWriter</c>), validated with the
/// MojoShader-rule <see cref="Fx2BinaryValidator"/> (source-linked from
/// <c>ShadowDusk.Core.Tests</c>).
///
/// Evidence-ladder note (docs/the-purpose.md): these are rung 1 (compiles) and rung 2
/// (structurally well-formed per MojoShader's parse rules, calibrated against the real
/// fxc goldens in <c>tests/fixtures/golden/FNA/</c>). Real MojoShader parse (rung 3) and
/// real FNA render (rung 4) are NOT covered here — proxies, not the bar.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "FNA")]
public sealed class FnaCompileFixtureTests
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(30);

    // -------------------------------------------------------------------------
    // Helpers (FNA-specific: TestHelpers.CompileFixtureAsync is .mgfx-shaped, so
    // these call EffectCompiler in-process directly and return the raw Result)
    // -------------------------------------------------------------------------

    private static string GoldenPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "FNA", fileName);

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileFnaFileAsync(
        string sourcePath, CancellationToken ct)
    {
        string source = await File.ReadAllTextAsync(sourcePath, ct);
        return await CompileFnaSourceAsync(source, sourcePath, ct);
    }

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileFnaSourceAsync(
        string source, string? sourcePath, CancellationToken ct)
    {
        var options = new CompilerOptions
        {
            Target         = PlatformTarget.Fna,
            SourceFileName = sourcePath,
            AdditionalIncludePaths = sourcePath is null
                ? Array.Empty<string>()
                : new[] { Path.GetDirectoryName(sourcePath)! },
        };

        var compiler = new EffectCompiler();
        return await compiler.CompileAsync(source, options, ct);
    }

    private static string DescribeErrors(Result<CompiledShader, ShaderError[]> result) =>
        result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))
            : "<none>";

    // -------------------------------------------------------------------------
    // A. SM3 corpus — every fixture compiles to a MojoShader-parseable fx_2_0
    // -------------------------------------------------------------------------

    public static TheoryData<string> Sm3Corpus() => new()
    {
        // PS-only effects
        "Grayscale.fx",
        "Invert.fx",
        "Sepia.fx",
        "Saturate.fx",
        "Pixelated.fx",
        "Scanlines.fx",
        "Fading.fx",
        "Dots.fx",
        "Dissolve.fx",
        "TintShader.fx",
        "BasicShader.fx",
        "BlendShader.fx",
        "ClipShader.fx",
        "ClipShaderNew.fx",
        "ClipShaderSpriteTarget.fx",
        "MultiTexture.fx",
        "MultiTextureOverlay.fx",
        "SimpleLightShader.fx",
        "SpriteAlphaTest.fx",
        "Teleport.fx",
        // VS+PS effects
        "PolygonLight.fx",
        "VertexAndPixel.fx",
        "VsTransformColorTexture.fx",
        "FnaMultiPassStates.fx",
        // Project-owned example shaders (known provenance; legacy SM3 surface)
        "examples/ExBareSamplerTex2D.fx",
        "examples/ExSamplerStateUniform.fx",
        "examples/ExDualTexture.fx",
        "examples/ExLegacyTextureDiscard.fx",
    };

    [FnaTheory]
    [MemberData(nameof(Sm3Corpus))]
    public async Task Corpus_Fna_CompilesToValidFx2Binary(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileFnaFileAsync(TestHelpers.FixturePath(fx), cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: $"'{fx}' is in the SM ≤ 3 FNA corpus and must compile; errors: {DescribeErrors(result)}");

        Func<Fx2ParsedEffect> parse = () => Fx2BinaryValidator.Parse(result.Value.Data);
        Fx2ParsedEffect effect = parse.Should().NotThrow(
            because: $"'{fx}' must produce an fx_2_0 binary that satisfies every MojoShader parse rule").Subject;

        effect.Techniques.Should().NotBeEmpty(
            because: $"'{fx}' declares at least one technique");
        effect.Shaders.Should().NotBeEmpty(
            because: $"'{fx}' declares at least one compiled shader pass");

        foreach (Fx2ParsedShader shader in effect.Shaders)
        {
            uint kind = shader.VersionToken >> 16;
            (kind is 0xFFFF or 0xFFFE).Should().BeTrue(
                because: $"shader version token 0x{shader.VersionToken:X8} in '{fx}' must be a D3D9 " +
                         "pixel (0xFFFF) or vertex (0xFFFE) token stream");
            (shader.VersionToken & 0xFFFF).Should().BeLessThanOrEqualTo(0x0300u,
                because: $"shader version token 0x{shader.VersionToken:X8} in '{fx}' must be SM ≤ 3 " +
                         "(MojoShader's hard ceiling)");
        }
    }

    // -------------------------------------------------------------------------
    // A2. Multi-pass / multi-technique structure — FnaMultiPassStates.fx is the
    //     one fixture combining two techniques, a two-pass technique, in-pass
    //     render states, a VS+PS pass, a float4 uniform, and a sampler→texture
    //     binding; pin the full validator-observed structure.
    // -------------------------------------------------------------------------

    [FnaFact]
    public async Task FnaMultiPassStates_Fna_StructureRoundTripsThroughValidator()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileFnaFileAsync(
            TestHelpers.FixturePath("FnaMultiPassStates.fx"), cts.Token);
        result.IsSuccess.Should().BeTrue(because: $"errors: {DescribeErrors(result)}");

        Fx2ParsedEffect effect = Fx2BinaryValidator.Parse(result.Value.Data);

        // Two techniques, declared names, source order.
        effect.Techniques.Select(t => t.Name).Should().Equal("MultiPass", "SinglePass");
        // The first technique declares two passes, in source order.
        effect.Techniques[0].Passes.Select(p => p.Name).Should().Equal("Blend", "Solid");
        effect.Techniques[1].Passes.Select(p => p.Name).Should().Equal("Only");

        // The state-bearing pass: render states in the D3D9 value domain
        // (docs/fx2-binary-format.md §8.2) plus both shader-binding states.
        Fx2ParsedPass blend = effect.Techniques[0].Passes[0];
        blend.States
            .Where(s => s.DwordValue is not null)
            .Select(s => (s.Operation, s.DwordValue!.Value))
            .Should().BeEquivalentTo(new[]
            {
                (13, 1u), // AlphaBlendEnable = TRUE   → D3DRS_ALPHABLENDENABLE, TRUE
                (6, 5u),  // SrcBlend  = SRCALPHA      → D3DRS_SRCBLEND,  D3DBLEND_SRCALPHA
                (7, 6u),  // DestBlend = INVSRCALPHA   → D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA
                (8, 1u),  // CullMode  = NONE          → D3DRS_CULLMODE,  D3DCULL_NONE
            },
            because: "each in-pass render state must arrive as its (op, D3D9 value) pair");
        blend.States.Should().ContainSingle(s => s.Operation == 146,
            because: "the pass compiles a vertex shader (VertexShader state)");
        blend.States.Should().ContainSingle(s => s.Operation == 147,
            because: "the pass compiles a pixel shader (PixelShader state)");
        // Full deterministic state order: render states first (the large-object shader
        // records back-reference the 146/147 state indices, so shader states come last).
        blend.States.Select(s => s.Operation).Should().Equal(13, 6, 7, 8, 146, 147);

        // The other passes are PS-only and state-free.
        effect.Techniques[0].Passes[1].States.Select(s => s.Operation).Should().Equal(147);
        effect.Techniques[1].Passes[0].States.Select(s => s.Operation).Should().Equal(147);

        // Sampler→texture name map (the usage==1 large-object record).
        effect.SamplerTextureMap.Should().HaveCount(1)
            .And.ContainKey("TexSampler").WhoseValue.Should().Be("SceneTexture");

        // One embedded shader object per compiled pass-stage binding, no sharing:
        // Blend (VS+PS) + Solid (PS) + Only (PS) = 4.
        effect.Shaders.Should().HaveCount(4,
            because: "every pass-stage binding embeds its own shader object");
        effect.Shaders.Count(s => s.Stage == ShaderStage.Vertex).Should().Be(1);
        effect.Shaders.Count(s => s.Stage == ShaderStage.Pixel).Should().Be(3);
    }

    // -------------------------------------------------------------------------
    // B. Determinism — same source, two compiler instances, byte-identical bytes
    // -------------------------------------------------------------------------

    [FnaFact]
    public async Task Grayscale_Fna_CompiledTwice_IsByteIdentical()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        string path = TestHelpers.FixturePath("Grayscale.fx");
        var first  = await CompileFnaFileAsync(path, cts.Token);
        var second = await CompileFnaFileAsync(path, cts.Token);

        first.IsSuccess.Should().BeTrue(because: $"errors: {DescribeErrors(first)}");
        second.IsSuccess.Should().BeTrue(because: $"errors: {DescribeErrors(second)}");

        second.Value.Data.Should().Equal(first.Value.Data,
            because: "same shader source + same target must produce byte-identical output " +
                     "(Core Design Constraint 3: ShadowDusk's own determinism)");
    }

    // -------------------------------------------------------------------------
    // C. Golden structural cross-check (rung-2 calibration) — our .fxb vs fxc's
    // -------------------------------------------------------------------------

    [FnaTheory]
    [InlineData("minimal")]
    [InlineData("textured")]
    public async Task Golden_Fna_OutputStructurallyEquivalentToFxc(string name)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileFnaFileAsync(GoldenPath($"{name}.fx"), cts.Token);
        result.IsSuccess.Should().BeTrue(
            because: $"golden source '{name}.fx' must compile for FNA; errors: {DescribeErrors(result)}");

        byte[] goldenBytes = await File.ReadAllBytesAsync(GoldenPath($"{name}.fxb"), cts.Token);
        Fx2ParsedEffect ours   = Fx2BinaryValidator.Parse(result.Value.Data);
        Fx2ParsedEffect golden = Fx2BinaryValidator.Parse(goldenBytes);

        // Structural equivalence, never byte equality — different compilers (vkd3d vs fxc)
        // legitimately produce different bytecode and layout (docs/the-purpose.md).
        ours.Parameters.Select(p => (p.Name, p.Class, p.Type)).Should().BeEquivalentTo(
            golden.Parameters.Select(p => (p.Name, p.Class, p.Type)),
            because: "the parameter table (name/class/type) must match fxc's so FNA binds identically");

        ours.Techniques.Select(t => t.Name).Should().Equal(
            golden.Techniques.Select(t => t.Name),
            because: "technique names and order must match fxc's");

        for (int t = 0; t < golden.Techniques.Count; t++)
        {
            Fx2ParsedTechnique ot = ours.Techniques[t];
            Fx2ParsedTechnique gt = golden.Techniques[t];

            ot.Passes.Select(p => p.Name).Should().Equal(
                gt.Passes.Select(p => p.Name),
                because: $"pass names of technique '{gt.Name}' must match fxc's");

            for (int p = 0; p < gt.Passes.Count; p++)
            {
                ot.Passes[p].States.Select(s => s.Operation).Should().BeEquivalentTo(
                    gt.Passes[p].States.Select(s => s.Operation),
                    because: $"pass '{gt.Passes[p].Name}' must carry the same state operations as fxc's output");
            }
        }

        ours.SamplerTextureMap.Should().BeEquivalentTo(golden.SamplerTextureMap,
            because: "the sampler→texture name map drives FNA's texture binding and must match fxc's");

        ours.Shaders.Select(s => (s.Stage, s.VersionToken)).Should().BeEquivalentTo(
            golden.Shaders.Select(s => (s.Stage, s.VersionToken)),
            because: "the same shader stages at the same SM versions must be embedded " +
                     "(the literal ps_2_0 profile in the source is honored as written)");
    }

    // -------------------------------------------------------------------------
    // D. Failure paths — SM4-style sources fail loudly, never silently degrade
    // -------------------------------------------------------------------------

    [FnaFact]
    public async Task Textured_Fna_LiteralSm4Profile_FailsWithSd0300()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        // The root textured.fx declares literal `compile vs_4_0` / `compile ps_4_0` —
        // above MojoShader's vs_3_0/ps_3_0 ceiling, so the FNA profile policy rejects it.
        var result = await CompileFnaFileAsync(TestHelpers.FixturePath("textured.fx"), cts.Token);

        result.IsFailure.Should().BeTrue(
            because: "a literal SM4+ profile under the FNA target must fail loudly, not silently degrade");
        result.Error.Should().Contain(e => e.Code == "SD0300",
            because: $"the documented FNA profile-policy error is SD0300; got: {DescribeErrors(result)}");
        result.Error.First(e => e.Code == "SD0300").Message.Should().Contain("Shader Model 1–3",
            because: "the diagnostic must tell the user what the FNA target supports");
    }

    [FnaFact]
    public async Task LiteralLevel91Profile_Fna_FailsWithSd0300()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        // ps_4_0_level_9_1 (the MonoGame Reach profile) is NOT in KnownProfiles, but its
        // SM major digit is 4 — the FNA profile policy is a shape test precisely so such
        // literals still classify as SM4+ and fail loudly instead of silently downgrading.
        // The body is plain D3D9-style HLSL: only the profile token is at issue.
        const string source = """
            float4 PSMain() : COLOR
            {
                return float4(1, 1, 1, 1);
            }

            technique T
            {
                pass P
                {
                    PixelShader = compile ps_4_0_level_9_1 PSMain();
                }
            }
            """;

        var result = await CompileFnaSourceAsync(source, sourcePath: null, cts.Token);

        result.IsFailure.Should().BeTrue(
            because: "a literal ps_4_0_level_9_1 profile is above MojoShader's SM3 ceiling");
        result.Error.Should().Contain(e => e.Code == "SD0300",
            because: $"the documented FNA profile-policy error is SD0300; got: {DescribeErrors(result)}");
        result.Error.First(e => e.Code == "SD0300").Message.Should().Contain("ps_4_0_level_9_1",
            because: "the diagnostic must name the offending profile as written");
    }

    [FnaFact]
    public async Task DuplicateRenderStateKey_Fna_LastAssignmentWins()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        // fxc semantics: assigning the same state twice in a pass is legal and the last
        // assignment wins (CW = D3DCULL_CW = 2, then NONE = D3DCULL_NONE = 1).
        const string source = """
            float4 PSMain() : COLOR
            {
                return float4(0, 0, 1, 1);
            }

            technique T
            {
                pass P
                {
                    CullMode = CW;
                    CullMode = NONE;
                    PixelShader = compile ps_2_0 PSMain();
                }
            }
            """;

        var result = await CompileFnaSourceAsync(source, sourcePath: null, cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: $"a duplicated render-state key must compile (last wins, fxc semantics); " +
                     $"errors: {DescribeErrors(result)}");

        Fx2ParsedPass pass = Fx2BinaryValidator.Parse(result.Value.Data).Techniques[0].Passes[0];
        var cullStates = pass.States.Where(s => s.Operation == 8).ToList();
        cullStates.Should().HaveCount(1,
            because: "the duplicated CullMode must collapse to one state record");
        cullStates[0].DwordValue.Should().Be(1u,
            because: "the LAST assignment (NONE = D3DCULL_NONE = 1) wins, not CW (2)");
    }

    // DeferredSprite.fx and ForwardLighting.fx hit the documented vkd3d 1.17 construct
    // gap (plan/DONE/PHASE-39-fna-fx2-output-target.md, "Known limitations"): int-typed
    // ternary in `clip((c < x) ? -1 : 1)` is unimplemented at SM ≤ 3 (vkd3d's E5017).
    //
    // Empirically pinned (2026-06-09, vkd3d 1.17 in-process): the surfaced ShaderError is
    // File=<full fixture path>, Code='X0000', Message='Shader compilation failed' — the
    // E5017 detail appears only in vkd3d's own debug stderr because its messages blob
    // comes back empty for this failure, so we deliberately do NOT pin 'E5017' or the
    // exact code/message wording here. The stable, load-bearing contract is: the compile
    // FAILS (never silently degrades or substitutes a compiler) and the diagnostic names
    // the offending source file.
    [FnaTheory]
    [InlineData("DeferredSprite.fx")]
    [InlineData("ForwardLighting.fx")]
    public async Task IntTernaryClip_Fna_FailsLoudlyOnVkd3dGap(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileFnaFileAsync(TestHelpers.FixturePath(fx), cts.Token);

        result.IsFailure.Should().BeTrue(
            because: $"'{fx}' uses an int-typed ternary, a known vkd3d 1.17 SM ≤ 3 gap");
        result.Error.Should().NotBeEmpty(because: "the failure must surface diagnostics");
        result.Error.Should().Contain(e => Path.GetFileName(e.File) == fx,
            because: "the diagnostic must name the offending source file");
        result.Error.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Message),
            because: "every surfaced error must carry a message, not be swallowed");
    }

    [FnaFact]
    public async Task InlineSm4StyleSource_Fna_FailsLoudly()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        // SM4-style HLSL (Texture2D + SamplerState + .Sample) with a literal ps_4_0
        // profile: the FNA target must reject it outright.
        const string sm4Source = """
            Texture2D t;
            SamplerState s;

            float4 PSMain(float2 uv : TEXCOORD0) : SV_Target
            {
                return t.Sample(s, uv);
            }

            technique T
            {
                pass P
                {
                    PixelShader = compile ps_4_0 PSMain();
                }
            }
            """;

        var result = await CompileFnaSourceAsync(sm4Source, sourcePath: null, cts.Token);

        result.IsFailure.Should().BeTrue(
            because: "SM4-style source must fail loudly under the FNA target, never silently degrade");
        result.Error.Should().NotBeEmpty(because: "the failure must carry diagnostics");
    }

    // -------------------------------------------------------------------------
    // E. CTAB-binding sanity — the MojoShader strcmp-bind invariant, asserted
    //    explicitly for documentation value (the validator also enforces it)
    // -------------------------------------------------------------------------

    [FnaFact]
    public async Task TintShader_Fna_CtabConstantsBindToParametersByExactName()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        // TintShader.fx has a float4 uniform (TintColor) and a texture+sampler pair —
        // both CTAB constant kinds (FLOAT4 and SAMPLER register sets).
        var result = await CompileFnaFileAsync(TestHelpers.FixturePath("TintShader.fx"), cts.Token);
        result.IsSuccess.Should().BeTrue(because: $"errors: {DescribeErrors(result)}");

        Fx2ParsedEffect effect = Fx2BinaryValidator.Parse(result.Value.Data);

        var parameterNames = effect.Parameters.Select(p => p.Name).ToList();
        foreach (Fx2ParsedShader shader in effect.Shaders)
        {
            shader.CtabConstantNames.Should().BeSubsetOf(parameterNames,
                because: "MojoShader binds every CTAB constant to an effect parameter by exact " +
                         "strcmp name match — a miss is release-mode memory corruption in FNA");
        }
    }
}
