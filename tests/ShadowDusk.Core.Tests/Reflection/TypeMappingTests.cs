#nullable enable

using FluentAssertions;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests.Reflection;

public sealed class TypeMappingTests
{
    // -------------------------------------------------------------------------
    // EffectParameterClass — values must match MonoGame's EffectParameterClass
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(EffectParameterClass.Scalar, 0)]
    [InlineData(EffectParameterClass.Vector, 1)]
    [InlineData(EffectParameterClass.Matrix, 2)]
    [InlineData(EffectParameterClass.Object, 3)]
    [InlineData(EffectParameterClass.Struct, 4)]
    public void EffectParameterClass_Values_MatchMonoGame(EffectParameterClass cls, int expected)
        => ((int)cls).Should().Be(expected);

    [Fact]
    public void EffectParameterClass_HasExactlyFiveMembers()
        => Enum.GetValues<EffectParameterClass>().Should().HaveCount(5);

    // -------------------------------------------------------------------------
    // EffectParameterType — values must match MonoGame's EffectParameterType
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(EffectParameterType.Void,        0)]
    [InlineData(EffectParameterType.Bool,        1)]
    [InlineData(EffectParameterType.Int32,       2)]
    [InlineData(EffectParameterType.Single,      3)]
    [InlineData(EffectParameterType.String,      4)]
    [InlineData(EffectParameterType.Texture,     5)]
    [InlineData(EffectParameterType.Texture1D,   6)]
    [InlineData(EffectParameterType.Texture2D,   7)]
    [InlineData(EffectParameterType.Texture3D,   8)]
    [InlineData(EffectParameterType.TextureCube, 9)]
    public void EffectParameterType_Values_MatchMonoGame(EffectParameterType type, int expected)
        => ((int)type).Should().Be(expected);

    [Fact]
    public void EffectParameterType_HasExactlyTenMembers()
        => Enum.GetValues<EffectParameterType>().Should().HaveCount(10);

    // -------------------------------------------------------------------------
    // TextureDimension — sanity-check that Unknown is the zero value
    // -------------------------------------------------------------------------

    [Fact]
    public void TextureDimension_Unknown_IsZeroValue()
        => ((int)TextureDimension.Unknown).Should().Be(0);

    [Theory]
    [InlineData(TextureDimension.Texture1D)]
    [InlineData(TextureDimension.Texture2D)]
    [InlineData(TextureDimension.Texture3D)]
    [InlineData(TextureDimension.TextureCube)]
    public void TextureDimension_KnownValues_AreNonZero(TextureDimension dim)
        => ((int)dim).Should().BeGreaterThan(0);
}
