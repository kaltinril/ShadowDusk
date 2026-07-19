#nullable enable

using System.Text;
using FluentAssertions;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// GL texture-breadth coverage — end-to-end through the real <c>EffectCompiler</c>
/// OpenGL pipeline (CLI child process), not just the rewriter unit.
///
/// <para>History: Phase 33 (issue #7) made these constructs FAIL LOUDLY
/// (<c>SD0210</c>) because the MojoShader-dialect rewriter only modelled
/// <c>sampler2D</c>/<c>texture2D</c>. <b>Phase 34 adds real support:</b></para>
/// <list type="bullet">
///   <item><b>Cube maps</b> (<c>TextureCube</c>) — supported everywhere
///   (Desktop + KNI HiDef + Reach). Emits <c>samplerCube ps_s{k}</c> +
///   <c>textureCube(</c>; sampler-type byte = 1.</item>
///   <item><b>3D / volume</b> (<c>Texture3D</c>) — supported on Desktop + HiDef
///   (Reach/WebGL1 has no 3D textures — documented platform wall). Emits
///   <c>sampler3D ps_s{k}</c> + <c>texture3D(</c>; sampler-type byte = 2.</item>
///   <item><b>Explicit-LOD / gradient</b> (<c>SampleLevel</c>/<c>SampleGrad</c>) —
///   supported on Desktop + HiDef. Since Phase 43 F7 this emits the
///   dimension-specific LEGACY names (<c>texture2DLod(</c>/<c>texture2DGrad(</c>,
///   the MojoShader-faithful, Mesa-valid form) plus the guarded extension header
///   whose <c>__VERSION__ &gt;= 300</c> branch maps them back to the generic
///   builtins for KNI HiDef — one artifact, both profiles. (The Phase 34
///   generic-form choice failed on Mesa: <c>textureLod</c> does not exist in
///   versionless legacy GLSL.)</item>
/// </list>
/// <para>The Reach walls (3D, explicit-LOD) are NOT compile-time errors: ShadowDusk
/// emits ONE OpenGL blob and cannot know the consumer's KNI profile, so the limit
/// is documented, mirroring the KNI-version-floor pattern from Phase 33. Sampler
/// kinds still unmodelled (sampler2DArray, shadow samplers) DO still fail loudly —
/// covered by the rewriter unit tests
/// (<c>MonoGameGlslRewriterTests.Sampling_StillUnmodeledSampler_FailsLoudly</c>).</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class HidefGeneralityFixtureTests
{
    private static string Ascii(byte[] mgfx) =>
        Encoding.ASCII.GetString(mgfx.Select(b => (b >= 9 && b <= 126) ? b : (byte)' ').ToArray());

    [Fact]
    public async Task CubeMap_Compiles_EmitsSamplerCubeAndTextureCube()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/ExCubeSamplerHidef.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"cube maps are supported on every GL profile now; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty();

        string ascii = Ascii(result.Mgfx);
        ascii.Should().Contain("uniform samplerCube ps_s0;");
        ascii.Should().Contain("textureCube(ps_s0,");
        ascii.Should().NotContain("texture2D(",
            because: "a cube sampler must not be down-rewritten to texture2D()");
    }

    [Fact]
    public async Task VolumeTexture_Compiles_EmitsSampler3DAndTexture3D()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/ExVolumeTextureHidef.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"3D textures are supported on Desktop + HiDef now; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty();

        string ascii = Ascii(result.Mgfx);
        ascii.Should().Contain("uniform sampler3D ps_s0;");
        ascii.Should().Contain("texture3D(ps_s0,");
        ascii.Should().NotContain("texture2D(");
    }

    [Theory]
    [InlineData("examples/ExSampleLevelHidef.fx", "texture2DLod(ps_s0,",  "textureLod(ps_s0,")]
    [InlineData("examples/ExSampleGradHidef.fx",  "texture2DGrad(ps_s0,", "textureGrad(ps_s0,")]
    public async Task LodGrad_Compiles_EmitsLegacyNameWithGuardedHeader(string fx, string expectedCall, string genericCall)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await TestHelpers.CompileFixtureAsync(fx, "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"explicit-LOD/gradient sampling is supported on Desktop + HiDef; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty();

        // Phase 43 F7: the generic textureLod/textureGrad forms only exist from GLSL
        // 1.30 / ES 3.00 — Mesa's strict front-end rejects them in the versionless
        // legacy dialect (the confirmed Linux DesktopGL Effect-load failure). The
        // faithful form is MojoShader's: the dimension-specific legacy name plus the
        // guarded extension header, whose `#if __VERSION__ >= 300` branch maps it
        // back to the generic builtin for KNI HiDef/WebGL2 — one artifact, both
        // profiles (the Phase 33 promise).
        string ascii = Ascii(result.Mgfx);
        ascii.Should().Contain(expectedCall,
            because: "the dimension-specific legacy spelling is the Mesa-valid MojoShader form");
        ascii.Should().NotContain(genericCall,
            because: "no generic call site may survive (Mesa rejects it in versionless GLSL)");

        // The guarded header: HiDef mapping + ARB/EXT extension ladder + degrade.
        ascii.Should().Contain("#if __VERSION__ >= 300");
        ascii.Should().Contain("#elif defined(GL_ARB_shader_texture_lod)");
        ascii.Should().Contain("#define texture2DLod(a,b,c) texture2D(a,b)");
    }

    [Fact]
    public async Task VsTextureFetch_FailsLoudly_SD0210_NeverSilentlyBlack()
    {
        // Phase 43 F8: MonoGame 3.8.2's GL runtime cannot bind vertex textures
        // (ShaderProgramCache.Link assigns texture units only for the PIXEL shader's
        // sampler records; GraphicsDevice.OpenGL.cs has no VertexTextures path), so
        // ANY emitted GLSL would silently sample the wrong texture at runtime.
        // Previously the rewriter shipped the un-renamed sampler decl and the .mgfx
        // pointed at ps_s0 — silently-black output. Now it must fail LOUDLY.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/ExVsTextureFetch.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().NotBe(0,
            because: "a VS texture fetch cannot work in MonoGame 3.8.2's GL runtime and must not compile silently");
        result.Stderr.Should().Contain("SD0210");
        result.Stderr.Should().Contain("Vertex-stage texture sampling",
            because: "the diagnostic must name the actual limitation, not a generic rewrite error");
    }

    [Fact]
    public async Task MultiSampler2D_StillCompiles_WithSingleOutputAlias_AndScaledSamplers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync(
            "examples/ExMultiSamplerHidef.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"an ordinary 4-sampler 2D shader must still compile; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty();

        // The emitted GLSL is embedded as ASCII in the .mgfx — assert the Phase-33
        // single-output alias form and the scaled sampler remap, and that NO non-2D
        // construct leaked in.
        string ascii = Ascii(result.Mgfx);

        ascii.Should().Contain("#define ps_oC0 gl_FragColor",
            because: "the single fragment output must use mgfxc's #define alias (KNI HiDef converts it)");
        ascii.Should().Contain("ps_s0");
        ascii.Should().Contain("ps_s3", because: "the sampler remap must scale to 4 samplers");
        ascii.Should().NotContain("gl_FragData", because: "this is a single-output shader, not MRT");
        ascii.Should().NotContain("texture2DLod");
        ascii.Should().NotContain("textureGrad");
        ascii.Should().NotContain("samplerCube");
        ascii.Should().NotContain("sampler3D");
    }

    [Fact]
    public async Task EarlyReturnHelper_EmitsNoDoWhile_NoWrapperLoop_Issues107And136()
    {
        // Issue #107: a helper with a nested `if` that early-returns makes SPIRV-Cross
        // emit a one-shot `do { … break; … } while(false);` loop. Desktop GL accepts
        // it, but GLSL ES 1.00 (WebGL1 / KNI Reach) does not guarantee do-while, so the
        // effect compiles + loads on desktop yet FAILS TO LOAD in WebGL.
        // Issue #136 sharpened the requirement: the for-loop lowering that replaced the
        // do-while is itself derivative-poison on ANGLE D3D11 (any loop with a
        // conditional break/discard zeroes dFdx/dFdy). The entry wrapper must now be
        // UNWRAPPED into straight-line main with real early returns — no loop at all.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/Issue107DoWhile.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0, because: $"the early-return helper must compile; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty();

        string ascii = Ascii(result.Mgfx);
        ascii.Should().NotContain("while(false)",
            because: "do-while is not guaranteed in GLSL ES 1.00 (WebGL1) — issue #107");
        ascii.Should().NotContain("while (false)");
        ascii.Should().NotContain("_spvonce_",
            because: "the entry wrapper must be unwrapped, not lowered to a for-loop — a " +
                     "one-shot for-loop with a divergent exit poisons gradient ops on " +
                     "ANGLE D3D11 (issue #136)");
        ascii.Should().MatchRegex(@"ps_oC0 = [^;]+; return; \}",
            because: "each wrapper-level break becomes the output-write tail plus an early return");
    }

    [Fact]
    public async Task EarlyReturnHelperGradient_NoGradientInsideDivergentLoop_Issue136()
    {
        // Issue #136, nested-wrapper case (found in the fix's adversarial review): a
        // helper that BOTH early-returns AND takes a derivative gets its own one-shot
        // wrapper nested inside the entry wrapper. Rule 9a must recurse through the
        // plain block the outer unwrap leaves behind and unwrap the helper's wrapper
        // too — otherwise the 9b for-loop fallback recreates exactly the poisoned
        // shape (fwidth inside a loop with a conditional break reads 0.0 on ANGLE
        // D3D11, silently disabling derivative-based AA in Windows browsers).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/Issue136HelperGradient.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0, because: $"the gradient helper must compile; stderr: {result.Stderr}");

        string ascii = Ascii(result.Mgfx);
        ascii.Should().MatchRegex(@"\bfwidth\s*\(",
            because: "the fixture's fwidth must reach the GL output — otherwise it no longer exercises issue #136");
        ascii.Should().NotContain("while(false)");
        ascii.Should().NotContain("while (false)");

        var poisoned = ThirdPartyShaderCorpusTests.FindGradientOpsInsideDivergentLoops(ascii);
        poisoned.Should().BeEmpty(
            because: "no gradient op may sit inside a loop with a divergent exit — ANGLE D3D11 " +
                     "zeroes it there (issue #136, nested-helper case)");
    }

    [Fact]
    public async Task DeferredSprite_Mrt_CompilesOnGl_EmitsFragDataOutputs_Gap2()
    {
        // Phase 41 GAP-2: DeferredSprite.fx (a real Nez deferred MRT effect) returns a struct with
        // `: COLOR0`/`: COLOR1` outputs. DXC's GL/SPIR-V backend rejected COLOR as a PS output, so
        // the effect failed to compile on OpenGL ("Semantic COLOR is invalid for shader model: ps").
        // The GL-only struct-output rewrite (GlStructOutputColorRewriter) retargets them to
        // SV_Target0/1, and the rewriter emits gl_FragData[0]/[1] for true MRT (matching mgfxc's
        // golden). End-to-end through the real OpenGL pipeline.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await TestHelpers.CompileFixtureAsync("DeferredSprite.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"the MRT struct-output COLOR semantics must be retargeted for GL; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty();

        string ascii = Ascii(result.Mgfx);
        // TRUE MRT: BOTH outputs map to gl_FragData[N], including slot 0 (mgfxc golden form).
        ascii.Should().Contain("#define ps_oC0 gl_FragData[0]",
            because: "true MRT slot 0 is gl_FragData[0], not gl_FragColor (which would broadcast to all attachments)");
        ascii.Should().Contain("#define ps_oC1 gl_FragData[1]");
        // The PS-INPUT interpolant `Color : COLOR0` (VertexShaderOutput) must survive as a varying,
        // never rewritten to an output semantic — proven by the effect compiling at all (DXC would
        // reject SV_Target on a PS input). The two samplers (s0 + _normalMapSampler) are present.
        ascii.Should().Contain("ps_s0");
        ascii.Should().Contain("ps_s1", because: "DeferredSprite binds a second (normal-map) sampler");
    }
}
