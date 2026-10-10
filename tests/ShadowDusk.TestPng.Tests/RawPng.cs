#nullable enable

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ShadowDusk.TestPng.Tests;

/// <summary>
/// Hand-assembles PNG files chunk by chunk, independently of <see cref="PngEncoder"/>, so the
/// decoder can be fed every colour type, filter and chunk layout (including broken ones).
/// </summary>
internal sealed class RawPng
{
    public static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly MemoryStream _out = new();

    public RawPng(bool writeSignature = true)
    {
        if (writeSignature)
            _out.Write(Signature);
    }

    public static byte[] IhdrData(int width, int height, byte bitDepth, byte colourType, byte interlace = 0)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), height);
        data[8] = bitDepth;
        data[9] = colourType;
        data[10] = 0; // compression: deflate
        data[11] = 0; // filter method: adaptive
        data[12] = interlace;
        return data;
    }

    /// <summary>Filter each scanline with <paramref name="filterType"/> and zlib-compress the result.</summary>
    public static byte[] FilteredZlib(byte[][] rows, int bytesPerPixel, byte filterType)
    {
        using var raw = new MemoryStream();
        byte[] previous = new byte[rows[0].Length];
        foreach (byte[] row in rows)
        {
            raw.WriteByte(filterType);
            raw.Write(ApplyFilter(filterType, row, previous, bytesPerPixel));
            previous = row;
        }

        return Zlib(raw.ToArray());
    }

    public static byte[] Zlib(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return compressed.ToArray();
    }

    public RawPng Chunk(string type, byte[] data, bool corruptCrc = false)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> word = stackalloc byte[4];

        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        _out.Write(word);
        _out.Write(typeBytes);
        _out.Write(data);

        uint crc = Crc32.Compute(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(word, corruptCrc ? crc ^ 1u : crc);
        _out.Write(word);
        return this;
    }

    public RawPng Ihdr(int width, int height, byte bitDepth, byte colourType, byte interlace = 0) =>
        Chunk("IHDR", IhdrData(width, height, bitDepth, colourType, interlace));

    public RawPng Idat(byte[] zlib) => Chunk("IDAT", zlib);

    public RawPng Iend() => Chunk("IEND", []);

    public byte[] ToArray() => _out.ToArray();

    // Straightforward forward filters (PNG spec section 9.2), written separately from the decoder's inverse.
    private static byte[] ApplyFilter(byte filterType, byte[] row, byte[] above, int bpp)
    {
        var result = new byte[row.Length];
        for (int i = 0; i < row.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0;
            int up = above[i];
            int upLeft = i >= bpp ? above[i - bpp] : 0;
            int predictor = filterType switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => 0, // an invalid filter byte is written verbatim so the decoder can reject it
            };
            result[i] = (byte)(row[i] - predictor);
        }

        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}
