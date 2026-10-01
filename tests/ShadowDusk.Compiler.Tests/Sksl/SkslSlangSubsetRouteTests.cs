#nullable enable

using ShadowDusk.Compiler.Sksl;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.Sksl;

/// <summary>
/// Slang input to SkSL through ShadowDusk.Compiler's built-in HLSL-compatible-subset frontend
/// (<see cref="SkslConverter.ConvertSlang"/>), judged by real SkiaSharp renders against the
/// <c>.fx</c>-sourced SkSL of the same effect. The real-slangc twin of this class is
/// <c>ShadowDusk.Slang.Tests.SlangSkslRouteTests</c>.
/// </summary>
public sealed class SkslSlangSubsetRouteTests
{
    private static readonly SkslTwinHarness.SlangRoute Route =
        (source, options) => SkslConverter.ConvertSlang(source, options);

    public static IEnumerable<object[]> TwinNames() =>
        SkslTwinHarness.Cases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(TwinNames))]
    public void SlangSourcedSksl_RendersLikeTheFxSourcedSksl(string name) =>
        SkslTwinHarness.AssertParity(SkslTwinHarness.Get(name), Route);

    [Theory]
    [MemberData(nameof(TwinNames))]
    public void PositiveControl_AMutatedSlangTwin_FallsOutsideTheTolerance(string name) =>
        SkslTwinHarness.AssertMutatedTwinDiverges(SkslTwinHarness.Get(name), Route);

    [Fact]
    public void GumGrayscaleTwin_ConvertsItsVertexColor_ToTheSameSynthesizedUniformAsItsFx()
    {
        SlangTwinCase twin = SkslTwinHarness.Get("GumGrayscale");

        var result = SkslConverter.ConvertSlang(SkslTwinHarness.ReadSlang(twin),
            new SkslConvertOptions { SourceName = "GumGrayscale.slang" });

        SkslConversion conversion = SkslTwinHarness.Succeeded(result);
        conversion.SynthesizedUniforms.ShouldContain("ShadowDusk_Color");
    }

    [Fact]
    public void SlangOnlyConstructs_AreRefusedByName()
    {
        const string slang = """
            import lighting;
            [shader("fragment")]
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return 1; }
            """;

        var result = SkslConverter.ConvertSlang(slang, new SkslConvertOptions { SourceName = "bad.slang" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0600");
    }

    [Fact]
    public void AVertexEntry_IsRefusedWithTheSkslCode()
    {
        const string slang = """
            struct V { float4 Position : SV_Position; };
            [shader("vertex")]
            V MainVS(float4 p : POSITION) { V v; v.Position = p; return v; }
            [shader("fragment")]
            float4 MainPS(V v) : SV_Target { return 1; }
            """;

        var result = SkslConverter.ConvertSlang(slang, new SkslConvertOptions { SourceName = "vs.slang" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0610");
    }
}
