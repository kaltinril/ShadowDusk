#nullable enable

using ShadowDusk.Compiler.Sksl;
using SkiaSharp;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.Sksl;

/// <summary>
/// Issue #371 (Phase 62 Area D): sampling at a COMPUTED UV converts. HLSL's <c>tex2D</c> takes a
/// normalized coordinate, SkSL's <c>.eval()</c> takes child pixels, so the converter emits
/// <c>child.eval((uv) * ShadowDusk_Resolution)</c> (the child's pixel size, the same uniform the
/// arithmetic-UV path already synthesizes). The shader under test is XnaFiddle's Pixelated, which
/// samples at <c>round()</c>-quantized coordinates. Real SkiaSharp (CPU raster, no GPU) renders the
/// emission over a known image and the result is compared with the ORIGINAL HLSL's math computed
/// here, at +-2/255.
/// </summary>
public sealed class SkslComputedUvSamplingTests
{
    private const int Tolerance = 2;
    private const string Fixture = "XnaFiddle-Pixelated.fx";

    // Pixelated's own constants (pixels = 128 is its sampling resolution, pixelation = 4 its block).
    private const float Pixels = 128f;
    private const float Block = 4f;

    public static TheoryData<int, int> ImageSizes => new()
    {
        { 128, 128 },
        { 96, 160 }, // non-square: pins that the scale is applied per axis
    };

    private static SkslConversion ConvertPixelated()
    {
        string fx = File.ReadAllText(SkslConverterTests.FindFixture("third-party", "XnaFiddle", Fixture));
        var converted = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = Fixture });
        converted.IsSuccess.ShouldBeTrue(
            converted.IsFailure ? string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        return converted.Value;
    }

    [Fact]
    public void Pixelated_Converts_ScalingTheComputedUvByTheChildPixelSize()
    {
        SkslConversion conversion = ConvertPixelated();

        conversion.SynthesizedUniforms.ShouldContain("ShadowDusk_Resolution");
        conversion.SynthesizedUniforms.ShouldNotContain("ShadowDusk_Color"); // Pixelated never reads input.Color
        conversion.SkslText.ShouldContain("uniform vec2 ShadowDusk_Resolution;", Case.Sensitive);
        conversion.SkslText.ShouldContain(") * ShadowDusk_Resolution)", Case.Sensitive);
        conversion.SkslText.ShouldNotContain("texture(", Case.Sensitive);
        conversion.Warnings.ShouldContain(w => w.Code == "SD0614" && w.Message.Contains("ShadowDusk_Resolution"));
        conversion.ChildShaders.ShouldBe(["SpriteTexture"]);
    }

    [Fact]
    public void Pixelated_Emission_IsAcceptedBySkiasOwnCompiler()
    {
        SkslConversion conversion = ConvertPixelated();

        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(conversion.SkslText, out string errors);
        effect.ShouldNotBeNull($"Skia's own compiler rejected the emission:\n{errors}\n--- SkSL ---\n{conversion.SkslText}");
        errors.ShouldBeNullOrEmpty();
    }

    [Theory]
    [MemberData(nameof(ImageSizes))]
    public void Pixelated_RendersTheHlslQuantizedSampling(int width, int height)
    {
        SKColor[] actual = Render(width, height, resolution: (width, height));
        SKColor[] expected = Expected(width, height);

        MaxChannelDiff(actual, expected).ShouldBeLessThanOrEqualTo(Tolerance,
            "the render diverges from the original HLSL's quantized-sampling math: " + FirstMismatch(actual, expected, width));
    }

    [Theory]
    [MemberData(nameof(ImageSizes))]
    public void PositiveControl_AWrongOrUnsetResolution_IsCaught(int width, int height)
    {
        SKColor[] expected = Expected(width, height);

        // The expectation must itself be a real signal: pixelated, not flat.
        expected.Distinct().Count().ShouldBeGreaterThan(100, "test bug: the expected image is nearly flat");

        // Unset (zero): every eval reads (0,0). Half the size: samples the wrong texels. A wrong
        // axis (swapped W/H) on the non-square image.
        MaxChannelDiff(Render(width, height, resolution: null), expected).ShouldBeGreaterThan(Tolerance,
            "an unset ShadowDusk_Resolution still matches: the assertion cannot fail");
        MaxChannelDiff(Render(width, height, resolution: (width / 2, height / 2)), expected).ShouldBeGreaterThan(Tolerance,
            "a wrong ShadowDusk_Resolution still matches: the assertion cannot fail");
        if (width != height)
        {
            MaxChannelDiff(Render(width, height, resolution: (height, width)), expected).ShouldBeGreaterThan(Tolerance,
                "swapped axes still match: the scale is not applied per axis");
        }
    }

    // ---- what still refuses -------------------------------------------------------------------

    private const string Header = """
        Texture2D Tex;
        sampler2D TexSampler = sampler_state { Texture = <Tex>; };
        """;

    private static ShadowDusk.Core.Result<MappedSksl, ShadowDusk.Core.ShaderError[]> MapGlsl(string mainBody) =>
        SkslGlslMapper.Map(
            "#version 450\nuniform sampler2D Tex;\nuniform sampler2D Other;\nin vec2 in_var_TEXCOORD0;\n" +
            "layout(location = 0) out vec4 out_var_SV_Target;\nvoid main()\n{\n    " + mainBody + "\n}\n",
            ["Tex", "Other"], new HashSet<string>(), "map.glsl");

    [Fact]
    public void ABiasedSample_IsRefusedByName_NotConvertedWithItsBiasDropped()
    {
        // The .fx front end refuses tex2Dbias before the mapper (FX0012); a GLSL three-argument
        // texture() reaching the mapper directly must still stop at SD0612.
        var result = MapGlsl("out_var_SV_Target = texture(Tex, in_var_TEXCOORD0, 1.5);");

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0612");
        result.Error.Single().Message.ShouldContain("texture(Tex, in_var_TEXCOORD0, 1.5)", Case.Sensitive);
    }

    [Fact]
    public void ASampleOfAnUnboundSampler_IsRefusedByName()
    {
        var result = MapGlsl("out_var_SV_Target = texture(Missing, in_var_TEXCOORD0 * 2.0);");

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0612");
        result.Error.Single().Message.ShouldContain("Missing", Case.Sensitive);
    }

    [Fact]
    public void AnUnterminatedSamplingCall_IsRefusedNotMangled()
    {
        var result = MapGlsl("out_var_SV_Target = texture(Tex, vec2(in_var_TEXCOORD0.x, 0.5);");

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0612");
    }

    [Fact]
    public void ABiasedSampleNestedInsideAConvertibleOne_IsStillRefused()
    {
        var result = MapGlsl(
            "out_var_SV_Target = texture(Tex, texture(Other, in_var_TEXCOORD0, 2.0).xy);");

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0612");
    }

    [Fact]
    public void AComputedSampleOfASecondTexture_ScalesWithTheSameUniform()
    {
        var result = MapGlsl("out_var_SV_Target = texture(Other, in_var_TEXCOORD0 * 0.5 + vec2(0.25));");

        result.IsSuccess.ShouldBeTrue();
        result.Value.SynthesizedUniforms.ShouldBe(["ShadowDusk_Resolution"]);
        result.Value.SkslText.ShouldContain(
            "Other.eval((_sd_uv * 0.5 + vec2(0.25)) * ShadowDusk_Resolution)", Case.Sensitive);
    }

    [Fact]
    public void TheInterpolatedUvSampleIsStillTheOneToOneEvalOfCoord()
    {
        var result = MapGlsl("out_var_SV_Target = texture(Tex, in_var_TEXCOORD0);");

        result.IsSuccess.ShouldBeTrue();
        result.Value.SynthesizedUniforms.ShouldBeEmpty();
        result.Value.SkslText.ShouldContain("Tex.eval(coord)", Case.Sensitive);
        result.Value.SkslText.ShouldNotContain("ShadowDusk_Resolution", Case.Sensitive);
    }

    [Fact]
    public void RoundEven_GetsAnExactTiesToEvenHelper_BecauseSkslHasNone()
    {
        // Ties: 1.5 -> 2, 2.5 -> 2, 3.5 -> 4 (floor(x + 0.5) would give 2, 3, 4).
        var result = MapGlsl(
            "out_var_SV_Target = vec4(roundEven(vec3(1.5, 2.5, 3.5)) / 4.0, 1.0);");

        result.IsSuccess.ShouldBeTrue();
        result.Value.SkslText.ShouldContain("_sd_roundEven(", Case.Sensitive);
        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(result.Value.SkslText, out string errors);
        effect.ShouldNotBeNull($"{errors}\n{result.Value.SkslText}");

        using SKShader dummy = SKShader.CreateColor(SKColors.White);
        var children = new SKRuntimeEffectChildren(effect);
        children["Tex"] = dummy;
        children["Other"] = dummy;
        using SKShader shader = effect.ToShader(new SKRuntimeEffectUniforms(effect), children);
        using var target = new SKBitmap(1, 1);
        using (var canvas = new SKCanvas(target))
        using (var paint = new SKPaint { Shader = shader })
            canvas.DrawRect(new SKRect(0, 0, 1, 1), paint);

        SKColor px = target.GetPixel(0, 0);
        Math.Abs(px.Red - 128).ShouldBeLessThanOrEqualTo(Tolerance);
        Math.Abs(px.Green - 128).ShouldBeLessThanOrEqualTo(Tolerance);
        px.Blue.ShouldBe((byte)255);
    }

    [Fact]
    public void AnOffsetSample_IsRefusedNeverSilentlyDropped()
    {
        // A sample with an offset has no SkSL meaning and is refused (SD0613 for textureOffset,
        // SD0612 for any other extra-argument form): never a silent drop of the extra argument.
        string fx = Header + """

            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Tex.Sample(TexSampler, uv, int2(1, 0));
            }
            technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
            """;

        var result = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = "off.fx" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBeOneOf("SD0612", "SD0613");
    }

    [Fact]
    public void ADoubleSample_ConvertsBothCallsInnermostFirst()
    {
        // The second sample's coordinate is the first sample's result: nested texture() calls.
        string fx = Header + """

            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                float2 warped = tex2D(TexSampler, uv).xy * 0.5;
                return tex2D(TexSampler, warped + uv * 0.25);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        var result = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = "nest.fx" });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        result.Value.SkslText.ShouldNotContain("texture(", Case.Sensitive);
        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(result.Value.SkslText, out string errors);
        effect.ShouldNotBeNull($"{errors}\n{result.Value.SkslText}");
    }

    // ---- rendering ----------------------------------------------------------------------------

    private static SKColor Source(int x, int y) =>
        new((byte)((x * 2) & 255), (byte)((y * 2) & 255), (byte)((x * 5 + y * 3) & 255), 255);

    /// <summary>
    /// The ORIGINAL HLSL's math: <c>coord = round(uv * 128 / 4) * 4 / 128</c>, then a BILINEAR read
    /// of the image at <c>coord</c> (what SpriteBatch's default LinearClamp sampler does), clamped to
    /// the image. The quantized coordinates land exactly on texel boundaries, where a linear read is
    /// the exact 50/50 mean of the two straddling texels; a nearest read there is an implementation
    /// artefact (Skia picks the lower texel, GL hardware may pick either), so it is not the oracle.
    /// </summary>
    private static SKColor[] Expected(int width, int height)
    {
        var pixels = new SKColor[width * height];
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                float u = (px + 0.5f) / width, v = (py + 0.5f) / height;
                float qx = MathF.Round(u * Pixels / Block, MidpointRounding.ToEven) * Block / Pixels;
                float qy = MathF.Round(v * Pixels / Block, MidpointRounding.ToEven) * Block / Pixels;
                pixels[py * width + px] = Bilinear(qx * width, qy * height, width, height);
            }
        }
        return pixels;
    }

    private static SKColor Bilinear(float x, float y, int width, int height)
    {
        float fx = x - 0.5f, fy = y - 0.5f; // texel centers sit at +0.5
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        var channels = new float[3];
        for (int dy = 0; dy < 2; dy++)
        {
            for (int dx = 0; dx < 2; dx++)
            {
                float w = (dx == 0 ? 1 - tx : tx) * (dy == 0 ? 1 - ty : ty);
                SKColor c = Source(Math.Clamp(x0 + dx, 0, width - 1), Math.Clamp(y0 + dy, 0, height - 1));
                channels[0] += w * c.Red;
                channels[1] += w * c.Green;
                channels[2] += w * c.Blue;
            }
        }
        return new SKColor((byte)MathF.Round(channels[0]), (byte)MathF.Round(channels[1]),
            (byte)MathF.Round(channels[2]), 255);
    }

    private static SKColor[] Render(int width, int height, (int W, int H)? resolution)
    {
        SkslConversion conversion = ConvertPixelated();
        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(conversion.SkslText, out string errors);
        effect.ShouldNotBeNull(errors);

        var uniforms = new SKRuntimeEffectUniforms(effect);
        if (resolution is { } r)
            uniforms[SkslGlslMapper.ResolutionUniform] = new[] { (float)r.W, r.H };
        if (conversion.SynthesizedUniforms.Contains(SkslGlslMapper.ColorUniform))
            uniforms[SkslGlslMapper.ColorUniform] = new[] { 1f, 1f, 1f, 1f };

        using var image = new SKBitmap(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image.SetPixel(x, y, Source(x, y));

        using SKShader child = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        var children = new SKRuntimeEffectChildren(effect);
        children[conversion.ChildShaders.Single()] = child;

        using SKShader shader = effect.ToShader(uniforms, children);
        using var target = new SKBitmap(width, height);
        target.Erase(SKColors.Black);
        using (var canvas = new SKCanvas(target))
        using (var paint = new SKPaint { Shader = shader })
            canvas.DrawRect(new SKRect(0, 0, width, height), paint);

        var pixels = new SKColor[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = target.GetPixel(x, y);
        return pixels;
    }

    private static string FirstMismatch(SKColor[] a, SKColor[] b, int width)
    {
        var bad = new List<string>();
        int count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Max(Math.Abs(a[i].Red - b[i].Red), Math.Max(Math.Abs(a[i].Green - b[i].Green),
                    Math.Abs(a[i].Blue - b[i].Blue))) <= Tolerance)
                continue;
            count++;
            if (bad.Count < 6)
                bad.Add($"({i % width},{i / width}) rendered {a[i]} expected {b[i]}");
        }
        return $"{count} of {a.Length} pixels differ; first: " + string.Join("; ", bad);
    }

    private static int MaxChannelDiff(SKColor[] a, SKColor[] b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Abs(a[i].Red - b[i].Red));
            max = Math.Max(max, Math.Abs(a[i].Green - b[i].Green));
            max = Math.Max(max, Math.Abs(a[i].Blue - b[i].Blue));
        }
        return max;
    }
}
