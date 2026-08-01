#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// Regression test for a phantom-parameter bug found while investigating a hosted-CI
/// DXIL-validation failure (dxil.dll version skew on GitHub Actions windows-latest).
///
/// <para><b>Root cause.</b> For the OpenGL target, ShadowDusk's desktop default reflects
/// from a SEPARATE companion DXC compile targeting <see cref="PlatformTarget.DirectX"/>
/// (SM6 DXIL, via <c>DxilReflectionExtractor</c>) rather than from the SPIR-V that is
/// actually transpiled to the shipped GLSL. The two are independent DXC invocations and
/// can apply DIFFERENT dead-code elimination: <c>GradientToy.fx</c>'s pixel shader computes
/// <c>fragCoord = uv * iResolution.xy</c> then <c>uv2 = fragCoord / iResolution.xy</c> — an
/// algebraic identity DXC's <c>-spirv</c> backend cancels out entirely (the shipped GLSL
/// never references <c>iResolution</c> at all), while the DirectX-target companion compile
/// does NOT perform the same cancellation, so its DXIL reflection still reports an
/// <c>iResolution</c> constant-buffer variable. The result: TODAY'S desktop default ships
/// an <c>.mgfx</c> with <c>Parameters["iResolution"]</c> present in the reflection table
/// with NO backing uniform anywhere in the actual GLSL — a phantom parameter.</para>
///
/// <para>This is NOT simply fixed by reflecting from the SPIR-V that actually ships
/// (<c>SpirvReflector</c>) instead of the DXIL companion compile: doing so was tried and
/// reverted, because it swaps this divergence for a different one against the committed
/// mgfxc golden. <c>tests/fixtures/golden/OpenGL/GradientToy.mgfx</c> DOES carry an
/// <c>iResolution</c> parameter — real fxc/mgfxc's compiler does not perform DXC's
/// algebraic cancellation, so mgfxc's own shipped GLSL still reads it. Reflecting from
/// SPIR-V would make ShadowDusk's parameter list diverge from mgfxc BY NAME (the project's
/// primary compatibility bar), trading a phantom-but-name-compatible parameter for a
/// missing one. The actual root cause is upstream of reflection: ShadowDusk's DXC compile
/// and mgfxc's fxc compile produce non-equivalent GLSL for this shader. See issue #185 for
/// the ongoing investigation; this test is intentionally left failing (Skip) until that
/// compile-level divergence has a scoped fix, rather than papering over it here.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class GlPhantomParameterTests
{
    private readonly ITestOutputHelper _output;

    public GlPhantomParameterTests(ITestOutputHelper output) => _output = output;

    [Fact(Skip = "Known open issue (#185): GradientToy.fx's DXC compile and mgfxc's fxc " +
                 "compile diverge on an algebraic identity, so no reflection source is " +
                 "fully mgfxc-compatible here. Tracked separately from the CI DXIL-validation " +
                 "crash this branch fixes; un-skip once the compile-level divergence is scoped.")]
    public async Task GradientToy_OpenGL_ReflectionHasNoPhantomParameters()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        string fxPath = TestHelpers.FixturePath("shadertoy/GradientToy.fx");
        File.Exists(fxPath).ShouldBeTrue($".fx fixture must exist at {fxPath}");
        string source = await File.ReadAllTextAsync(fxPath, ct);

        var result = await new EffectCompiler().CompileAsync(
            source,
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = fxPath },
            ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
                ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
                : "compile must succeed");

        MgfxBlobReader mgfx = MgfxBlobReader.Parse(result.Value.Data);

        string allGlsl = string.Join("\n---\n",
            mgfx.ShaderBlobs.Select(b => Encoding.UTF8.GetString(b)));
        _output.WriteLine(allGlsl);

        foreach (MgfxParameterRecord p in mgfx.Parameters)
        {
            _output.WriteLine($"parameter: {p.Name} class={p.Class} type={p.Type}");

            // Every reflected parameter must correspond to SOMETHING the shipped GLSL
            // actually reads — a parameter name absent from every shader blob has no
            // uniform backing it, so SetValue silently writes nowhere. This is the
            // general form of the iResolution phantom-parameter bug.
            allGlsl.ShouldContain(p.Name, Case.Sensitive,
                customMessage: $"reflected parameter '{p.Name}' has no corresponding uniform in the " +
                                "shipped GLSL — it was optimized out of the actual compile but still " +
                                "reflected from a separate companion compile (phantom parameter)");
        }
    }
}
