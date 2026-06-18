#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Tests.Fx2;
using ShadowDusk.Integration.Tests.Tests;
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Permanent regression coverage for issue #106 ("Shader should be able to return
/// ternary values"): relational operators (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>,
/// <c>&gt;=</c>), ternaries, <c>if</c>/<c>else</c> branches, and helper functions in a
/// shader BODY were misparsed by the <c>FxPreParser</c> as FX annotations and failed the
/// compile with FX0001. The just-fixed parser must now keep these shapes compiling.
///
/// <para>The dedicated <c>examples/ExTernaryHelper.fx</c>, <c>examples/ExRelationalThreshold.fx</c>,
/// <c>examples/ExRelationalBranch.fx</c>, and <c>examples/ExLoopRelational.fx</c> fixtures
/// (see <c>docs/test-shader-corpus.md</c>) live in the all-runtime SM3/fx_2_0 subset, so
/// each is compile-asserted on ALL THREE delivery targets the consumer's game might use:
/// OpenGL (MonoGame-GL / KNI), DirectX_11 (MonoGame-DX), and FNA (D3D9 fx_2_0).</para>
///
/// <para>Scope: this asserts the bug-class compiles to a well-formed container on every
/// target (the #106 regression is a PARSE failure, so a green compile is the direct
/// proof). It is NOT a pixel-equivalence claim — that bar is carried by the
/// <c>validation/*</c> render drivers and is a follow-up for these fixtures (no committed
/// <c>mgfxc</c>/<c>fxc</c> golden exists for them yet; mgfxc needs Windows + fxc.exe).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue106RegressionCorpusTests
{
    private const byte ProfileOpenGL    = 0; // MgfxProfile.OpenGL
    private const byte ProfileDirectX11 = 1; // MgfxProfile.DirectX11

    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The issue-#106 regression fixtures, each in the all-runtime SM3/fx_2_0 subset.</summary>
    public static TheoryData<string> Fixtures() => new()
    {
        "examples/Issue106Repro.fx",         // the VERBATIM reporter shader (nested if, ==, <=, early return in a helper)
        "examples/ExTernaryHelper.fx",      // helper returns a ternary over a relational (the canonical #106 shape)
        "examples/ExRelationalThreshold.fx", // <, <=, >, >= directly in the PS body
        "examples/ExRelationalBranch.fx",    // relational-driven if/else if/else + a chained ternary
        "examples/ExLoopRelational.fx",      // relational condition in a for-loop header
    };

    // -------------------------------------------------------------------------
    // OpenGL — the MonoGame-GL / KNI delivery target.
    // -------------------------------------------------------------------------

    [Theory]
    [Trait("Platform", "OpenGL")]
    [MemberData(nameof(Fixtures))]
    public async Task Issue106Fixture_OpenGL_CompilesToValidMgfx(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await TestHelpers.CompileFixtureAsync(fx, "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"'{fx}' (issue #106 regression) must compile for OpenGL; stderr: {result.Stderr}");
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
    [MemberData(nameof(Fixtures))]
    public async Task Issue106Fixture_DirectX11_CompilesToValidMgfx(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await TestHelpers.CompileFixtureAsync(fx, "DirectX_11", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: $"'{fx}' (issue #106 regression) must compile for DirectX_11; stderr: {result.Stderr}");
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
    [MemberData(nameof(Fixtures))]
    public async Task Issue106Fixture_Fna_CompilesToValidFx2Binary(string fx)
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
            because: $"'{fx}' (issue #106 regression) is in the SM <= 3 FNA subset and must compile; " +
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
