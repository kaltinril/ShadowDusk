#nullable enable

using FluentAssertions;
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
        ShaderFeatureSupport.RuntimeSupported.Should().Be(ShaderFeatures.None);
    }

    [Fact]
    public void Validate_None_ReturnsNull()
    {
        ShaderFeatureSupport.Validate(ShaderFeatures.None).Should().BeNull();
    }

    [Theory]
    [InlineData(ShaderFeatures.VertexTextureFetch)]
    [InlineData(ShaderFeatures.TextureArrays)]
    [InlineData(ShaderFeatures.FullPrecisionGLES)]
    public void Validate_UnsupportedFeature_RejectsWithSD0201(ShaderFeatures feature)
    {
        ShaderError? error = ShaderFeatureSupport.Validate(feature);

        error.Should().NotBeNull("no shipping runtime consumes this feature yet, so it must be rejected");
        error!.Code.Should().Be("SD0201");
        error.Message.Should().Contain(feature.ToString());
    }

    [Fact]
    public void Validate_MultipleUnsupported_NamesAllOfThem()
    {
        ShaderError? error = ShaderFeatureSupport.Validate(
            ShaderFeatures.VertexTextureFetch | ShaderFeatures.TextureArrays);

        error.Should().NotBeNull();
        error!.Message.Should().Contain("VertexTextureFetch");
        error.Message.Should().Contain("TextureArrays");
    }
}
