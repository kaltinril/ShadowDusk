#nullable enable

using System.Buffers.Binary;
using System.Text;
using Shouldly;
using Xunit;

namespace ShadowDusk.TestPng.Tests;

public sealed class PngEncoderTests
{
    [Fact]
    public void Encode_StartsWithThePngSignature()
    {
        byte[] png = PngEncoder.Encode(new byte[4], 1, 1);

        png.AsSpan(0, 8).ToArray().ShouldBe(RawPng.Signature);
    }

    [Fact]
    public void Encode_WritesIhdrFirst_AsEightBitRgbaNonInterlaced()
    {
        byte[] png = PngEncoder.Encode(new byte[5 * 3 * 4], 5, 3);

        List<(string Type, byte[] Data)> chunks = ReadChunks(png);
        chunks[0].Type.ShouldBe("IHDR");
        chunks[0].Data.ShouldBe(RawPng.IhdrData(width: 5, height: 3, bitDepth: 8, colourType: 6));
    }

    [Fact]
    public void Encode_EndsWithIend_AndEveryChunkCrcIsValid()
    {
        byte[] png = PngEncoder.Encode(Gradient(16, 9), 16, 9);

        List<(string Type, byte[] Data)> chunks = ReadChunks(png); // ReadChunks verifies each CRC
        chunks[^1].Type.ShouldBe("IEND");
        chunks[^1].Data.ShouldBeEmpty();
        chunks.ShouldContain(c => c.Type == "IDAT");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 7)]
    [InlineData(64, 64)]
    [InlineData(257, 2)]
    public void Encode_ThenDecode_RoundTripsEveryByteIncludingAlpha(int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        new Random(width * 1000 + height).NextBytes(rgba);

        PngImage decoded = PngDecoder.Decode(PngEncoder.Encode(rgba, width, height));

        decoded.Width.ShouldBe(width);
        decoded.Height.ShouldBe(height);
        decoded.Rgba.ShouldBe(rgba);
    }

    [Fact]
    public void Encode_SmoothImage_CompressesWellBelowRawSize()
    {
        // Row filtering is what makes rendered output small; a regression to "no filter,
        // no compression" would silently bloat every committed reference image.
        byte[] rgba = Gradient(128, 128);

        byte[] png = PngEncoder.Encode(rgba, 128, 128);

        png.Length.ShouldBeLessThan(rgba.Length / 10);
    }

    [Fact]
    public void Encode_IsDeterministic()
    {
        byte[] rgba = Gradient(32, 32);

        PngEncoder.Encode(rgba, 32, 32).ShouldBe(PngEncoder.Encode(rgba, 32, 32));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-2, 3)]
    public void Encode_NonPositiveDimensions_Throws(int width, int height)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PngEncoder.Encode(new byte[4], width, height));
    }

    [Fact]
    public void Encode_BufferLengthNotWidthTimesHeightTimesFour_Throws()
    {
        var ex = Should.Throw<ArgumentException>(() => PngEncoder.Encode(new byte[15], 2, 2));

        ex.Message.ShouldContain("16", Case.Sensitive);
    }

    private static byte[] Gradient(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                rgba[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                rgba[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                rgba[i + 2] = 128;
                rgba[i + 3] = 255;
            }

        return rgba;
    }

    private static List<(string Type, byte[] Data)> ReadChunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        int offset = 8;
        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            byte[] type = png.AsSpan(offset + 4, 4).ToArray();
            byte[] data = png.AsSpan(offset + 8, length).ToArray();
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            crc.ShouldBe(Crc32.Compute(type, data), $"CRC of chunk {Encoding.ASCII.GetString(type)}");

            chunks.Add((Encoding.ASCII.GetString(type), data));
            offset += 12 + length;
        }

        offset.ShouldBe(png.Length);
        return chunks;
    }
}
