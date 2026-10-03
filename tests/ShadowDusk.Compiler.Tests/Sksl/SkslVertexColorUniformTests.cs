#nullable enable

using ShadowDusk.Compiler.Sksl;
using SkiaSharp;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.Sksl;

/// <summary>
/// Issue #368 (Phase 62 Area D1): COLOR0, SpriteBatch's vertex color, converts BY DEFAULT to the
/// synthesized uniform <c>ShadowDusk_Color</c>. The shaders under test are XnaFiddle's examples
/// (vendored under <c>third-party/XnaFiddle</c>), which Gum authors reuse on KNI and Skia: every
/// one reads <c>input.Color</c>. Three claims, each against real SkiaSharp (CPU raster, no GPU):
/// Skia's own compiler accepts the emission; a non-white tint is honored; white reproduces the
/// untinted math. A positive control proves the tint assertions can fail.
/// </summary>
public sealed class SkslVertexColorUniformTests
{
    private const int Size = 16;
    private const int Tolerance = 2;

    private static readonly (float R, float G, float B, float A) Texel = (0.8f, 0.4f, 0.2f, 1f);
    private static readonly (float R, float G, float B, float A) Tint = (0.5f, 1f, 0.25f, 1f);
    private static readonly (float R, float G, float B, float A) White = (1f, 1f, 1f, 1f);

    private static string Fixture(string name) =>
        File.ReadAllText(SkslConverterTests.FindFixture("third-party", "XnaFiddle", name));

    private static SkslConversion Convert(string name)
    {
        var converted = SkslConverter.Convert(Fixture(name), new SkslConvertOptions { SourceName = name });
        converted.IsSuccess.ShouldBeTrue(
            converted.IsFailure ? string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        return converted.Value;
    }

    /// <summary>
    /// The HLSL math of each shader, computed by hand, as the RGB the shader RETURNS at the center
    /// pixel (Skia reads a runtime effect's return as premultiplied; with an opaque-black
    /// background and a source alpha that is the shader's own, the stored RGB is that return).
    /// </summary>
    private static (float R, float G, float B) Expected(
        string shader, (float R, float G, float B, float A) tint, (float R, float G, float B, float A) tintColor)
    {
        float r = Texel.R * tint.R, g = Texel.G * tint.G, b = Texel.B * tint.B;
        switch (shader)
        {
            case "XnaFiddle-Grayscale.fx":
                float avg = (r + g + b) / 3f;
                return (avg, avg, avg);
            case "XnaFiddle-Invert.fx":
                return (1f - r, 1f - g, 1f - b);
            case "XnaFiddle-Tint.fx":
                return (r * tintColor.R, g * tintColor.G, b * tintColor.B);
            case "XnaFiddle-Fading.fx":
                float f = ((Size / 2 + 0.5f) / Size) * 2f - 0.4f; // uv.y at the center pixel
                return (r * f, g * f, b * f);
            case "XnaFiddle-Mask.fx": // the mask child is solid white here, so it keeps the pixel
                return (r, g, b);
            default:
                throw new ArgumentException(shader);
        }
    }

    public static TheoryData<string> Shaders => new()
    {
        "XnaFiddle-Fading.fx",
        "XnaFiddle-Grayscale.fx",
        "XnaFiddle-Invert.fx",
        "XnaFiddle-Tint.fx",
        "XnaFiddle-Mask.fx",
    };

    [Theory]
    [MemberData(nameof(Shaders))]
    public void XnaFiddleShader_ConvertsByDefault_AndSkiasOwnCompilerAcceptsIt(string shader)
    {
        SkslConversion conversion = Convert(shader);

        conversion.SynthesizedUniforms.ShouldContain("ShadowDusk_Color");
        conversion.SkslText.ShouldContain("uniform vec4 ShadowDusk_Color;", Case.Sensitive);
        conversion.SkslText.ShouldNotContain("in_var_COLOR", Case.Sensitive);
        conversion.Warnings.ShouldContain(w => w.Code == "SD0614" && w.Message.Contains("ShadowDusk_Color"));

        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(conversion.SkslText, out string errors);
        effect.ShouldNotBeNull($"Skia's own compiler rejected the emission:\n{errors}\n--- SkSL ---\n{conversion.SkslText}");
        errors.ShouldBeNullOrEmpty();
    }

    [Theory]
    [MemberData(nameof(Shaders))]
    public void XnaFiddleShader_HonorsANonWhiteTint(string shader)
    {
        (byte r, byte g, byte b) = Render(shader, Tint);

        AssertMatches((r, g, b), Expected(shader, Tint, TintColorUniform));
    }

    [Theory]
    [MemberData(nameof(Shaders))]
    public void XnaFiddleShader_WithWhiteTint_ReproducesTheUntintedMath(string shader)
    {
        (byte r, byte g, byte b) = Render(shader, White);

        // The untinted HLSL: input.Color is 1, so the expectation is the shader's own math.
        AssertMatches((r, g, b), Expected(shader, White, TintColorUniform));
    }

    [Theory]
    [MemberData(nameof(Shaders))]
    public void PositiveControl_ATintTheShaderIgnores_IsCaughtByTheTintAssertion(string shader)
    {
        // Fails if the tint assertion is blind to the uniform. (1) The tinted and untinted
        // expectations must be distinguishable. (2) Rendering with the uniform left at 0 (what an
        // emission that ignored it, or a consumer that never set it, produces) must NOT match
        // the tinted expectation.
        var tinted = Expected(shader, Tint, TintColorUniform);
        var untinted = Expected(shader, White, TintColorUniform);
        MaxDiff(tinted, untinted).ShouldBeGreaterThan(Tolerance,
            "test bug: the tint must make the tinted and untinted expectations distinguishable");

        (byte r, byte g, byte b) unset = Render(shader, tint: null);
        MaxDiff(((float)unset.r / 255, (float)unset.g / 255, (float)unset.b / 255), tinted)
            .ShouldBeGreaterThan(Tolerance,
                "the render with ShadowDusk_Color unset still matches the tinted expectation: the tint assertion cannot fail");
    }

    [Fact]
    public void XnaFiddlePixelated_Converts_WithoutAColorUniform()
    {
        // Pixelated never reads input.Color (DXC drops the dead input), so it gets no
        // ShadowDusk_Color. It samples at round()-quantized coordinates, which converts through
        // ShadowDusk_Resolution since issue #371 (SkslComputedUvSamplingTests holds the render).
        SkslConversion conversion = Convert("XnaFiddle-Pixelated.fx");

        conversion.SynthesizedUniforms.ShouldNotContain("ShadowDusk_Color");
        conversion.SynthesizedUniforms.ShouldContain("ShadowDusk_Resolution");
    }

    // TintColor (XnaFiddle-Tint.fx's own cbuffer uniform) is set to this so the Tint shader's
    // result differs from the plain tint math.
    private static readonly (float R, float G, float B, float A) TintColorUniform = (1f, 0.5f, 0.75f, 1f);

    /// <summary>
    /// Renders <paramref name="shader"/> at the center pixel with <c>ShadowDusk_Color</c> set to
    /// <paramref name="tint"/> (<see langword="null"/>: left unset, i.e. zero).
    /// </summary>
    private static (byte R, byte G, byte B) Render(string shader, (float R, float G, float B, float A)? tint)
    {
        SkslConversion conversion = Convert(shader);
        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(conversion.SkslText, out string errors);
        effect.ShouldNotBeNull(errors);

        var uniforms = new SKRuntimeEffectUniforms(effect);
        if (tint is { } t)
            uniforms[SkslGlslMapper.ColorUniform] = new[] { t.R, t.G, t.B, t.A };
        if (conversion.SynthesizedUniforms.Contains(SkslGlslMapper.ResolutionUniform))
            uniforms[SkslGlslMapper.ResolutionUniform] = new[] { (float)Size, Size };
        if (shader == "XnaFiddle-Tint.fx")
        {
            uniforms["TintColor"] = new[]
            {
                TintColorUniform.R, TintColorUniform.G, TintColorUniform.B, TintColorUniform.A,
            };
        }

        // Children: every `uniform shader` is a solid-color full-canvas texture, so .eval(coord)
        // reads one texel everywhere. The mask is solid white (mask = 1: keeps the pixel).
        var children = new SKRuntimeEffectChildren(effect);
        var owned = new List<IDisposable>();
        try
        {
            foreach (string child in conversion.ChildShaders)
            {
                SKColor color = child == "MaskTexture"
                    ? SKColors.White
                    : new SKColor(
                        (byte)Math.Round(Texel.R * 255), (byte)Math.Round(Texel.G * 255),
                        (byte)Math.Round(Texel.B * 255));
                var bitmap = new SKBitmap(Size, Size);
                bitmap.Erase(color);
                SKShader childShader = bitmap.ToShader();
                owned.Add(bitmap);
                owned.Add(childShader);
                children[child] = childShader;
            }

            using SKShader shader2 = effect.ToShader(uniforms, children);
            using var target = new SKBitmap(Size, Size);
            target.Erase(SKColors.Black);
            using (var canvas = new SKCanvas(target))
            using (var paint = new SKPaint { Shader = shader2 })
                canvas.DrawRect(new SKRect(0, 0, Size, Size), paint);

            SKColor px = target.GetPixel(Size / 2, Size / 2);
            return (px.Red, px.Green, px.Blue);
        }
        finally
        {
            foreach (IDisposable d in owned)
                d.Dispose();
        }
    }

    private static float MaxDiff((float R, float G, float B) a, (float R, float G, float B) b) =>
        new[] { Math.Abs(a.R - b.R), Math.Abs(a.G - b.G), Math.Abs(a.B - b.B) }.Max() * 255f;

    private static void AssertMatches((byte R, byte G, byte B) actual, (float R, float G, float B) expected)
    {
        Assert255(actual.R, expected.R, "R");
        Assert255(actual.G, expected.G, "G");
        Assert255(actual.B, expected.B, "B");
    }

    private static void Assert255(byte actual, float expected, string channel) =>
        Math.Abs(actual - (int)Math.Round(Math.Clamp(expected, 0f, 1f) * 255)).ShouldBeLessThanOrEqualTo(Tolerance,
            $"{channel}: expected ~{(int)Math.Round(Math.Clamp(expected, 0f, 1f) * 255)}, rendered {actual} " +
            $"(tolerance ±{Tolerance}: SkSL half precision)");
}
