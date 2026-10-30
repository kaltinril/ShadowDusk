#nullable enable

namespace ShadowDusk.TestPng;

/// <summary>
/// A decoded image: <see cref="Width"/> x <see cref="Height"/> pixels, stored as tightly packed
/// 8-bit RGBA rows, top row first (<c>Rgba.Length == Width * Height * 4</c>).
/// </summary>
public sealed class PngImage
{
    public PngImage(int width, int height, byte[] rgba)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.LongLength != (long)width * height * 4)
            throw new ArgumentException(
                $"RGBA buffer is {rgba.LongLength} bytes, expected {(long)width * height * 4} for {width}x{height}.",
                nameof(rgba));

        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Rgba { get; }
}
