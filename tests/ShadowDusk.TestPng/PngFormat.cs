#nullable enable

namespace ShadowDusk.TestPng;

/// <summary>Constants and the row-filter predictors shared by the encoder and decoder (PNG spec, 3rd ed.).</summary>
internal static class PngFormat
{
    public static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public const byte FilterNone = 0;
    public const byte FilterSub = 1;
    public const byte FilterUp = 2;
    public const byte FilterAverage = 3;
    public const byte FilterPaeth = 4;

    public const byte ColourGreyscale = 0;
    public const byte ColourRgb = 2;
    public const byte ColourPalette = 3;
    public const byte ColourGreyscaleAlpha = 4;
    public const byte ColourRgba = 6;

    /// <summary>The value a filter predicts for a byte from its left, upper and upper-left neighbours.</summary>
    public static int Predict(byte filterType, int left, int up, int upLeft) => filterType switch
    {
        FilterSub => left,
        FilterUp => up,
        FilterAverage => (left + up) >> 1,
        FilterPaeth => Paeth(left, up, upLeft),
        _ => 0,
    };

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int distLeft = Math.Abs(estimate - left);
        int distUp = Math.Abs(estimate - up);
        int distUpLeft = Math.Abs(estimate - upLeft);
        if (distLeft <= distUp && distLeft <= distUpLeft) return left;
        return distUp <= distUpLeft ? up : upLeft;
    }
}
