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
}
