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
/// <c>tex1D</c> are not on the MonoGame-GL path), with one tracked exception:
/// <c>Noise.fx</c> hits a ShadowDusk GL bug (a uniform named <c>noise</c> collides
/// with the GLSL reserved word; SPIRV-Cross renames it to <c>_noise</c> but the
/// reflected parameter list does not follow, so the GL cbuffer/parameter join fails
/// with <c>SD0012</c>), so it is wired on DX + FNA only until that is fixed.</para>
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
    /// Shaders that compile on the MonoGame-GL target. The all-runtime subset only
    /// (Crosshatch/PaletteCycler/Reflection have legitimate GL SM limits; Noise hits
    /// the tracked SD0012 GL bug).
    /// </summary>
    public static TheoryData<string> OpenGLShaders() => new()
    {
        Root + "Bevels.fx",
        Root + "BloomCombine.fx",
        Root + "BloomExtract.fx",
        Root + "GaussianBlur.fx",       // literal-bounded for-loop over array uniforms
        Root + "HeatDistortion.fx",
        Root + "Letterbox.fx",          // VPOS (compiles; render-equivalence not claimed)
        Root + "PixelGlitch.fx",        // helper fn hash11()
        Root + "SpriteBlinkEffect.fx",
        Root + "SpriteLines.fx",        // VPOS + float % (compiles; render-equivalence not claimed)
        Root + "Twist.fx",              // relational if(dist<radius) + sin/cos
        Root + "Vignette.fx",
    };

    /// <summary>
    /// Shaders that compile on the MonoGame-DX target — the all-runtime subset PLUS the
    /// DX-capable ones GL/FNA can't take: Crosshatch (int uniform), Reflection (2-tech
    /// VS+PS), Noise (the GL SD0012 bug does not affect DX).
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
        Root + "Noise.fx",              // helper fn rand(); DX is fine (GL bug is SD0012)
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
        Root + "Noise.fx",              // helper fn rand(); FNA path unaffected by the GL bug
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
