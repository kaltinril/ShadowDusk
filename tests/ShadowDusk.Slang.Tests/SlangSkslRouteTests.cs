#nullable enable

using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Compiler.Tests.Sksl;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Slang input to SkSL and to raylib through the <b>real-slangc route</b>
/// (<see cref="SlangCompiler.ConvertToSksl"/>, <see cref="SlangCompiler.ConvertToRaylib"/>), judged
/// by the same real-SkiaSharp renders as the subset route
/// (<c>ShadowDusk.Compiler.Tests.Sksl.SkslSlangSubsetRouteTests</c>). Every test spawns the real,
/// restored slangc and FAILS, never skips, on a host without one, so a green run on any OS means
/// the route ran there.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangSkslRouteTests
{
    private static readonly SkslTwinHarness.SlangRoute Route =
        (source, options) => new SlangCompiler().ConvertToSksl(source, options);

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

        var result = new SlangCompiler().ConvertToSksl(SkslTwinHarness.ReadSlang(twin),
            new SkslConvertOptions { SourceName = "GumGrayscale.slang" });

        SkslConversion conversion = SkslTwinHarness.Succeeded(result);
        conversion.SynthesizedUniforms.ShouldContain("ShadowDusk_Color");
    }

    // Generics and interfaces are exactly what the subset frontend refuses with SD0600.
    private const string GenericSlang = """
        interface IBlendMode { float3 apply(float3 a, float3 b); }
        struct Multiply : IBlendMode { float3 apply(float3 a, float3 b) { return a * b; } }
        float3 blend<T : IBlendMode>(T mode, float3 a, float3 b) { return mode.apply(a, b); }

        Texture2D SpriteTexture;
        SamplerState SpriteSampler;
        float3 Tint;

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            float4 c = SpriteTexture.Sample(SpriteSampler, uv);
            Multiply m;
            return float4(blend(m, c.rgb, Tint), c.a);
        }
        """;

    [Fact]
    public void GenuineSlang_ConvertsToSksl_AndRendersTheMath_WhereTheSubsetFrontendRefuses()
    {
        var subset = SkslConverter.ConvertSlang(GenericSlang, new SkslConvertOptions { SourceName = "generic.slang" });
        subset.IsFailure.ShouldBeTrue();
        subset.Error.Single().Code.ShouldBe("SD0600");

        var result = new SlangCompiler().ConvertToSksl(GenericSlang, new SkslConvertOptions { SourceName = "generic.slang" });
        SkslConversion conversion = SkslTwinHarness.Succeeded(result);

        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(conversion.SkslText, out string errors);
        effect.ShouldNotBeNull($"Skia rejected the emission:\n{errors}\n{conversion.SkslText}");

        (byte r, byte g, byte b) texel = (200, 100, 50);
        float[] tint = [0.5f, 1.0f, 0.25f];
        using var child = new SKBitmap(SkslTwinHarness.Size, SkslTwinHarness.Size);
        child.Erase(new SKColor(texel.r, texel.g, texel.b));
        using SKShader childShader = child.ToShader();

        var uniforms = new SKRuntimeEffectUniforms(effect) { ["Tint"] = tint };
        var children = new SKRuntimeEffectChildren(effect) { ["SpriteTexture"] = childShader };
        using SKShader shader = effect.ToShader(uniforms, children);
        using var target = new SKBitmap(SkslTwinHarness.Size, SkslTwinHarness.Size);
        using (var canvas = new SKCanvas(target))
        using (var paint = new SKPaint { Shader = shader })
            canvas.DrawRect(new SKRect(0, 0, SkslTwinHarness.Size, SkslTwinHarness.Size), paint);

        SKColor pixel = target.GetPixel(SkslTwinHarness.Size / 2, SkslTwinHarness.Size / 2);
        Math.Abs(pixel.Red - (byte)Math.Round(texel.r * tint[0])).ShouldBeLessThanOrEqualTo(SkslTwinHarness.Tolerance);
        Math.Abs(pixel.Green - (byte)Math.Round(texel.g * tint[1])).ShouldBeLessThanOrEqualTo(SkslTwinHarness.Tolerance);
        Math.Abs(pixel.Blue - (byte)Math.Round(texel.b * tint[2])).ShouldBeLessThanOrEqualTo(SkslTwinHarness.Tolerance);

        // Positive control: a tint that was ignored would leave the texel untouched.
        Math.Abs(pixel.Red - texel.r).ShouldBeGreaterThan(SkslTwinHarness.Tolerance);
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

        var result = new SlangCompiler().ConvertToSksl(slang, new SkslConvertOptions { SourceName = "vs.slang" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0610");
    }

    [Fact]
    public void SlangcSyntaxErrors_SurfaceThroughTheConverter_NotAsAConversionFailure()
    {
        const string slang = """
            [shader("fragment")]
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return float4(uv, 0 }
            """;

        var result = new SlangCompiler().ConvertToSksl(slang, new SkslConvertOptions { SourceName = "broken.slang" });

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotContain(e => e.Code.StartsWith("SD061", StringComparison.Ordinal));
    }

    [Fact]
    public void Raylib_GenuineSlang_ConvertsThroughRealSlangc_WithTheSameBindingContractAsTheSubsetRoute()
    {
        const string tint = """
            Texture2D SpriteTexture;
            SamplerState SpriteTextureSampler;
            float Amount;

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target
            {
                return SpriteTexture.Sample(SpriteTextureSampler, uv) * color * Amount;
            }
            """;
        var options = new RaylibConvertOptions { SourceName = "tint.slang" };

        var real = new SlangCompiler().ConvertToRaylib(tint, options);
        var subset = RaylibConverter.ConvertSlang(tint, options);

        real.IsSuccess.ShouldBeTrue(real.IsFailure ? string.Join(" | ", real.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        subset.IsSuccess.ShouldBeTrue();
        real.Value.FragmentShader.ShouldContain("uniform float Amount;", Case.Sensitive);
        real.Value.FragmentShader.ShouldContain("texture(texture0, fragTexCoord)", Case.Sensitive);
        real.Value.Uniforms.ShouldBe(subset.Value.Uniforms);
        real.Value.Samplers.Select(s => (s.UniformName, s.HlslTextureName, s.BoundByDrawCall))
            .ShouldBe(subset.Value.Samplers.Select(s => (s.UniformName, s.HlslTextureName, s.BoundByDrawCall)));
    }

    [Fact]
    public void Raylib_AnAuthorWrittenSamplerRegister_IsHonored_NotStripped()
    {
        const string slang = """
            Texture2D SpriteTexture;
            SamplerState SpriteTextureSampler : register(s0);

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return SpriteTexture.Sample(SpriteTextureSampler, uv);
            }
            """;

        var result = new SlangCompiler().ConvertToRaylib(slang, new RaylibConvertOptions { SourceName = "reg.slang" });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        // The author reserved s0, so the texture moves off unit 0 exactly as it does for a .fx.
        result.Value.Samplers.Single().BoundByDrawCall.ShouldBeFalse();
    }

    [Fact]
    public void Raylib_AVertexEntry_IsRefusedWithTheRaylibCode()
    {
        const string slang = """
            struct V { float4 Position : SV_Position; };
            [shader("vertex")]
            V MainVS(float4 p : POSITION) { V v; v.Position = p; return v; }
            [shader("fragment")]
            float4 MainPS(V v) : SV_Target { return 1; }
            """;

        var result = new SlangCompiler().ConvertToRaylib(slang, new RaylibConvertOptions { SourceName = "vs.slang" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0631");
    }

    [Fact]
    public async Task AsyncVariants_MatchTheSyncResults()
    {
        SlangTwinCase twin = SkslTwinHarness.Get("Sepia");
        string source = SkslTwinHarness.ReadSlang(twin);
        var compiler = new SlangCompiler();
        var options = new SkslConvertOptions { SourceName = "Sepia.slang" };

        var sksl = await compiler.ConvertToSkslAsync(source, options);
        sksl.IsSuccess.ShouldBeTrue();
        sksl.Value.SkslText.ShouldBe(compiler.ConvertToSksl(source, options).Value.SkslText);

        var raylib = await compiler.ConvertToRaylibAsync(source, new RaylibConvertOptions { SourceName = "Sepia.slang" });
        raylib.IsSuccess.ShouldBeTrue();
    }
}
