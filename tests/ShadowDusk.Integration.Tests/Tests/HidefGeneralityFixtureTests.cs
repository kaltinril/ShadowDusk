#nullable enable

using System.Text;
using FluentAssertions;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 33 (issue #7) GENERALITY coverage — end-to-end through the real
/// <c>EffectCompiler</c> OpenGL pipeline, not just the rewriter unit.
///
/// <para>The § Scope bar is "any shader that compiles for GL must work in KNI
/// HiDef/WebGL2, OR fail loudly at compile time — never silently produce broken
/// HiDef GLSL." These fixtures exercise the constructs the corpus does NOT, and
/// assert that behavior at the product boundary:</para>
/// <list type="bullet">
///   <item><b>Loud-failure cases</b> — LOD (`SampleLevel`), gradient (`SampleGrad`),
///   and a non-2D sampler (`TextureCube`) each have no single-blob GLSL form valid
///   in both KNI Reach (WebGL1) and HiDef (WebGL2), so the compile MUST fail with
///   <c>SD0210</c> rather than emit GLSL that breaks (silently) under HiDef.</item>
///   <item><b>Parity case</b> — a 4-sampler 2D shader (larger than the corpus's
///   max of 2) MUST still compile and emit the <c>#define ps_oC0 gl_FragColor</c>
///   single-output form + <c>ps_s0..ps_s3</c>, proving the fix/guards don't
///   over-trigger and the sampler remap scales.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class HidefGeneralityFixtureTests
{
    // These compile through DXC/SPIRV-Cross but produce a GL construct that cannot
    // be lowered to a profile-agnostic GLSL payload — the rewriter must reject them.
    public static TheoryData<string> LoudFailureFixtures() => new()
    {
        "examples/ExSampleLevelHidef.fx", // textureLod  -> texture2DLod (guarded)
        "examples/ExSampleGradHidef.fx",  // textureGrad (guarded)
        "examples/ExCubeSamplerHidef.fx", // samplerCube (guarded)
    };

    [Theory]
    [MemberData(nameof(LoudFailureFixtures))]
    public async Task HidefUnsupportedConstruct_FailsLoudly_WithSD0210(string fx)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync(fx, "OpenGL", ct: cts.Token);

        // Must FAIL (the whole point — silent broken-HiDef output is the bug).
        result.ExitCode.Should().NotBe(0,
            because: $"'{fx}' emits a construct with no HiDef-safe single-blob form and must fail loudly; stderr: {result.Stderr}");
        result.Mgfx.Should().BeEmpty(because: "a failed compile must not emit output bytes");

        // The loud diagnostic is the Phase-33 generality guard (SD0210), not a crash.
        result.Stderr.Should().Contain("SD0210",
            because: $"the failure must be the explicit generality guard, not an unrelated error; stderr: {result.Stderr}");
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
        // single-output alias form and the scaled sampler remap, and that NO guard
        // construct leaked in.
        string ascii = Encoding.ASCII.GetString(
            result.Mgfx.Select(b => (b >= 9 && b <= 126) ? b : (byte)' ').ToArray());

        ascii.Should().Contain("#define ps_oC0 gl_FragColor",
            because: "the single fragment output must use mgfxc's #define alias (KNI HiDef converts it)");
        ascii.Should().Contain("ps_s0");
        ascii.Should().Contain("ps_s3", because: "the sampler remap must scale to 4 samplers");
        ascii.Should().NotContain("gl_FragData", because: "this is a single-output shader, not MRT");
        ascii.Should().NotContain("texture2DLod");
        ascii.Should().NotContain("textureGrad");
    }
}
