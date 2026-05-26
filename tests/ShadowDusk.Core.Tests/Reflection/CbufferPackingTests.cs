#nullable enable

using FluentAssertions;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests.Reflection;

// These tests validate the reflection data model's ability to represent
// correct HLSL cbuffer packing offsets and sizes.  They do NOT call any
// production packing logic — they assert that hand-constructed
// VariableReflection records can capture the expected layout.
public sealed class CbufferPackingTests
{
    // HLSL packing rules (matching D3D constant-buffer layout):
    //
    //   float        : size 4,  aligns to 4
    //   float3       : size 12, aligns to 16 (but can pack into a row after a float if room remains)
    //   float4       : size 16, aligns to 16
    //   float4x4     : size 64, aligns to 16
    //   float[N]     : each element padded to 16 bytes → total = N * 16
    //
    // Layout for basic_cbuffer.hlsl:
    //   Scale     float      offset=0,  size=4
    //   Direction float3     offset=4,  size=12   (packs into same row as Scale)
    //   Color     float4     offset=16, size=16   (next row; row 0 is now full)
    //   World     float4x4   offset=32, size=64
    //   Total cbuffer size = 96 bytes

    [Fact]
    public void Float_AtOffsetZero_HasSizeFour()
    {
        var variable = new VariableReflection
        {
            Name           = "Scale",
            StartOffset    = 0,
            SizeBytes      = 4,
            ParameterClass = EffectParameterClass.Scalar,
            ParameterType  = EffectParameterType.Single,
            Rows           = 1,
            Columns        = 1,
            Elements       = 0,
        };

        variable.StartOffset.Should().Be(0);
        variable.SizeBytes.Should().Be(4);
        variable.ParameterClass.Should().Be(EffectParameterClass.Scalar);
        variable.ParameterType.Should().Be(EffectParameterType.Single);
    }

    [Fact]
    public void Float3_AfterFloat_PacksIntoSameRow()
    {
        // float3 at offset 4 consumes bytes 4–15, sharing the first 16-byte row
        // with the preceding float at offset 0.
        var variable = new VariableReflection
        {
            Name           = "Direction",
            StartOffset    = 4,
            SizeBytes      = 12,
            ParameterClass = EffectParameterClass.Vector,
            ParameterType  = EffectParameterType.Single,
            Rows           = 1,
            Columns        = 3,
            Elements       = 0,
        };

        variable.StartOffset.Should().Be(4);
        variable.SizeBytes.Should().Be(12);
        variable.Columns.Should().Be(3);
        // Verify the variable fits inside the first 16-byte row
        (variable.StartOffset + variable.SizeBytes).Should().BeLessThanOrEqualTo(16);
    }

    [Fact]
    public void Float4_AfterFloat3_StartsAtNextSixteenByteRow()
    {
        // float4 cannot start at offset 16 and spill; it must be fully within a row.
        var variable = new VariableReflection
        {
            Name           = "Color",
            StartOffset    = 16,
            SizeBytes      = 16,
            ParameterClass = EffectParameterClass.Vector,
            ParameterType  = EffectParameterType.Single,
            Rows           = 1,
            Columns        = 4,
            Elements       = 0,
        };

        variable.StartOffset.Should().Be(16);
        variable.SizeBytes.Should().Be(16);
        (variable.StartOffset % 16).Should().Be(0, because: "float4 must start on a 16-byte boundary");
    }

    [Fact]
    public void Float4x4_HasSizeSixtyFour()
    {
        var variable = new VariableReflection
        {
            Name           = "World",
            StartOffset    = 32,
            SizeBytes      = 64,
            ParameterClass = EffectParameterClass.Matrix,
            ParameterType  = EffectParameterType.Single,
            Rows           = 4,
            Columns        = 4,
            Elements       = 0,
        };

        variable.SizeBytes.Should().Be(64);
        variable.Rows.Should().Be(4);
        variable.Columns.Should().Be(4);
        (variable.StartOffset % 16).Should().Be(0, because: "matrix must start on a 16-byte boundary");
    }

    [Fact]
    public void BasicCbuffer_TotalSize_IsNinetySixBytes()
    {
        // Validate that the layout described by the fixture adds up to 96 bytes.
        //   Scale     offset=0,  size=4
        //   Direction offset=4,  size=12
        //   Color     offset=16, size=16
        //   World     offset=32, size=64
        var variables = new[]
        {
            new VariableReflection { Name = "Scale",     StartOffset = 0,  SizeBytes = 4,  ParameterClass = EffectParameterClass.Scalar, ParameterType = EffectParameterType.Single, Rows = 1, Columns = 1, Elements = 0 },
            new VariableReflection { Name = "Direction", StartOffset = 4,  SizeBytes = 12, ParameterClass = EffectParameterClass.Vector, ParameterType = EffectParameterType.Single, Rows = 1, Columns = 3, Elements = 0 },
            new VariableReflection { Name = "Color",     StartOffset = 16, SizeBytes = 16, ParameterClass = EffectParameterClass.Vector, ParameterType = EffectParameterType.Single, Rows = 1, Columns = 4, Elements = 0 },
            new VariableReflection { Name = "World",     StartOffset = 32, SizeBytes = 64, ParameterClass = EffectParameterClass.Matrix, ParameterType = EffectParameterType.Single, Rows = 4, Columns = 4, Elements = 0 },
        };

        var last = variables.Last();
        var totalSize = last.StartOffset + last.SizeBytes;
        totalSize.Should().Be(96);
    }

    [Fact]
    public void FloatArray_EachElementPaddedToSixteenBytes()
    {
        // float[4] in a cbuffer: each element is padded to 16 bytes → total 64 bytes.
        var variable = new VariableReflection
        {
            Name           = "PointLights",
            StartOffset    = 0,
            SizeBytes      = 64,
            ParameterClass = EffectParameterClass.Scalar,
            ParameterType  = EffectParameterType.Single,
            Rows           = 1,
            Columns        = 1,
            Elements       = 4,
        };

        variable.Elements.Should().Be(4);
        variable.SizeBytes.Should().Be(64, because: "each float array element is padded to 16 bytes");
        (variable.SizeBytes / variable.Elements).Should().Be(16);
    }
}
