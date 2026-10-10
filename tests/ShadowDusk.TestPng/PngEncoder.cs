#nullable enable

using System.Buffers.Binary;
using System.IO.Compression;

namespace ShadowDusk.TestPng;

/// <summary>Writes 8-bit RGBA pixels as a non-interlaced PNG (colour type 6).</summary>
public static class PngEncoder
{
    private const int BytesPerPixel = 4;

    /// <summary>Encode <paramref name="rgba"/> (top row first) as PNG bytes.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        long expected = (long)width * height * BytesPerPixel;
        if (rgba.Length != expected)
            throw new ArgumentException(
                $"RGBA buffer is {rgba.Length} bytes, expected {expected} for {width}x{height}.", nameof(rgba));

        using var png = new MemoryStream();
        png.Write(PngFormat.Signature);
        WriteChunk(png, "IHDR"u8, Ihdr(width, height));
        WriteChunk(png, "IDAT"u8, CompressRows(rgba, width * BytesPerPixel, height));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    /// <summary>Encode <paramref name="rgba"/> and write it to <paramref name="path"/>.</summary>
    public static void Save(string path, ReadOnlySpan<byte> rgba, int width, int height) =>
        File.WriteAllBytes(path, Encode(rgba, width, height));

    private static byte[] Ihdr(int width, int height)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), height);
        data[8] = 8; // bit depth
        data[9] = PngFormat.ColourRgba;
        // data[10..12] stay 0: deflate compression, adaptive filtering, no interlace.
        return data;
    }

    private static byte[] CompressRows(ReadOnlySpan<byte> rgba, int stride, int height)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var candidate = new byte[stride];
            var best = new byte[stride];
            ReadOnlySpan<byte> previous = new byte[stride]; // the row above the first row is all zero

            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = rgba.Slice(y * stride, stride);
                byte bestFilter = ChooseFilter(row, previous, candidate, best);
                zlib.WriteByte(bestFilter);
                zlib.Write(best);
                previous = row;
            }
        }

        return compressed.ToArray();
    }

    /// <summary>
    /// Try all five filters and keep the one whose output has the smallest sum of absolute
    /// signed bytes: the standard heuristic (PNG spec section 12.8), which favours output deflate
    /// compresses well.
    /// </summary>
    private static byte ChooseFilter(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, byte[] candidate, byte[] best)
    {
        byte bestFilter = PngFormat.FilterNone;
        long bestScore = long.MaxValue;

        for (byte filter = PngFormat.FilterNone; filter <= PngFormat.FilterPaeth; filter++)
        {
            long score = 0;
            for (int i = 0; i < row.Length; i++)
            {
                int left = i >= BytesPerPixel ? row[i - BytesPerPixel] : 0;
                int upLeft = i >= BytesPerPixel ? previous[i - BytesPerPixel] : 0;
                byte filtered = (byte)(row[i] - PngFormat.Predict(filter, left, previous[i], upLeft));
                candidate[i] = filtered;
                score += Math.Abs((int)(sbyte)filtered); // widen first: Math.Abs(sbyte.MinValue) throws
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestFilter = filter;
                candidate.CopyTo(best, 0);
            }
        }

        return bestFilter;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32.Compute(type, data));
        output.Write(word);
    }
}
