#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Tests.Fx2;
using ShadowDusk.Integration.Tests.Tests;
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Compile-level regression coverage for the vendored, real-world third-party
/// <c>.fx</c> corpus under <c>tests/fixtures/shaders/third-party/</c> (Nez, MIT —
/// see that directory's <c>LICENSE</c> / <c>NOTICE.md</c> and the "Third-party shader
/// corpus" section of <c>docs/test-shader-corpus.md</c>).
///
/// <para>These are real shipping MonoGame post-process shaders, used here to broaden
/// the corpus along the language features the project-owned fixtures under-covered:
/// a literal-bounded <c>for</c>-loop (GaussianBlur), helper functions called from the
/// entry (BloomCombine's <c>adjustSaturation</c>, PixelGlitch's <c>hash11</c>),
/// relational-driven <c>if</c> branches (Twist, Letterbox), bloom passes, UV
/// distortion, vignette, edge-detect, VPOS + float-modulo scanlines, a two-technique
/// VS+PS effect (Reflection), and a 1-D-LUT palette swap (PaletteCycler).</para>
///
/// <para><b>Each shader is compile-asserted only on the delivery targets it actually
/// compiles on</b> — the classification is recorded in the directory's NOTICE.md and
/// in docs/test-shader-corpus.md. A target a shader is NOT tested on is excluded for a
/// documented, legitimate shader-model reason (e.g. <c>int</c> uniforms and
/// <c>tex1D</c> are not on the MonoGame-GL path). <c>Noise.fx</c> (a uniform named
/// <c>noise</c>, a GLSL reserved word) USED to be a tracked GL exception — Phase 45 B10
/// fixed it: the OpenGL cbuffer/parameter join now falls back to an offset bridge when
/// SPIRV-Cross's <c>noise</c>→<c>_noise</c> rename breaks the name match, so
/// <c>Noise.fx</c> compiles on GL too and is in the GL set below.</para>
///
/// <para><b>Scope:</b> a green compile to a well-formed container is the bar here
/// (this is COMPILE regression coverage). It is NOT a pixel-equivalence claim to
/// <c>mgfxc</c>/<c>fxc</c> — that bar is carried by the <c>validation/*</c> render
/// drivers and there is no committed golden for these vendored shaders. In particular
/// the VPOS shaders (Letterbox, SpriteLines) compile on every target but their
/// VPOS->gl_FragCoord behavior across GL/DX is deliberately NOT asserted equivalent.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ThirdPartyShaderCorpusTests
{
    private const byte ProfileOpenGL    = 0; // MgfxProfile.OpenGL
    private const byte ProfileDirectX11 = 1; // MgfxProfile.DirectX11

    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(60);

    private const string Root = "third-party/Nez/";

    // -------------------------------------------------------------------------
    // Per-target applicable sets (see NOTICE.md for the classification rationale).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shaders that compile on the MonoGame-GL target. The all-runtime subset PLUS
    /// Noise (Phase 45 B10 fixed the reserved-word <c>noise</c>→<c>_noise</c> GL
    /// join). Crosshatch/PaletteCycler/Reflection still have legitimate GL SM limits.
    /// </summary>
    public static TheoryData<string> OpenGLShaders() => new()
    {
        Root + "Bevels.fx",
        Root + "BloomCombine.fx",
        Root + "BloomExtract.fx",
        Root + "GaussianBlur.fx",       // literal-bounded for-loop over array uniforms
        Root + "HeatDistortion.fx",
        Root + "Letterbox.fx",          // VPOS (compiles; render-equivalence not claimed)
        Root + "Noise.fx",              // B10: uniform 'noise' (GLSL reserved word) — fixed on GL
        Root + "PixelGlitch.fx",        // helper fn hash11()
        Root + "SpriteBlinkEffect.fx",
        Root + "SpriteLines.fx",        // VPOS + float % (compiles; render-equivalence not claimed)
        Root + "Twist.fx",              // relational if(dist<radius) + sin/cos
        Root + "Vignette.fx",
    };

    /// <summary>
    /// Shaders that compile on the MonoGame-DX target — the all-runtime subset PLUS the
    /// DX-capable ones GL/FNA can't take: Crosshatch (int uniform), Reflection (2-tech
    /// VS+PS). (Noise also compiles on GL since the Phase 45 B10 fix, but is listed in
    /// the GL set; it is included here too as an ordinary DX shader.)
    /// </summary>
    public static TheoryData<string> DirectXShaders() => new()
    {
        Root + "Bevels.fx",
        Root + "BloomCombine.fx",
        Root + "BloomExtract.fx",
        Root + "GaussianBlur.fx",
        Root + "HeatDistortion.fx",
        Root + "Letterbox.fx",
        Root + "PixelGlitch.fx",
        Root + "SpriteBlinkEffect.fx",
        Root + "SpriteLines.fx",
        Root + "Twist.fx",
        Root + "Vignette.fx",
        Root + "Crosshatch.fx",         // int uniform + VPOS + float % + nested if
        Root + "Noise.fx",              // helper fn rand(); compiles on DX (and GL since B10)
        Root + "Reflection.fx",         // two techniques, each VS+PS
    };

    /// <summary>
    /// Shaders that compile on the FNA (D3D9 fx_2_0, vkd3d SM &lt;= 3) target — the
    /// all-runtime subset PLUS Crosshatch (SM3 native) and PaletteCycler (tex1D native).
    /// Reflection is excluded (its int/relational construct hits the vkd3d 1.17 SM3 gap).
    /// </summary>
    public static TheoryData<string> FnaShaders() => new()
    {
        Root + "Bevels.fx",
        Root + "BloomCombine.fx",
        Root + "BloomExtract.fx",
        Root + "GaussianBlur.fx",
        Root + "HeatDistortion.fx",
        Root + "Letterbox.fx",
        Root + "PixelGlitch.fx",
        Root + "SpriteBlinkEffect.fx",
        Root + "SpriteLines.fx",
        Root + "Twist.fx",
        Root + "Vignette.fx",
        Root + "Crosshatch.fx",         // int uniform + VPOS + % compile natively at SM3
        Root + "Noise.fx",              // helper fn rand(); 'noise' is an ordinary SM3 const here
        Root + "PaletteCycler.fx",      // tex1D / sampler1D — FNA compiles it natively
    };

    // -------------------------------------------------------------------------
    // OpenGL — the MonoGame-GL / KNI delivery target.
    // -------------------------------------------------------------------------

    [Theory]
    [Trait("Platform", "OpenGL")]
    [MemberData(nameof(OpenGLShaders))]
    public async Task ThirdPartyShader_OpenGL_CompilesToValidMgfx(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await TestHelpers.CompileFixtureAsync(fx, "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"vendored '{fx}' is classified GL-compilable; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty(because: "a successful compile must emit output bytes");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.Signature.Should().Be("MGFX");
        reader.MgfxVersion.Should().Be(10);
        reader.ProfileId.Should().Be(ProfileOpenGL);
        reader.TotalShaderBlobCount.Should().BeGreaterThan(0, because: "each shader declares a pixel shader pass");
    }

    // -------------------------------------------------------------------------
    // DirectX_11 — the MonoGame-DX delivery target.
    // -------------------------------------------------------------------------

    [Theory]
    [Trait("Platform", "DirectX_11")]
    [MemberData(nameof(DirectXShaders))]
    public async Task ThirdPartyShader_DirectX11_CompilesToValidMgfx(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await TestHelpers.CompileFixtureAsync(fx, "DirectX_11", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"vendored '{fx}' is classified DX-compilable; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty(because: "a successful compile must emit output bytes");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.Signature.Should().Be("MGFX");
        reader.MgfxVersion.Should().Be(10);
        reader.ProfileId.Should().Be(ProfileDirectX11);
        reader.TotalShaderBlobCount.Should().BeGreaterThan(0, because: "each shader declares at least one shader pass");
    }

    // -------------------------------------------------------------------------
    // FNA — the additive D3D9 fx_2_0 delivery target (vkd3d SM <= 3 + Fx2EffectWriter).
    // [FnaTheory] skips when the vkd3d native is absent locally; runs (and fails
    // loudly) in CI where SHADOWDUSK_REQUIRE_VKD3D is set.
    // -------------------------------------------------------------------------

    [FnaTheory]
    [Trait("Platform", "FNA")]
    [MemberData(nameof(FnaShaders))]
    public async Task ThirdPartyShader_Fna_CompilesToValidFx2Binary(string fx)
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
            because: $"vendored '{fx}' is classified FNA-compilable (SM <= 3); " +
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
