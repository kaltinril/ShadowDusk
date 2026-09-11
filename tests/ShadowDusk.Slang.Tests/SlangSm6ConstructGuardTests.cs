#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Pure tests (no disk, no process) for <see cref="SlangSm6ConstructGuard"/> — Phase 66 A5,
/// Band 2 part B (OQ2). The regex it scans with is hand-duplicated from
/// <see cref="SlangSm6ConstructGuard.WaveAndQuadIntrinsics"/> (a source-generated regex's
/// pattern must be a compile-time literal, so it cannot be built from the array directly);
/// <see cref="EveryListedIntrinsic_IsDetectedByTheRegex"/> is what keeps the two from drifting.
/// </summary>
public sealed class SlangSm6ConstructGuardTests
{
    [Theory]
    [InlineData("float v = WaveActiveSum(x);", "WaveActiveSum")]
    [InlineData("bool b = WaveIsFirstLane();", "WaveIsFirstLane")]
    [InlineData("float q = QuadReadAcrossX(x);", "QuadReadAcrossX")]
    public void FindConstruct_DetectsAKnownSm6Intrinsic(string source, string expected)
    {
        var hit = SlangSm6ConstructGuard.FindConstruct(source);

        hit.ShouldNotBeNull();
        hit!.Value.Construct.ShouldBe(expected);
    }

    [Fact]
    public void FindConstruct_ReportsThe1BasedLineOfTheFirstMatch()
    {
        const string source = "float4 MainPS()\n{\n    float v = WaveActiveSum(1.0);\n    return 0;\n}\n";

        var hit = SlangSm6ConstructGuard.FindConstruct(source);

        hit.ShouldNotBeNull();
        hit!.Value.Line.ShouldBe(3);
    }

    [Fact]
    public void FindConstruct_NoMatch_ReturnsNull()
    {
        const string source = "float4 MainPS() : SV_Target { return SpriteTexture.Sample(SpriteSampler, uv); }";

        SlangSm6ConstructGuard.FindConstruct(source).ShouldBeNull();
    }

    // A plain identifier that merely CONTAINS "Wave" (the WaveVertex.slang corpus fixture's own
    // 'float2 Wave;' field, real Phase 66 A3/A4 corpus shape) must not false-positive — the
    // guard matches whole identifiers from the closed intrinsic vocabulary only, not a "Wave"
    // prefix.
    [Fact]
    public void FindConstruct_PlainIdentifierNamedWave_IsNotFlagged()
    {
        const string source = "cbuffer Params { float2 Wave; }\n" +
                               "float f(float2 p) { return sin(p.x * Wave.x) * Wave.y; }";

        SlangSm6ConstructGuard.FindConstruct(source).ShouldBeNull();
    }

    [Fact]
    public void EveryListedIntrinsic_IsDetectedByTheRegex()
    {
        foreach (string name in SlangSm6ConstructGuard.WaveAndQuadIntrinsics)
        {
            var hit = SlangSm6ConstructGuard.FindConstruct($"void F() {{ {name}(0); }}");
            hit.ShouldNotBeNull($"'{name}' is listed in WaveAndQuadIntrinsics but the regex did not match it");
            hit!.Value.Construct.ShouldBe(name);
        }
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL, true)]
    [InlineData(PlatformTarget.DirectX, true)]
    [InlineData(PlatformTarget.Fna, true)]
    [InlineData(PlatformTarget.Vulkan, false)]
    [InlineData(PlatformTarget.DirectX12, false)]
    public void IsArchitecturallyBelowSm6_MatchesTheMeasuredPerTargetCeiling(
        PlatformTarget target, bool expected)
    {
        SlangSm6ConstructGuard.IsArchitecturallyBelowSm6(target).ShouldBe(expected);
    }
}
