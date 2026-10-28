#nullable enable

using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Sksl;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Pins what the two non-MGFX routes do with a global initializer (<c>float R = 1;</c>), which the
/// MGFX/KNIFX writers now preserve (owner directive 2026-10-28). Neither has an effect container
/// to carry a default blob: the raylib route emits a <c>glsl330</c> fragment shader and the SkSL
/// route a <c>SKRuntimeEffect</c> source, both fed by DXC, which drops the initializer. The
/// uniform is declared WITHOUT a value, so it starts at the host library's own default (zero in
/// raylib and Skia) until the game sets it. This is the documented current behavior, pinned so a
/// change to it is deliberate; it is not a statement that it is the desirable end state.
/// Needs the DXC and SPIRV-Cross natives, like the converters' other tests.
/// </summary>
public sealed class InitializerDefaultsOtherRoutesTests
{
    private const string Fx = """
        #define PS ps_3_0
        float R = 1;
        float4 main() : COLOR0 { return float4(R, 0, 0, 1); }
        technique T { pass P { PixelShader = compile PS main(); } }
        """;

    [Fact]
    public void Raylib_DeclaresTheUniformWithoutAValue()
    {
        var result = RaylibConverter.Convert(Fx, new RaylibConvertOptions { SourceName = "R.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : "");

        string fs = result.Value.FragmentShader;
        fs.ShouldContain("uniform float R;", Case.Sensitive);
        fs.ShouldNotContain("R = 1", Case.Sensitive);
    }

    [Fact]
    public void Sksl_DeclaresTheUniformWithoutAValue()
    {
        var result = SkslConverter.Convert(Fx, new SkslConvertOptions { SourceName = "R.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : "");

        string sksl = result.Value.SkslText;
        sksl.ShouldContain("uniform float R;", Case.Sensitive);
        sksl.ShouldNotContain("R = 1", Case.Sensitive);
    }
}
