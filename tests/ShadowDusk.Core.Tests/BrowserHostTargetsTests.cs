#nullable enable

using Shouldly;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Issue #272: the browser/WASM host's up-front target check (<see cref="BrowserHostTargets"/>),
/// which <c>WasmShaderCompiler</c> and <c>WasmSlangCompiler</c> run before any module loads. The
/// real-browser half (the published sample, real <c>[JSImport]</c>) is asserted by
/// <c>tests/ShadowDusk.BrowserTests/browser-vkd3d-gate.mjs</c> and <c>browser-slang-gate.mjs</c>.
/// </summary>
public sealed class BrowserHostTargetsTests
{
    [Fact]
    public void DirectX12_IsRejected_WithSD1906_NamingTargetAndHost()
    {
        ShaderError? error = BrowserHostTargets.Reject(
            new CompilerOptions { Target = PlatformTarget.DirectX12 }, "Grayscale.fx");

        error.ShouldNotBeNull();
        error.Code.ShouldBe("SD1906");
        error.File.ShouldBe("Grayscale.fx");
        error.Severity.ShouldBe(ShaderErrorSeverity.Error);
        error.Message.ShouldContain("PlatformTarget.DirectX12", Case.Sensitive);
        error.Message.ShouldContain("browser/WASM host", Case.Sensitive);
        error.Message.ShouldContain("OpenGL, Vulkan, DirectX, Fna", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.Vulkan)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.Fna)]
    public void BrowserExportTargets_AreAccepted(PlatformTarget target) =>
        BrowserHostTargets.Reject(new CompilerOptions { Target = target }, "a.fx").ShouldBeNull();

    [Fact]
    public void Metal_IsLeftToThePipelinesSD0200_SameAsEveryHost() =>
        BrowserHostTargets.Reject(new CompilerOptions { Target = PlatformTarget.Metal }, "a.fx").ShouldBeNull();

    [Fact]
    public void EveryPlatformTargetMember_IsClassified()
    {
        // A new PlatformTarget member must be decided for the browser host on purpose: it is
        // either an export target, Metal (refused by the pipeline everywhere), or rejected here.
        foreach (PlatformTarget target in Enum.GetValues<PlatformTarget>())
        {
            bool accepted = BrowserHostTargets.Reject(new CompilerOptions { Target = target }, "a.fx") is null;
            bool expectedAccepted = target is PlatformTarget.OpenGL or PlatformTarget.Vulkan
                or PlatformTarget.DirectX or PlatformTarget.Fna or PlatformTarget.Metal;
            accepted.ShouldBe(expectedAccepted, $"PlatformTarget.{target}");
        }
    }

    [Fact]
    public void UndefinedTargetValue_IsRejected() =>
        BrowserHostTargets.Reject(new CompilerOptions { Target = (PlatformTarget)99 }, "a.fx")!
            .Message.ShouldContain("PlatformTarget.value 99", Case.Sensitive);

    [Fact]
    public void ProfileGraphicsTarget_WinsOverTarget_LikeThePipeline()
    {
        // The pipeline lets a set Profile's GraphicsTarget override Target, so the check must see
        // the same effective target: a DX11 profile is accepted even with Target = DirectX12.
        var options = new CompilerOptions
        {
            Target = PlatformTarget.DirectX12,
            Profile = CapabilityProfile.MonoGameDX_SM5,
        };

        BrowserHostTargets.EffectiveTarget(options).ShouldBe(PlatformTarget.DirectX);
        BrowserHostTargets.Reject(options, "a.fx").ShouldBeNull();
    }
}
