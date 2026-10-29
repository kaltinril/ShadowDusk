#nullable enable

using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Sksl;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Issue #425 on the two non-MGFX routes. Both share the OpenGL DXC compile, so a <c>float3</c>
/// that HLSL packing put behind a <c>float</c> at byte 4 failed them too (SPIRV-Cross's std140
/// refusal, <c>SD0100</c>). Both declare each block member as its own named uniform, so the
/// std140 layout the OpenGL compile now uses changes nothing else. Needs the DXC and SPIRV-Cross
/// natives, like the converters' other tests.
/// </summary>
public sealed class Issue425PackedUniformRoutesTests
{
    private const string Fx = """
        #define PS ps_3_0
        float Gain;
        float3 Axis;
        float4 Tail;
        float4 main() : COLOR0 { return float4(Axis * Gain, 1) + Tail; }
        technique T { pass P { PixelShader = compile PS main(); } }
        """;

    [Fact]
    public void Raylib_ConvertsAFloat3PackedBehindAFloat()
    {
        var result = RaylibConverter.Convert(Fx, new RaylibConvertOptions { SourceName = "Packed.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : "");

        string fs = result.Value.FragmentShader;
        fs.ShouldContain("uniform float Gain;", Case.Sensitive);
        fs.ShouldContain("uniform vec3 Axis;", Case.Sensitive);
        fs.ShouldContain("uniform vec4 Tail;", Case.Sensitive);
    }

    [Fact]
    public void Sksl_ConvertsAFloat3PackedBehindAFloat()
    {
        var result = SkslConverter.Convert(Fx, new SkslConvertOptions { SourceName = "Packed.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : "");

        string sksl = result.Value.SkslText;
        sksl.ShouldContain("uniform float Gain;", Case.Sensitive);
        sksl.ShouldContain("uniform vec3 Axis;", Case.Sensitive);
        sksl.ShouldContain("uniform vec4 Tail;", Case.Sensitive);
    }
}
