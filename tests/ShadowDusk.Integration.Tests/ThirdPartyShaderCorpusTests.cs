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
/// <para>Phase 49 adds two more vendored sets the Gum/Apos.Shapes ecosystem actually
/// ships (requested by vchelaru, Gum's author): <c>third-party/Apos.Shapes/</c>
/// (Gum's SDF shape renderer — MIT) and <c>third-party/Gum/</c> (Gum's own sample
/// shaders — MIT). These are wired in below with FULL inline relative paths (the
/// <c>Root</c> constant is Nez-specific). Their per-target classification came from an
/// actual compile probe, recorded in each directory's NOTICE.md. One of them,
/// <c>Gum/FnaSample-Shader.fx</c>, fails on every target because of the
/// <c>TECHNIQUE()</c> macro idiom = Phase 41 GAP-1 (macro-defined techniques are
/// invisible to <c>FxPreParser</c>); it is pinned as a KNOWN-FAILURE fact below so the
/// gap is exercised and flips loudly when GAP-1 is fixed.</para>
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
        // Phase 49 (full inline paths — Root is Nez-specific):
        "third-party/Apos.Shapes/apos-shapes.fx",        // Gum SDF renderer: VS+PS, 10 TEXCOORDs, Newton for-loop, discard
        "third-party/Gum/MonoGameInCode-Grayscale.fx",   // vs/ps_4_0_level_9_1, Texture2D+sampler2D, dot-luminance
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
        // Phase 49 (full inline paths — Root is Nez-specific):
        "third-party/Apos.Shapes/apos-shapes.fx",        // Gum SDF renderer (DX SM5 via vkd3d)
        "third-party/Gum/MonoGameInCode-Grayscale.fx",   // vs/ps_4_0_level_9_1 grayscale
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
        // Phase 49 (full inline paths — Root is Nez-specific):
        "third-party/Gum/KniInCode-Shader.fx",           // legacy D3D9 effect syntax (uniform extern texture, sampler_state, lowercase compile vs_2_0) — FNA only
        "third-party/Gum/MonoGameInCode-Grayscale.fx",   // level_9_1 grayscale compiles at SM3 too
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

    // -------------------------------------------------------------------------
    // Phase 49 / GAP-1 — Gum's FnaSample-Shader.fx defines its technique entirely inside a
    // TECHNIQUE() #define macro. FxPreParser counts techniques BEFORE macro expansion, so the
    // raw pre-parse saw none. The FNA path now recovers macro techniques (GAP-1 fixed on FNA;
    // see Phase41MacroTechniqueTests.Fna_GumFnaSample_* — on FNA this shader now reaches
    // profile validation and is declined for its sub-SM2 vs_1_1 profile, SD0300).
    //
    // The OpenGL pin below remains: GL still returns SD0010 because the OpenGL target is gated
    // OUT of macro recovery (its macro set lacks SM4/SM6, so the stock effects would expand to
    // a legacy DX9/SM2 branch that crashes DXC's SPIR-V codegen — the documented GL macro-model
    // gap, distinct from the now-fixed FNA technique-blindness). If a future change closes the
    // GL macro-model gap and this shader compiles on GL, this test will fail; promote the
    // shader into OpenGLShaders() and delete this pin.
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Issue #127 — GL codegen fidelity pins for apos-shapes.fx. Reported by the
    // shader's own author: ShadowDusk's GL output rendered with small (<= 4/255)
    // deviations vs mgfxc. Two mechanical contributors were fixed in the GLSL
    // rewriter and are pinned here end-to-end through the real pipeline:
    //   (1) pow(x, 2.0) with a possibly-negative base (LinearGradient squares
    //       normalized-direction components) — undefined in GLSL, folded to a
    //       multiply by fxc; ShadowDusk now strength-reduces it the same way.
    //   (2) 1.0 / (aaSize / length(...)) double division (SmoothDiscontinuity) —
    //       fxc folds it; ShadowDusk now emits the single division b / a.
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task AposShapes_OpenGl_EmitsNoPowSquare_NoReciprocalOfQuotient_Issue127()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await TestHelpers.CompileFixtureAsync(
            "third-party/Apos.Shapes/apos-shapes.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0, because: $"apos-shapes must compile on GL; stderr: {result.Stderr}");

        string ascii = System.Text.Encoding.ASCII.GetString(
            result.Mgfx.Select(b => (b >= 9 && b <= 126) ? b : (byte)' ').ToArray());

        ascii.Should().NotMatchRegex(@"pow\([^,()]*, 2\.0\)",
            because: "pow(x, 2.0) is undefined for the negative bases LinearGradient feeds it — " +
                     "it must be strength-reduced to a multiply (issue #127)");
        ascii.Should().NotContain("1.0 / (",
            because: "every 1.0 / (a / b) reciprocal-of-quotient must fold to a single division (issue #127)");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task GumFnaSampleShader_MacroTechnique_OpenGl_KeepsSd0010_GlMacroModelGap()
    {
        const string fx = "third-party/Gum/FnaSample-Shader.fx";
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await TestHelpers.CompileFixtureAsync(fx, "OpenGL", ct: cts.Token);

        result.ExitCode.Should().NotBe(0,
            because: "OpenGL is gated out of macro-technique recovery (the GL macro-model gap); " +
                     "if this now compiles on GL, the gap is closed — promote the shader into " +
                     "OpenGLShaders() and delete this pin");
        result.Stderr.Should().Contain("SD0010",
            because: "on GL the macro technique is not recovered, so it surfaces as 'no techniques'");
    }
}
