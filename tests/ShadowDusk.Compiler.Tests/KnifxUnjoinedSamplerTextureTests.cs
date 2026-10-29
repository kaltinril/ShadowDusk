#nullable enable

using Shouldly;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// On KNIFX a sampler record legally binds -1 for a texture-less legacy sampler (as KNI's own
/// pipeline writes it), so the writer cannot tell a deliberate -1 from a failed texture-to-parameter
/// join. The pipeline collects the textures whose join failed (any texture NOT in the deliberate
/// set) and refuses the compile with SD0018, instead of shipping "no texture" silently.
/// </summary>
public sealed class KnifxUnjoinedSamplerTextureTests
{
    [Fact]
    public void NoUnjoinedTexture_IsNoError() =>
        CompilationPipeline.UnjoinedSamplerTextureError([], "Effect.fx").ShouldBeNull();

    [Fact]
    public void AnUnjoinedTexture_IsSd0018_NamingIt()
    {
        ShaderError? error = CompilationPipeline.UnjoinedSamplerTextureError(["Diffuse", "Diffuse", "Mask"], "Effect.fx");

        error.ShouldNotBeNull();
        error.Code.ShouldBe("SD0018");
        error.Severity.ShouldBe(ShaderErrorSeverity.Error);
        error.File.ShouldBe("Effect.fx");
        error.Message.ShouldContain("'Diffuse', 'Mask'", Case.Sensitive);
    }
}
