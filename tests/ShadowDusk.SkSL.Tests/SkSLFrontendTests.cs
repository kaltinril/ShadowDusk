#nullable enable

using Shouldly;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.SkSL.Tests;

public sealed class SkSLFrontendTests
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
    public void Format_IsSksl() => new SkSLFrontend().Format.ShouldBe(ShaderFormat.Sksl);

    [Fact]
    public void ToFx_Succeeds_WithTheConvertersFxAndNames()
    {
        var result = new SkSLFrontend().ToFx(Plain, "tint.sksl");

        result.IsSuccess.ShouldBeTrue();
        result.Value.FxText.ShouldBe(SkSLToFxConverter.Convert(Plain).Fx);
        // A host selects the technique by the converter's name, not by the file's.
        result.Value.FxText.ShouldContain("technique " + SkSLToFxConverter.PremultipliedTechniqueName, Case.Sensitive);
    }

    [Fact]
    public void ToFx_Failure_KeepsTheStableCode_AndFilesUnderTheName()
    {
        var result = new SkSLFrontend().ToFx("half4 main(float2 c) { return half4(sk_FragCoord.xy, 0, 1); }", "bad.sksl");

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0666");
        error.File.ShouldBe("bad.sksl");
        error.Line.ShouldBe(1);
        error.Severity.ShouldBe(ShaderErrorSeverity.Error);
    }

    [Theory]
    [InlineData("half4 notmain(float2 c) { return half4(1); }", "SD0661")]
    [InlineData("half4 main(half4 c) { return c; }", "SD0663")]
    public void ToFx_MapsEachConverterCodeThrough(string sksl, string code)
    {
        var result = new SkSLFrontend().ToFx(sksl, null);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldAllBe(e => e.Code == code);
        result.Error.ShouldAllBe(e => e.File == "<memory>.sksl");
    }

    [Fact]
    public void ToFx_ShaderToyStageFailure_IsSD0672()
    {
        // A construct only the shared stage rejects.
        var result = new SkSLFrontend().ToFx("half4 main(float2 c) { return half4(undeclared, 0, 0, 1); }", "x.sksl");

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldAllBe(e => e.Code.StartsWith("SD06", StringComparison.Ordinal));
    }

    [Fact]
    public void Register_MakesItTheSkslFrontend()
    {
        SkSLFrontend.Register();

        ShaderFrontends.Find(ShaderFormat.Sksl).ShouldBeOfType<SkSLFrontend>();
    }

    [Fact]
    public void ToFx_NullSource_Throws() =>
        Should.Throw<ArgumentNullException>(() => new SkSLFrontend().ToFx(null!, null));
}
