namespace ShadowDusk.ImageTests;

public sealed record ImageComparison(bool Matches, int DifferentPixels, int TotalPixels, byte MaxChannelDelta);

public static class ImageComparer
{
    public static ImageComparison Compare(byte[] expected, byte[] actual, byte tolerance = 1)
    {
        if (expected is null) throw new ArgumentNullException(nameof(expected));
        if (actual   is null) throw new ArgumentNullException(nameof(actual));

        if (expected.Length != actual.Length)
            throw new ArgumentException(
                $"Buffer lengths differ: expected={expected.Length}, actual={actual.Length}.",
                nameof(actual));

        if ((expected.Length & 3) != 0)
            throw new ArgumentException(
                $"Buffer length {expected.Length} is not a multiple of 4 (RGBA8 required).",
                nameof(expected));

        int totalPixels     = expected.Length / 4;
        int differentPixels = 0;
        byte maxDelta       = 0;

        for (int i = 0; i < expected.Length; i += 4)
        {
            int dR = Math.Abs(expected[i    ] - actual[i    ]);
            int dG = Math.Abs(expected[i + 1] - actual[i + 1]);
            int dB = Math.Abs(expected[i + 2] - actual[i + 2]);
            int dA = Math.Abs(expected[i + 3] - actual[i + 3]);

            int pixelMax = dR;
            if (dG > pixelMax) pixelMax = dG;
            if (dB > pixelMax) pixelMax = dB;
            if (dA > pixelMax) pixelMax = dA;

            if (pixelMax > maxDelta)
                maxDelta = (byte)pixelMax;

            if (pixelMax > tolerance)
                differentPixels++;
        }

        return new ImageComparison(
            Matches: differentPixels == 0,
            DifferentPixels: differentPixels,
            TotalPixels: totalPixels,
            MaxChannelDelta: maxDelta);
    }
}
