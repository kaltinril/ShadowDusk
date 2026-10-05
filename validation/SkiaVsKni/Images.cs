#nullable enable

using SkiaSharp;

namespace ShadowDusk.Validation.SkiaVsKni;

/// <summary>
/// The shared scene: procedurally generated, opaque RGBA8 images (rows top-first) that both arms
/// upload byte for byte, plus the comparison. Procedural rather than a decoded JPEG so neither
/// runtime's image decoder is in the comparison. Opaque, so premultiplied and straight alpha
/// read the same texels on both sides (SpriteBatch textures are raw RGBA; a Skia image is
/// premultiplied).
/// </summary>
internal static class Images
{
    /// <summary>
    /// The container size. 128 is also Pixelated's own sampling resolution constant, so its
    /// quantized coordinates land on texel boundaries (where a bilinear read is the exact mean
    /// of two texels) rather than at arbitrary sub-texel offsets.
    /// </summary>
    public const int Size = 128;

    /// <summary>Gradients plus a fine checker and diagonal bands: every channel varies and neighbours differ.</summary>
    public static byte[] Source()
    {
        var px = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                bool checker = ((x >> 2) + (y >> 2) & 1) == 0;
                px[i] = (byte)(x * 2 + (checker ? 0 : 1));
                px[i + 1] = (byte)(y * 2);
                px[i + 2] = (byte)((x * 5 + y * 3) & 255);
                px[i + 3] = 255;
            }
        }
        return px;
    }

    /// <summary>A grayscale radial ramp for Mask's second texture (Mask reads its red channel).</summary>
    public static byte[] Mask()
    {
        var px = new byte[Size * Size * 4];
        float c = (Size - 1) / 2f;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                byte v = (byte)Math.Clamp(MathF.Round((1.2f - d) * 255f), 0f, 255f);
                int i = (y * Size + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 255;
            }
        }
        return px;
    }

    /// <summary>Largest per-channel (RGBA) delta, and how many pixels exceed the tolerance.</summary>
    public static (int MaxDelta, int OverTolerance) Compare(byte[] a, byte[] b, int tolerance)
    {
        if (a.Length != b.Length)
            throw new InvalidDataException($"image sizes differ: {a.Length} vs {b.Length} bytes");
        int maxd = 0, over = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            int d = 0;
            for (int ch = 0; ch < 4; ch++)
                d = Math.Max(d, Math.Abs(a[i + ch] - b[i + ch]));
            maxd = Math.Max(maxd, d);
            if (d > tolerance)
                over++;
        }
        return (maxd, over);
    }

    public static int DistinctColors(byte[] rgba)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < rgba.Length; i += 4)
            seen.Add(BitConverter.ToInt32(rgba, i));
        return seen.Count;
    }

    public static void WritePng(string path, byte[] rgba)
    {
        var info = new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKImage image = SKImage.FromPixelCopy(info, rgba);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>Per-pixel max channel delta, amplified x16, as an opaque grey image.</summary>
    public static void WriteDiffPng(string path, byte[] a, byte[] b)
    {
        var diff = new byte[a.Length];
        for (int i = 0; i < a.Length; i += 4)
        {
            int d = 0;
            for (int ch = 0; ch < 4; ch++)
                d = Math.Max(d, Math.Abs(a[i + ch] - b[i + ch]));
            diff[i] = diff[i + 1] = diff[i + 2] = (byte)Math.Min(255, d * 16);
            diff[i + 3] = 255;
        }
        WritePng(path, diff);
    }
}
