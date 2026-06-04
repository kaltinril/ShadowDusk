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
///   supported on Desktop + HiDef (Reach gates these behind an optional
///   extension — documented platform wall). Emits the GENERIC <c>textureLod(</c>/
///   <c>textureGrad(</c> (NOT the legacy <c>texture2DLod</c> KNI HiDef can't
///   convert).</item>
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
    [InlineData("examples/ExSampleLevelHidef.fx", "textureLod(ps_s0,")]
    [InlineData("examples/ExSampleGradHidef.fx",  "textureGrad(ps_s0,")]
    public async Task LodGrad_Compiles_KeepsGenericBuiltin(string fx, string expectedCall)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await TestHelpers.CompileFixtureAsync(fx, "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"explicit-LOD/gradient sampling is supported on Desktop + HiDef now; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty();

        string ascii = Ascii(result.Mgfx);
        ascii.Should().Contain(expectedCall,
            because: "the generic LOD/grad form is the single-blob-correct one (desktop + KNI HiDef)");
        // The legacy texture2DLod/texture2DGrad forms are NOT ES-3.00 builtins and KNI
        // HiDef does not convert them — they must never be emitted.
        ascii.Should().NotContain("texture2DLod");
        ascii.Should().NotContain("texture2DGrad");
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
