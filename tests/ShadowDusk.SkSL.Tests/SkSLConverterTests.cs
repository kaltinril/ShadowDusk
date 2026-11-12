using Shouldly;
using ShadowDusk.ShaderToy;
using Xunit;

namespace ShadowDusk.SkSL.Tests;

public sealed class SkSLConverterTests
{
    private const string Plain = """
        uniform float2 size;
        uniform half4 tint;
        half4 main(float2 coord) {
            half2 uv = coord / size;
            return half4(uv, 0.5, 1) * tint;
        }
        """;

    [Fact]
    public void Convert_PlainColorShader_Succeeds()
    {
        ConvertResult r = SkSLConverter.Convert(Plain);

        r.Success.ShouldBeTrue(string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        r.Fx.ShouldNotBeNull();
        r.Fx.ShouldContain("skslMain", Case.Sensitive);
        r.UsedUniforms.ShouldContain("size");
        r.UsedUniforms.ShouldContain("tint");
    }

    [Fact]
    public void Convert_FlipsYToTopLeftOrigin()
    {
        ConvertResult r = SkSLConverter.Convert(Plain);

        r.Fx.ShouldNotBeNull();
        r.Fx.ShouldContain("iResolution.y", Case.Sensitive);
    }

    [Fact]
    public void Convert_NoMain_FailsLoudly()
    {
        ConvertResult r = SkSLConverter.Convert("half4 foo(float2 c) { return half4(1); }");

        r.Success.ShouldBeFalse();
        r.Diagnostics.ShouldContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Convert_ChildShaderEval_SamplesWithPixelCoordsDividedBySize()
    {
        ConvertResult r = SkSLConverter.Convert(
            "uniform shader img; half4 main(float2 c) { return img.eval(c + float2(1, 0)); }");

        r.Success.ShouldBeTrue(string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        r.Fx.ShouldNotBeNull();
        r.Fx.ShouldContain("tex2D(img, ((c + float2(1, 0)) / imgSize))", Case.Sensitive);
        r.UsedUniforms.ShouldContain("img");
        r.UsedUniforms.ShouldContain("imgSize");
    }
}
