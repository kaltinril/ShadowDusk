#nullable enable

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ShadowDusk.TestPng;

/// <summary>
/// Reads non-interlaced 8-bit PNGs (greyscale, RGB, palette, greyscale+alpha, RGBA) into RGBA.
/// Anything else is rejected with an <see cref="InvalidDataException"/> naming what was found.
/// </summary>
public static class PngDecoder
{
    /// <summary>
    /// Refuse images whose RGBA buffer would exceed this, so a corrupt or hostile header cannot
    /// trigger a multi-gigabyte allocation. 256 MiB is far above anything the test suites render.
    /// </summary>
    private const long MaxRgbaBytes = 256L * 1024 * 1024;

    /// <summary>Decode PNG bytes into RGBA pixels.</summary>
    public static PngImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < PngFormat.Signature.Length || !png[..PngFormat.Signature.Length].SequenceEqual(PngFormat.Signature))
            throw new InvalidDataException("Not a PNG: the 8-byte PNG signature is missing.");

        Header? header = null;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var imageData = new MemoryStream();
        bool sawEnd = false;

        int offset = PngFormat.Signature.Length;
        while (!sawEnd)
        {
            string type = ReadChunk(png, ref offset, out ReadOnlySpan<byte> data);

            if (header is null && type != "IHDR")
                throw new InvalidDataException($"The first PNG chunk must be IHDR, found {type}.");

            switch (type)
            {
                case "IHDR":
                    if (header is not null)
                        throw new InvalidDataException("PNG has more than one IHDR chunk.");
                    header = Header.Parse(data);
                    break;
                case "PLTE":
                    palette = data.ToArray();
                    break;
                case "tRNS":
                    transparency = data.ToArray();
                    break;
                case "IDAT":
                    imageData.Write(data);
                    break;
                case "IEND":
                    sawEnd = true;
                    break;
                default:
                    // Bit 5 of the first type byte clear (uppercase letter) marks a critical chunk.
                    if (char.IsAsciiLetterUpper(type[0]))
                        throw new InvalidDataException($"Unsupported critical PNG chunk {type}.");
                    break; // ancillary (gAMA, tEXt, pHYs, ...): safe to ignore
            }
        }

        if (imageData.Length == 0)
            throw new InvalidDataException("PNG has no IDAT (image data) chunk.");

        Header h = header!.Value;
        byte[] raw = Inflate(imageData, (long)h.Height * (1 + h.Stride));
        Unfilter(raw, h.Stride, h.Height, h.BytesPerPixel);
        return new PngImage(h.Width, h.Height, ToRgba(raw, h, palette, transparency));
    }

    /// <summary>Read and decode the PNG file at <paramref name="path"/>.</summary>
    public static PngImage Load(string path)
    {
        try
        {
            return Decode(File.ReadAllBytes(path));
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"'{path}': {ex.Message}", ex);
        }
    }

    private static string ReadChunk(ReadOnlySpan<byte> png, ref int offset, out ReadOnlySpan<byte> data)
    {
        if (png.Length - offset < 12)
            throw new InvalidDataException("PNG is truncated: it ends before the IEND chunk.");

        uint length = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
        ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
        string typeName = Encoding.ASCII.GetString(type);
        if (length > (uint)(png.Length - offset - 12))
            throw new InvalidDataException($"PNG is truncated inside the {typeName} chunk.");

        data = png.Slice(offset + 8, (int)length);
        uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(png[(offset + 8 + (int)length)..]);
        if (storedCrc != Crc32.Compute(type, data))
            throw new InvalidDataException($"PNG chunk {typeName} fails its CRC check (the file is corrupt).");

        offset += 12 + (int)length;
        return typeName;
    }

    private static byte[] Inflate(MemoryStream compressed, long expectedLength)
    {
        compressed.Position = 0;
        var raw = new byte[expectedLength];
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        int read = zlib.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false);
        if (read != raw.Length)
            throw new InvalidDataException(
                $"PNG pixel data ends early: {read} of {raw.Length} decompressed bytes present.");
        return raw;
    }

    /// <summary>Reverse each scanline's filter in place; <paramref name="raw"/> keeps its filter-type bytes.</summary>
    private static void Unfilter(byte[] raw, int stride, int height, int bpp)
    {
        for (int y = 0; y < height; y++)
        {
            int start = y * (stride + 1);
            byte filter = raw[start];
            if (filter > PngFormat.FilterPaeth)
                throw new InvalidDataException($"PNG row {y} uses unknown filter type {filter}.");

            int row = start + 1;
            int above = row - (stride + 1); // negative on the first row: treated as zeros
            for (int i = 0; i < stride; i++)
            {
                int left = i >= bpp ? raw[row + i - bpp] : 0;
                int up = y > 0 ? raw[above + i] : 0;
                int upLeft = y > 0 && i >= bpp ? raw[above + i - bpp] : 0;
                raw[row + i] = (byte)(raw[row + i] + PngFormat.Predict(filter, left, up, upLeft));
            }
        }
    }

    private static byte[] ToRgba(byte[] raw, Header h, byte[]? palette, byte[]? transparency)
    {
        var rgba = new byte[(long)h.Width * h.Height * 4];
        int o = 0;
        for (int y = 0; y < h.Height; y++)
        {
            int p = y * (h.Stride + 1) + 1;
            for (int x = 0; x < h.Width; x++, o += 4)
            {
                switch (h.ColourType)
                {
                    case PngFormat.ColourGreyscale:
                        {
                            byte g = raw[p++];
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = g;
                            rgba[o + 3] = MatchesKey(transparency, g) ? (byte)0 : (byte)255;
                            break;
                        }
                    case PngFormat.ColourRgb:
                        {
                            byte r = raw[p++], g = raw[p++], b = raw[p++];
                            (rgba[o], rgba[o + 1], rgba[o + 2]) = (r, g, b);
                            rgba[o + 3] = MatchesKey(transparency, r, g, b) ? (byte)0 : (byte)255;
                            break;
                        }
                    case PngFormat.ColourPalette:
                        {
                            int index = raw[p++];
                            if (palette is null || index * 3 + 2 >= palette.Length)
                                throw new InvalidDataException(
                                    $"PNG pixel ({x}, {y}) uses palette index {index}, beyond the {(palette?.Length ?? 0) / 3}-entry palette.");
                            (rgba[o], rgba[o + 1], rgba[o + 2]) = (palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2]);
                            rgba[o + 3] = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
                            break;
                        }
                    case PngFormat.ColourGreyscaleAlpha:
                        {
                            byte g = raw[p++];
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = g;
                            rgba[o + 3] = raw[p++];
                            break;
                        }
                    default: // ColourRgba (Header.Parse rejected every other value)
                        Buffer.BlockCopy(raw, p, rgba, o, 4);
                        p += 4;
                        break;
                }
            }
        }

        return rgba;
    }

    /// <summary>True when a tRNS colour key (16-bit big-endian samples) names exactly these samples.</summary>
    private static bool MatchesKey(byte[]? transparency, params byte[] samples)
    {
        if (transparency is null || transparency.Length != samples.Length * 2)
            return false;
        for (int i = 0; i < samples.Length; i++)
            if (BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(i * 2)) != samples[i])
                return false;
        return true;
    }

    private readonly record struct Header(int Width, int Height, byte ColourType, int BytesPerPixel)
    {
        public int Stride => Width * BytesPerPixel;

        public static Header Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length != 13)
                throw new InvalidDataException($"PNG IHDR chunk is {data.Length} bytes, expected 13.");

            uint width = BinaryPrimitives.ReadUInt32BigEndian(data);
            uint height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            byte bitDepth = data[8];
            byte colourType = data[9];

            if (width == 0 || height == 0)
                throw new InvalidDataException($"PNG has zero size ({width}x{height}).");
            if ((ulong)width * height * 4 > MaxRgbaBytes)
                throw new InvalidDataException($"PNG is {width}x{height}, larger than this decoder accepts.");
            if (bitDepth != 8)
                throw new InvalidDataException($"PNG bit depth {bitDepth} is not supported (only 8).");
            if (data[10] != 0 || data[11] != 0)
                throw new InvalidDataException("PNG uses an unknown compression or filter method.");
            if (data[12] != 0)
                throw new InvalidDataException("Interlaced (Adam7) PNGs are not supported: save without interlace.");

            int bytesPerPixel = colourType switch
            {
                PngFormat.ColourGreyscale => 1,
                PngFormat.ColourRgb => 3,
                PngFormat.ColourPalette => 1,
                PngFormat.ColourGreyscaleAlpha => 2,
                PngFormat.ColourRgba => 4,
                _ => throw new InvalidDataException($"PNG colour type {colourType} is not a valid PNG colour type."),
            };

            return new Header((int)width, (int)height, colourType, bytesPerPixel);
        }
    }
}
