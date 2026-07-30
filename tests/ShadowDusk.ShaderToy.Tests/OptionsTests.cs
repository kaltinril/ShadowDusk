using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Tests that <see cref="ConvertOptions"/> values flow into the emitted <c>.fx</c>:
/// <see cref="ConvertOptions.EffectName"/> into the header metadata,
/// <see cref="ConvertOptions.TechniqueName"/> into the <c>technique</c> block, and
/// <see cref="ConvertOptions.CommonSource"/> (the ShaderToy "Common" tab) prepended so its helpers
/// are available to the image tab.
/// </summary>
public sealed class OptionsTests
{
    private const string MinimalImage = """
    void mainImage(out vec4 fragColor, in vec2 fragCoord)
    {
        fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
    }
    """;

    [Fact]
    public void EffectName_FlowsIntoEmittedFx()
    {
        ConvertResult result = ShaderToyConverter.Convert(
            MinimalImage, new ConvertOptions { EffectName = "MyCustomEffect" });

        result.Success.ShouldBeTrue();
        result.Fx!.ShouldContain("MyCustomEffect", Case.Sensitive);
    }

    [Fact]
    public void TechniqueName_UsedInTechniqueBlock()
    {
        ConvertResult result = ShaderToyConverter.Convert(
            MinimalImage, new ConvertOptions { TechniqueName = "MyTechnique" });

        result.Success.ShouldBeTrue();
        result.Fx!.ShouldContain("technique MyTechnique", Case.Sensitive);
        result.Fx!.ShouldNotContain("technique ShaderToy", Case.Sensitive);
    }

    [Fact]
    public void DefaultTechniqueName_IsShaderToy()
    {
        ConvertResult result = ShaderToyConverter.Convert(MinimalImage);

        result.Success.ShouldBeTrue();
        result.Fx!.ShouldContain("technique ShaderToy", Case.Sensitive);
    }

    [Fact]
    public void CommonSource_IsAvailableToImageTab()
    {
        // A helper defined in the Common tab and called from the image tab. The conversion must
        // succeed (the helper resolves) and the helper must appear in the emitted .fx.
        const string common = "float commonHelper(float x) { return x * 2.0; }";
        const string image = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float v = commonHelper(fragCoord.x);
            fragColor = vec4(v, v, v, 1.0);
        }
        """;

        ConvertResult result = ShaderToyConverter.Convert(
            image, new ConvertOptions { CommonSource = common });

        result.Success.ShouldBeTrue(string.Format(
            "the image tab references a Common-tab helper; diagnostics: {0}",
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
        result.Fx!.ShouldContain("commonHelper", Case.Sensitive);
    }

    [Fact]
    public void CommonSource_HelperEmittedBeforeMainImage()
    {
        const string common = "float commonHelper(float x) { return x * 2.0; }";
        const string image = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(commonHelper(fragCoord.x), 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult result = ShaderToyConverter.Convert(
            image, new ConvertOptions { CommonSource = common });

        result.Success.ShouldBeTrue();
        string fx = result.Fx!;
        int helperIdx = fx.IndexOf("float commonHelper", StringComparison.Ordinal);
        int mainIdx = fx.IndexOf("void mainImage", StringComparison.Ordinal);
        helperIdx.ShouldBeGreaterThan(-1);
        mainIdx.ShouldBeGreaterThan(-1);
        helperIdx.ShouldBeLessThan(mainIdx, customMessage: "the Common tab is prepended before the image tab");
    }
}
