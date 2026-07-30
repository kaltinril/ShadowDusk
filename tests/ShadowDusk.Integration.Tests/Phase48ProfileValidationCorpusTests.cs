#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Integration.Tests.Tests;
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Permanent regression coverage for Phase 48 ("compile target profile validation"): a
/// <c>.fx</c> whose <c>compile &lt;target&gt;</c> token does not resolve — after macro
/// expansion — to a recognized shader profile is now <b>rejected with SD0013</b>, matching
/// mgfxc/fxc's <c>unrecognized compiler target</c>. ShadowDusk previously accepted these
/// silently (a fidelity gap: a user shipped a shader mgfxc would have rejected).
///
/// <para>Two reject reproductions and the W0 accept guard are exercised end-to-end on each
/// delivery target the consumer's game might use: OpenGL (MonoGame-GL / KNI), DirectX_11
/// (MonoGame-DX), and FNA (D3D9 fx_2_0). The accept fixture proves the
/// <c>*_level_9_1</c> feature-level profiles the standard MonoGame DirectX header expands
/// to are recognized (work item W0) — without that, rejection would wrongly fail every
/// stock MonoGame DirectX shader.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Phase48ProfileValidationCorpusTests
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Fixtures whose compile target is NOT a recognized profile — must reject SD0013.</summary>
    public static TheoryData<string> RejectFixtures() => new()
    {
        "examples/ExProfileTypo.fx",          // 'compile A …' — typo, not a profile, not a macro
        "examples/ExProfileUndefinedMacro.fx", // 'compile PS_SHADERMODEL …' with the #if OPENGL header removed
        "examples/ExProfileBogusLiteral.fx",  // 'compile ps_9_9 …' — profile-shaped but bogus literal
    };

    // -------------------------------------------------------------------------
    // Reject — OpenGL and DirectX_11 (GL/DX pipeline, macro expansion via DXC -P).
    // -------------------------------------------------------------------------

    [Theory]
    [Trait("Platform", "OpenGL")]
    [MemberData(nameof(RejectFixtures))]
    public async Task BogusProfile_OpenGL_RejectsWithSd0013(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileAsync(fx, PlatformTarget.OpenGL, cts.Token);

        result.IsFailure.ShouldBeTrue($"'{fx}' has an unrecognized compile target; mgfxc rejects it");
        result.Error.ShouldContain(e => e.Code == "SD0013", "the unrecognized-profile diagnostic must surface as SD0013");
    }

    [Theory]
    [Trait("Platform", "DirectX_11")]
    [MemberData(nameof(RejectFixtures))]
    public async Task BogusProfile_DirectX11_RejectsWithSd0013(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileAsync(fx, PlatformTarget.DirectX, cts.Token);

        result.IsFailure.ShouldBeTrue($"'{fx}' has an unrecognized compile target; mgfxc rejects it");
        result.Error.ShouldContain(e => e.Code == "SD0013");
    }

    // -------------------------------------------------------------------------
    // Reject — FNA (D3D9 fx_2_0 path; expansion reuses DXC -P, codegen stays vkd3d).
    // -------------------------------------------------------------------------

    [FnaTheory]
    [Trait("Platform", "FNA")]
    [MemberData(nameof(RejectFixtures))]
    public async Task BogusProfile_Fna_RejectsWithSd0013(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileAsync(fx, PlatformTarget.Fna, cts.Token);

        result.IsFailure.ShouldBeTrue($"'{fx}' has an unrecognized compile target; fxc rejects it");
        result.Error.ShouldContain(e => e.Code == "SD0013");
    }

    // -------------------------------------------------------------------------
    // Reject — W3 cross-stage: a ps_* profile bound to the VertexShader slot.
    // GL/DX/Vulkan reject with SD0014 (the FNA SD0300 equivalent is covered by
    // FnaProfilePolicyTests and is unchanged by W3).
    // -------------------------------------------------------------------------

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task StageMismatchProfile_GlDx_RejectsWithSd0014(PlatformTarget target)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileAsync("examples/ExProfileStageMismatch.fx", target, cts.Token);

        result.IsFailure.ShouldBeTrue("a ps_* profile in the VertexShader slot is a cross-stage binding mgfxc rejects");
        result.Error.ShouldContain(e => e.Code == "SD0014", "the stage/slot prefix mismatch must surface as SD0014");
    }

    // -------------------------------------------------------------------------
    // Accept — the W0 guard: the standard MonoGame header's *_level_9_1 profiles
    // must keep compiling on every target (else rejection regresses stock shaders).
    // -------------------------------------------------------------------------

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("OpenGL")]
    [InlineData("DirectX_11")]
    public async Task Level9HeaderShader_CompilesOnGlAndDx(string profile)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await TestHelpers.CompileFixtureAsync(
            "examples/ExProfileLevel9Header.fx", profile, ct: cts.Token);

        result.ExitCode.ShouldBe(0, customMessage: $"the standard *_level_9_1 header must remain accepted on {profile}; stderr: {result.Stderr}");
        result.Mgfx.ShouldNotBeEmpty();
    }

    [FnaFact]
    [Trait("Platform", "FNA")]
    public async Task Level9HeaderShader_CompilesOnFna()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileAsync(
            "examples/ExProfileLevel9Header.fx", PlatformTarget.Fna, cts.Token);

        result.IsSuccess.ShouldBeTrue("PS_SHADERMODEL resolves to a recognized profile, so FNA must compile it (SM3 ceiling); " +
                     $"errors: {(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>")}");
    }

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string fx, PlatformTarget target, CancellationToken ct)
    {
        string source = await File.ReadAllTextAsync(TestHelpers.FixturePath(fx), ct);
        var options = new CompilerOptions
        {
            Target          = target,
            SourceFileName  = TestHelpers.FixturePath(fx),
            IncludeResolver = new ShadowDusk.Core.Preprocessor.FileSystemIncludeResolver(),
        };
        return await new EffectCompiler().CompileAsync(source, options, ct);
    }
}
