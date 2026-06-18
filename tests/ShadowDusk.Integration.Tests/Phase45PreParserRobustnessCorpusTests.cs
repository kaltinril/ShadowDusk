#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Tests.Fx2;
using ShadowDusk.Integration.Tests.Tests;
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Permanent regression coverage for the Phase 45 FX pre-parser robustness fixes
/// (the dropped-operator bug class catalogued in
/// <c>plan/PHASE-45-fx-preparser-robustness.md</c>):
/// <list type="bullet">
///   <item><b>B2</b> — a <c>sampler S = sampler_state { … }</c> used through the
///   MODERN <c>T.Sample(S, uv)</c> method (not <c>tex2D</c>) was erased, so DXC
///   failed with "undeclared identifier 'S'". Now rewritten to a passthrough
///   <c>SamplerState S;</c>. (<c>ExModernSamplerState.fx</c> — GL + DX; <c>.Sample</c>
///   is SM4 method syntax so FNA is N/A.)</item>
///   <item><b>B3</b> — <c>ColorWriteEnable = Red | Green | Blue;</c> mis-parsed
///   (the lexer drops <c>|</c>). (<c>ExColorWriteMask.fx</c> — GL + DX + FNA.)</item>
///   <item><b>B8</b> — <c>sampler S : register(s0) = sampler_state { … };</c> (register
///   clause before <c>=</c>) leaked the state block. (<c>ExSamplerRegisterState.fx</c>
///   — GL + DX + FNA.)</item>
///   <item><b>B9</b> — a trailing sampler-level annotation
///   <c>sampler2D S = sampler_state { … } &lt; … &gt;;</c> failed FX0001.
///   (<c>ExSamplerAnnotation.fx</c> — GL + DX + FNA.)</item>
///   <item><b>B4</b> — a legacy <c>texture T &lt; …annotation… &gt;;</c> leaked the
///   trailing <c>&gt;;</c> (the inner annotation <c>;</c> stopped the consume early)
///   → DXC "expected unqualified-id". Now the consume tracks angle-bracket depth.
///   (<c>ExLegacyTextureAnnotation.fx</c> — GL + DX + FNA.)</item>
///   <item><b>B5</b> — a resource VARIABLE named <c>Texture</c>
///   (<c>Texture2D Texture : register(t0);</c>) was corrupted to
///   <c>Texture2D Texture2D register;</c>. The legacy-texture rewrite (RewriteToSm4
///   only) now declines when the keyword is in name position.
///   (<c>ExTextureNamedTexture.fx</c> — GL + DX; the fixture's <c>.Sample</c> is SM4
///   method syntax so the FNA SM &lt;= 3 path is N/A.)</item>
///   <item><b>B6</b> — a VERTEX shader whose return semantic is <c>: COLOR</c> (a VS
///   that writes POSITION via an <c>out</c> param) had its <c>: COLOR</c> wrongly
///   rewritten to the PS-only <c>: SV_Target</c>. The rewrite is now deferred and
///   skips VS entry points. (<c>ExVsColorReturn.fx</c> — GL + DX + FNA.)</item>
///   <item><b>B7</b> — an array-indexed relational with an assignment in a ternary
///   arm inside a function body (<c>Thresholds[i] &lt; x ? acc = w : acc;</c>) was
///   misread as an FX annotation → FX0001 (the issue-#106 residual). The global
///   annotation strip is now gated on brace depth 0.
///   (<c>ExArrayTernaryAssign.fx</c> — GL + DX + FNA.)</item>
/// </list>
///
/// <para>Each fixture is compile-asserted on every applicable delivery target. Scope:
/// a green compile to a well-formed container is the DIRECT proof for these bugs (they
/// were PARSE / mis-rewrite failures). It is NOT a pixel-equivalence claim — that bar is
/// carried by the <c>validation/*</c> render drivers and is the follow-up (no committed
/// <c>mgfxc</c>/<c>fxc</c> golden exists for these fixtures yet).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Phase45PreParserRobustnessCorpusTests
{
    private const byte ProfileOpenGL    = 0; // MgfxProfile.OpenGL
    private const byte ProfileDirectX11 = 1; // MgfxProfile.DirectX11

    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Every Phase-45 fixture compiles on the MGFX targets (GL + DX).</summary>
    public static TheoryData<string> MgfxFixtures() => new()
    {
        "examples/ExModernSamplerState.fx",   // B2: sampler_state used via .Sample (SM4 method)
        "examples/ExColorWriteMask.fx",       // B3: ColorWriteEnable = Red | Green | Blue
        "examples/ExSamplerRegisterState.fx", // B8: register(s0) before = sampler_state
        "examples/ExSamplerAnnotation.fx",    // B9: trailing sampler-level annotation
        "examples/ExLegacyTextureAnnotation.fx", // B4: legacy texture < annotation >;
        "examples/ExTextureNamedTexture.fx",  // B5: resource variable named 'Texture'
        "examples/ExVsColorReturn.fx",        // B6: VS function-return ': COLOR'
        "examples/ExArrayTernaryAssign.fx",   // B7: array-indexed relational + ternary-assign
    };

    /// <summary>
    /// The all-runtime (SM3 / fx_2_0) subset of the Phase-45 fixtures — these also
    /// compile on the FNA target. B2 and B5 are excluded: both use the SM4
    /// <c>Texture.Sample(…)</c> method, which the FNA SM &lt;= 3 path does not use
    /// (it uses the <c>tex2D</c> form). B6's VS <c>: COLOR</c> is a valid SM3 output
    /// semantic on FNA and passes through to vkd3d unchanged.
    /// </summary>
    public static TheoryData<string> AllRuntimeFixtures() => new()
    {
        "examples/ExColorWriteMask.fx",
        "examples/ExSamplerRegisterState.fx",
        "examples/ExSamplerAnnotation.fx",
        "examples/ExLegacyTextureAnnotation.fx", // B4: passes through to vkd3d (annotation stripped)
        "examples/ExVsColorReturn.fx",        // B6: ': COLOR' is a valid SM3 output semantic on FNA
        "examples/ExArrayTernaryAssign.fx",   // B7: relational/ternary in body, all-runtime
    };

    // -------------------------------------------------------------------------
    // OpenGL — the MonoGame-GL / KNI delivery target.
    // -------------------------------------------------------------------------

    [Theory]
    [Trait("Platform", "OpenGL")]
    [MemberData(nameof(MgfxFixtures))]
    public async Task Phase45Fixture_OpenGL_CompilesToValidMgfx(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await TestHelpers.CompileFixtureAsync(fx, "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"'{fx}' (Phase 45 regression) must compile for OpenGL; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty(because: "a successful compile must emit output bytes");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.Signature.Should().Be("MGFX");
        reader.MgfxVersion.Should().Be(10);
        reader.ProfileId.Should().Be(ProfileOpenGL);
        reader.TotalShaderBlobCount.Should().BeGreaterThan(0, because: "each fixture declares a pixel shader pass");
    }

    // -------------------------------------------------------------------------
    // DirectX_11 — the MonoGame-DX delivery target.
    // -------------------------------------------------------------------------

    [Theory]
    [Trait("Platform", "DirectX_11")]
    [MemberData(nameof(MgfxFixtures))]
    public async Task Phase45Fixture_DirectX11_CompilesToValidMgfx(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await TestHelpers.CompileFixtureAsync(fx, "DirectX_11", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"'{fx}' (Phase 45 regression) must compile for DirectX_11; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty(because: "a successful compile must emit output bytes");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.Signature.Should().Be("MGFX");
        reader.MgfxVersion.Should().Be(10);
        reader.ProfileId.Should().Be(ProfileDirectX11);
        reader.TotalShaderBlobCount.Should().BeGreaterThan(0, because: "each fixture declares a pixel shader pass");
    }

    // -------------------------------------------------------------------------
    // FNA — the additive D3D9 fx_2_0 delivery target (vkd3d SM <= 3 + Fx2EffectWriter).
    // [FnaTheory] skips when the vkd3d native is absent locally; runs (and fails
    // loudly) in CI where SHADOWDUSK_REQUIRE_VKD3D is set.
    // -------------------------------------------------------------------------

    [FnaTheory]
    [Trait("Platform", "FNA")]
    [MemberData(nameof(AllRuntimeFixtures))]
    public async Task Phase45Fixture_Fna_CompilesToValidFx2Binary(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        string source = await File.ReadAllTextAsync(TestHelpers.FixturePath(fx), cts.Token);
        var options = new CompilerOptions
        {
            Target         = PlatformTarget.Fna,
            SourceFileName = TestHelpers.FixturePath(fx),
        };

        var result = await new EffectCompiler().CompileAsync(source, options, cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: $"'{fx}' (Phase 45 regression) is in the SM <= 3 FNA subset and must compile; " +
                     $"errors: {(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>")}");

        Func<Fx2ParsedEffect> parse = () => Fx2BinaryValidator.Parse(result.Value.Data);
        Fx2ParsedEffect effect = parse.Should().NotThrow(
            because: $"'{fx}' must produce an fx_2_0 binary that satisfies every MojoShader parse rule").Subject;

        effect.Techniques.Should().NotBeEmpty(because: $"'{fx}' declares at least one technique");
        effect.Shaders.Should().NotBeEmpty(because: $"'{fx}' declares at least one compiled shader pass");

        foreach (Fx2ParsedShader shader in effect.Shaders)
        {
            (shader.VersionToken & 0xFFFF).Should().BeLessThanOrEqualTo(0x0300u,
                because: $"shader version token 0x{shader.VersionToken:X8} in '{fx}' must be SM <= 3 (MojoShader's hard ceiling)");
        }
    }
}
