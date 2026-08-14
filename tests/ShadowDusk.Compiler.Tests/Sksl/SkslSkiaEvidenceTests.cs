#nullable enable

using ShadowDusk.Compiler.Sksl;
using SkiaSharp;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.Sksl;

/// <summary>
/// The Phase 62 evidence half, at the owner-accepted bar (2026-08-13, recorded in
/// <c>project_decisions.md</c>): the emitted SkSL is judged by whether <b>real Skia</b> accepts
/// and renders it — not by <c>mgfxc</c>-equivalence, which cannot exist for a runtime Skia
/// itself compiles. Two rungs, both against real SkiaSharp with no GPU (Skia's CPU raster):
///
/// <list type="number">
///   <item><b>Acceptance:</b> <c>SKRuntimeEffect.CreateShader</c> — Skia's own compiler — takes
///   the emission with zero errors. The analogue of the Effect-load half of rung 4.</item>
///   <item><b>Fidelity:</b> a real render, pixel-compared against the analytically computed
///   result of the ORIGINAL HLSL's math. Tolerance ±2/255, stated not assumed: SkSL evaluates
///   at <c>half</c> precision and Skia's raster rounds differently from a GL rasterizer —
///   which is exactly why the tolerance cannot be 0 (the same reasoning recorded with the
///   evidence decision).</item>
/// </list>
/// </summary>
public sealed class SkslSkiaEvidenceTests
{
    private const int Size = 16;
    private const int Tolerance = 2;

    [Fact]
    public void GumGrayscale_Emission_IsAcceptedBySkiasOwnCompiler()
    {
        string fxPath = SkslConverterTests.FindFixture("third-party", "Gum", "MonoGameInCode-Grayscale.fx");
        var converted = SkslConverter.Convert(File.ReadAllText(fxPath), new SkslConvertOptions
        {
            SourceName = "Grayscale.fx",
            TreatVaryingsAsUniforms = ["COLOR0"],
        });
        converted.IsSuccess.ShouldBeTrue(
            converted.IsFailure ? string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}")) : "");

        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(
            converted.Value.SkslText, out string errors);

        effect.ShouldNotBeNull(
            $"Skia's own compiler rejected the emission:\n{errors}\n--- SkSL ---\n{converted.Value.SkslText}");
        errors.ShouldBeNullOrEmpty();
    }

    [Fact]
    public void GumGrayscale_RendersTheHlslMath_IncludingTheTintTheHandPortDropped()
    {
        string fxPath = SkslConverterTests.FindFixture("third-party", "Gum", "MonoGameInCode-Grayscale.fx");
        var converted = SkslConverter.Convert(File.ReadAllText(fxPath), new SkslConvertOptions
        {
            SourceName = "Grayscale.fx",
            TreatVaryingsAsUniforms = ["COLOR0"],
        });
        converted.IsSuccess.ShouldBeTrue();

        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(
            converted.Value.SkslText, out string errors);
        effect.ShouldNotBeNull(errors);

        // Child: a solid-color "sprite texture" covering the whole canvas, so .eval(coord)
        // reads the same texel everywhere and the expectation is exact.
        (float r, float g, float b) texel = (0.8f, 0.4f, 0.2f);
        (float r, float g, float b, float a) tint = (0.5f, 1.0f, 1.0f, 1.0f);

        using var childBitmap = new SKBitmap(Size, Size);
        childBitmap.Erase(new SKColor(
            (byte)Math.Round(texel.r * 255), (byte)Math.Round(texel.g * 255),
            (byte)Math.Round(texel.b * 255)));
        using SKShader child = childBitmap.ToShader();

        var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["in_var_COLOR0"] = new[] { tint.r, tint.g, tint.b, tint.a },
        };
        var children = new SKRuntimeEffectChildren(effect) { ["SpriteTexture"] = child };

        using SKShader shader = effect.ToShader(uniforms, children);
        SKColor rendered = RenderCenterPixel(shader);

        // The ORIGINAL HLSL, computed by hand:
        //   color = tex2D(...) * input.Color;  gray = dot(color.rgb, lum);  out = (gray,gray,gray,color.a)
        float cr = texel.r * tint.r, cg = texel.g * tint.g, cb = texel.b * tint.b;
        float gray = cr * 0.299f + cg * 0.587f + cb * 0.114f;
        byte expected = (byte)Math.Round(gray * 255);

        AssertClose(rendered.Red, expected);
        AssertClose(rendered.Green, expected);
        AssertClose(rendered.Blue, expected);
        AssertClose(rendered.Alpha, 255);

        // The positive control that separates us from the hand port: with the tint NON-white,
        // an emission that silently dropped it (Gum's port) would render dot(texel, lum)
        // instead — measurably different from the tinted expectation.
        float grayUntinted = texel.r * 0.299f + texel.g * 0.587f + texel.b * 0.114f;
        Math.Abs(grayUntinted - gray).ShouldBeGreaterThan(0.03f,
            "test bug: the tint must make the tinted and untinted expectations distinguishable");
        Math.Abs(rendered.Red - (byte)Math.Round(grayUntinted * 255)).ShouldBeGreaterThan(Tolerance,
            "the render matches the UNTINTED math — the COLOR0 tint was dropped, the exact " +
            "silent-loss failure this converter exists to prevent");
    }

    [Fact]
    public void UniformGradient_RendersTheLerp_AtEachProbedColumn()
    {
        var converted = SkslConverter.Convert(SkslConverterTests.GradientFx,
            new SkslConvertOptions { SourceName = "grad.fx" });
        converted.IsSuccess.ShouldBeTrue(
            converted.IsFailure ? string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}")) : "");

        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(
            converted.Value.SkslText, out string errors);
        effect.ShouldNotBeNull(
            $"Skia's own compiler rejected the emission:\n{errors}\n--- SkSL ---\n{converted.Value.SkslText}");

        var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["LeftColor"]  = new[] { 1f, 0f, 0f, 1f },
            ["RightColor"] = new[] { 0f, 0f, 1f, 1f },
            [SkslGlslMapper.ResolutionUniform] = new[] { (float)Size, Size },
        };

        using SKShader shader = effect.ToShader(uniforms, new SKRuntimeEffectChildren(effect));
        using var bitmap = new SKBitmap(Size, Size);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            paint.Shader = shader;
            canvas.DrawRect(new SKRect(0, 0, Size, Size), paint);
        }

        foreach (int x in new[] { 0, Size / 2, Size - 1 })
        {
            // The HLSL: lerp(Left, Right, uv.x), with uv.x = (x + 0.5) / Size (pixel centers).
            float t = (x + 0.5f) / Size;
            SKColor pixel = bitmap.GetPixel(x, Size / 2);
            AssertClose(pixel.Red,  (byte)Math.Round((1f - t) * 255));
            AssertClose(pixel.Blue, (byte)Math.Round(t * 255));
            AssertClose(pixel.Green, 0);
        }
    }

    private static SKColor RenderCenterPixel(SKShader shader)
    {
        using var bitmap = new SKBitmap(Size, Size);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            paint.Shader = shader;
            canvas.DrawRect(new SKRect(0, 0, Size, Size), paint);
        }
        return bitmap.GetPixel(Size / 2, Size / 2);
    }

    private static void AssertClose(byte actual, byte expected) =>
        Math.Abs(actual - expected).ShouldBeLessThanOrEqualTo(Tolerance,
            $"expected ~{expected}, rendered {actual} (tolerance ±{Tolerance}: SkSL half precision)");
}
