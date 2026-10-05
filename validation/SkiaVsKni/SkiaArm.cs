#nullable enable

using ShadowDusk.Core;
using SkiaSharp;

namespace ShadowDusk.Validation.SkiaVsKni;

/// <summary>
/// The Skia arm: real SkiaSharp, CPU raster (no GPU, no window). Each job's SkSL is compiled by
/// Skia's own compiler (<see cref="SKRuntimeEffect.CreateShader"/>), the container texture is the
/// child shader with SpriteBatch's sampler (bilinear, clamp), and the shader fills the whole
/// target with <see cref="SKBlendMode.Src"/>, so the stored pixels are the shader's output and
/// nothing else (the KNI arm draws with <c>BlendState.Opaque</c> for the same reason).
/// </summary>
internal static class SkiaArm
{
    private const string ColorUniform = "ShadowDusk_Color";
    private const string ResolutionUniform = "ShadowDusk_Resolution";

    /// <summary>The uniform names Skia's compiler finds in <paramref name="sksl"/>; empty with <paramref name="errors"/> set on a compile failure.</summary>
    public static IReadOnlyList<string> DeclaredUniforms(string sksl, out string? errors)
    {
        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(sksl, out string compileErrors);
        if (effect is null)
        {
            errors = string.IsNullOrEmpty(compileErrors) ? "(no message)" : compileErrors;
            return [];
        }
        errors = null;
        return effect.Uniforms.ToArray();
    }

    public static Result<byte[], string> Render(SkiaJob job, IReadOnlyDictionary<string, byte[]> textures)
    {
        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(job.Sksl, out string errors);
        if (effect is null)
            return Result<byte[], string>.Fail($"Skia's compiler rejected the SkSL: {errors}");

        // SKRuntimeEffectUniforms does not zero its buffer, so every declared uniform is written.
        var uniforms = new SKRuntimeEffectUniforms(effect);
        foreach (string name in effect.Uniforms)
        {
            float[] value = name switch
            {
                ColorUniform => job.Tint.Select(b => b / 255f).ToArray(),
                ResolutionUniform => job.Resolution,
                _ => job.Uniforms.TryGetValue(name, out float[]? v)
                    ? v
                    : throw new InvalidDataException($"{job.Name}: uniform '{name}' has no value"),
            };
            uniforms[name] = value;
        }

        var images = new List<IDisposable>();
        try
        {
            var children = new SKRuntimeEffectChildren(effect);
            foreach (string child in job.Children)
            {
                if (!textures.TryGetValue(child, out byte[]? rgba))
                    return Result<byte[], string>.Fail($"no texture for child '{child}'");
                var info = new SKImageInfo(Images.Size, Images.Size, SKColorType.Rgba8888, SKAlphaType.Premul);
                SKImage image = SKImage.FromPixelCopy(info, rgba);
                images.Add(image);
                SKShader shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                images.Add(shader);
                children[child] = shader;
            }

            using SKShader effectShader = effect.ToShader(uniforms, children);
            var targetInfo = new SKImageInfo(Images.Size, Images.Size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var target = new SKBitmap(targetInfo);
            target.Erase(SKColors.Transparent);
            using (var canvas = new SKCanvas(target))
            using (var paint = new SKPaint { Shader = effectShader, BlendMode = SKBlendMode.Src, IsAntialias = false })
                canvas.DrawRect(new SKRect(0, 0, Images.Size, Images.Size), paint);

            return Result<byte[], string>.Ok(target.Bytes);
        }
        finally
        {
            foreach (IDisposable d in images)
                d.Dispose();
        }
    }
}
