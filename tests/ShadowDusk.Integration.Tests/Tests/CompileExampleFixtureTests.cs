#nullable enable

using FluentAssertions;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Compile-level coverage for the fresh, project-owned example shaders under
/// <c>tests/fixtures/shaders/examples/</c> (see <c>docs/test-shader-corpus.md</c>).
/// These fixtures have FULLY KNOWN provenance — authored from scratch for
/// ShadowDusk — and exist to exercise the legacy→modern rewrite surface going
/// forward, alongside the original 10 cross-validated shaders.
///
/// Scope note: these assert ShadowDusk produces a well-formed, structurally
/// valid OpenGL <c>.mgfx</c> (compiles + loads-shaped). Pixel-equivalence to
/// <c>mgfxc</c> is NOT asserted here because these fixtures have no checked-in
/// <c>mgfxc</c> golden yet (mgfxc needs Windows + fxc.exe; see the corpus doc).
/// The in-engine render-and-compare lives in <c>validation/</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CompileExampleFixtureTests
{
    private const byte ProfileOpenGL = 0; // MgfxProfile.OpenGL

    public static TheoryData<string> ExampleFixtures() => new()
    {
        "examples/ExBareSamplerTex2D.fx",
        "examples/ExSamplerStateUniform.fx",
        "examples/ExDualTexture.fx",
        "examples/ExLegacyTextureDiscard.fx",
        "examples/ExModernSample.fx",
    };

    [Theory]
    [Trait("Platform", "OpenGL")]
    [MemberData(nameof(ExampleFixtures))]
    public async Task Example_OpenGL_ProducesValidMgfx(string fx)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync(fx, "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0, because: $"'{fx}' should compile for OpenGL; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty(because: "a successful compile must emit output bytes");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.Signature.Should().Be("MGFX");
        reader.MgfxVersion.Should().Be(10);
        reader.ProfileId.Should().Be(ProfileOpenGL);
        reader.TotalShaderBlobCount.Should().BeGreaterThan(0, because: "each example declares a pixel shader pass");
    }
}
