#nullable enable

using FluentAssertions;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests.Reflection;

/// <summary>
/// Pins the shared HLSL-semantic → MonoGame <c>VertexElementUsage</c> mapping. Both the
/// SPIR-V (Vulkan) and DXIL (DirectX 12) attribute-table builders go through this type, so
/// a regression here is a wrong vertex layout on two rung-4 targets at once — and it fails
/// at the consumer's first Draw, not at compile time.
///
/// <para>Bug-hunt 2026-07-27 N5 landed the PSIZE alias and the numeric-suffix overflow
/// guard with no test; these are that missing guard.</para>
/// </summary>
public sealed class VertexSemanticMapperTests
{
    [Theory]
    [InlineData("POSITION",  0, 0)]
    [InlineData("POSITION0", 0, 0)]
    [InlineData("POSITION1", 0, 1)]
    [InlineData("SV_POSITION", 0, 0)]
    [InlineData("COLOR",     1, 0)]
    [InlineData("COLOR1",    1, 1)]
    [InlineData("TEXCOORD",  2, 0)]
    [InlineData("TEXCOORD3", 2, 3)]
    [InlineData("NORMAL",    3, 0)]
    [InlineData("BINORMAL",  4, 0)]
    [InlineData("TANGENT",   5, 0)]
    [InlineData("BLENDINDICES", 6, 0)]
    [InlineData("BLENDWEIGHT",  7, 0)]
    [InlineData("DEPTH",     8, 0)]
    [InlineData("FOG",       9, 0)]
    [InlineData("TESSELLATEFACTOR", 12, 0)]
    public void Map_KnownSemantics(string semantic, byte usage, int index)
        => VertexSemanticMapper.Map(semantic).Should().Be((usage, index));

    [Theory]
    [InlineData("PSIZE")]
    [InlineData("PSIZE0")]
    [InlineData("POINTSIZE")]
    [InlineData("POINTSIZE0")]
    public void Map_PointSizeSpellings_MapToPointSizeNotTextureCoordinate(string semantic)
    {
        // PSIZE is the real D3D9-era HLSL spelling. It used to fall through to the
        // TextureCoordinate default, where it collided with a genuine TEXCOORD0 attribute
        // and silently produced a wrong vertex layout.
        VertexSemanticMapper.Map(semantic).Should().Be(((byte)10, 0));
    }

    [Fact]
    public void Map_IsCaseInsensitive()
        => VertexSemanticMapper.Map("TexCoord2").Should().Be(((byte)2, 2));

    [Fact]
    public void Map_UnknownSemantic_FallsBackToTextureCoordinate()
    {
        // mgfxc's own default for a semantic it does not model.
        VertexSemanticMapper.Map("POSITIONT").Should().Be(((byte)2, 0));
        VertexSemanticMapper.Map("MYTHING7").Should().Be(((byte)2, 7));
    }

    [Fact]
    public void Map_AbsurdNumericSuffix_DoesNotThrow()
    {
        // A 40-digit index cannot fit an int. This mapper is pure and is called from the
        // middle of a compile, so it must degrade rather than throw OverflowException out
        // of the pipeline as an unhandled internal error.
        string semantic = "TEXCOORD" + new string('9', 40);

        var act = () => VertexSemanticMapper.Map(semantic);

        act.Should().NotThrow();
    }
}
