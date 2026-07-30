#nullable enable

using Shouldly;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Pure unit tests for the <see cref="ShaderFeatureSupport"/> "don't allow a feature with no
/// downstream consumer" guard (Phase 35 auto-select seam 4).
/// </summary>
public sealed class ShaderFeatureSupportTests
{
    [Fact]
    public void RuntimeSupported_IsNone_NoFeatureShippedYet()
    {
        // The load-bearing invariant: no shipping runtime consumes any of these features yet, so
        // none may be emitted. Flipping a flag here is a deliberate, render-proven version event.
        ShaderFeatureSupport.RuntimeSupported.ShouldBe(ShaderFeatures.None);
    }

    [Fact]
    public void Validate_None_ReturnsNull()
    {
        ShaderFeatureSupport.Validate(ShaderFeatures.None).ShouldBeNull();
    }

    [Theory]
    [InlineData(ShaderFeatures.VertexTextureFetch)]
    [InlineData(ShaderFeatures.TextureArrays)]
    [InlineData(ShaderFeatures.FullPrecisionGLES)]
    public void Validate_UnsupportedFeature_RejectsWithSD0201(ShaderFeatures feature)
    {
        ShaderError? error = ShaderFeatureSupport.Validate(feature);

        error.ShouldNotBeNull("no shipping runtime consumes this feature yet, so it must be rejected");
        error!.Code.ShouldBe("SD0201");
        error.Message.ShouldContain(feature.ToString(), Case.Sensitive);
    }

    [Fact]
    public void Validate_MultipleUnsupported_NamesAllOfThem()
    {
        ShaderError? error = ShaderFeatureSupport.Validate(
            ShaderFeatures.VertexTextureFetch | ShaderFeatures.TextureArrays);

        error.ShouldNotBeNull();
        error!.Message.ShouldContain("VertexTextureFetch", Case.Sensitive);
        error.Message.ShouldContain("TextureArrays", Case.Sensitive);
    }
}
