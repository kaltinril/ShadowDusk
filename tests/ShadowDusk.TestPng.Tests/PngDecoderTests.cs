#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.TestPng.Tests;

public sealed class PngDecoderTests
{
    // PNG colour types (spec section 11.2.2).
    private const byte Greyscale = 0;
    private const byte Rgb = 2;
    private const byte Palette = 3;
    private const byte GreyscaleAlpha = 4;
    private const byte Rgba = 6;

    // A 3x2 RGBA image with distinct values in every channel, so a swapped channel,
    // row or column cannot go unnoticed.
    private static readonly byte[][] RgbaRows =
    [
        [10, 20, 30, 255, 200, 150, 100, 128, 0, 255, 1, 0],
        [90, 80, 70, 60, 5, 250, 125, 255, 33, 66, 99, 200],
    ];

    [Theory]
    [InlineData(0)] // None
    [InlineData(1)] // Sub
    [InlineData(2)] // Up
    [InlineData(3)] // Average
    [InlineData(4)] // Paeth
    public void Decode_Rgba_UndoesEveryFilterType(byte filterType)
    {
        byte[] png = Png(3, 2, Rgba, RawPng.FilteredZlib(RgbaRows, bytesPerPixel: 4, filterType));

        PngImage image = PngDecoder.Decode(png);

        image.Width.ShouldBe(3);
        image.Height.ShouldBe(2);
        image.Rgba.ShouldBe([.. RgbaRows[0], .. RgbaRows[1]]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Decode_Rgb_ExpandsToOpaqueRgba(byte filterType)
    {
        byte[][] rows = [[1, 2, 3, 250, 251, 252], [7, 8, 9, 100, 0, 200]];

        PngImage image = PngDecoder.Decode(Png(2, 2, Rgb, RawPng.FilteredZlib(rows, 3, filterType)));

        image.Rgba.ShouldBe([1, 2, 3, 255, 250, 251, 252, 255, 7, 8, 9, 255, 100, 0, 200, 255]);
    }

    [Fact]
    public void Decode_Greyscale_ReplicatesIntoRgbAndIsOpaque()
    {
        byte[][] rows = [[0, 77, 255]];

        PngImage image = PngDecoder.Decode(Png(3, 1, Greyscale, RawPng.FilteredZlib(rows, 1, 1)));

        image.Rgba.ShouldBe([0, 0, 0, 255, 77, 77, 77, 255, 255, 255, 255, 255]);
    }

    [Fact]
    public void Decode_GreyscaleAlpha_KeepsAlpha()
    {
        byte[][] rows = [[40, 10, 200, 250]];

        PngImage image = PngDecoder.Decode(Png(2, 1, GreyscaleAlpha, RawPng.FilteredZlib(rows, 2, 4)));

        image.Rgba.ShouldBe([40, 40, 40, 10, 200, 200, 200, 250]);
    }

    [Fact]
    public void Decode_Palette_LooksUpColoursAndAppliesTrnsAlpha()
    {
        byte[] plte = [255, 0, 0, 0, 255, 0, 0, 0, 255];
        byte[] trns = [128]; // entry 0 is half transparent; entries 1-2 default to opaque
        byte[] png = new RawPng()
            .Ihdr(3, 1, 8, Palette)
            .Chunk("PLTE", plte)
            .Chunk("tRNS", trns)
            .Idat(RawPng.FilteredZlib([[2, 0, 1]], 1, 0))
            .Iend()
            .ToArray();

        PngImage image = PngDecoder.Decode(png);

        image.Rgba.ShouldBe([0, 0, 255, 255, 255, 0, 0, 128, 0, 255, 0, 255]);
    }

    [Fact]
    public void Decode_Palette_IndexBeyondPalette_Throws()
    {
        byte[] png = new RawPng()
            .Ihdr(1, 1, 8, Palette)
            .Chunk("PLTE", [1, 2, 3])
            .Idat(RawPng.FilteredZlib([[5]], 1, 0))
            .Iend()
            .ToArray();

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png)).Message
            .ShouldContain("palette", Case.Sensitive);
    }

    [Fact]
    public void Decode_RgbWithTrnsColourKey_MakesOnlyThatColourTransparent()
    {
        byte[] trns = [0, 10, 0, 20, 0, 30]; // 16-bit samples: key colour (10, 20, 30)
        byte[] png = new RawPng()
            .Ihdr(2, 1, 8, Rgb)
            .Chunk("tRNS", trns)
            .Idat(RawPng.FilteredZlib([[10, 20, 30, 10, 20, 31]], 3, 0))
            .Iend()
            .ToArray();

        PngDecoder.Decode(png).Rgba.ShouldBe([10, 20, 30, 0, 10, 20, 31, 255]);
    }

    [Fact]
    public void Decode_ImageDataSplitAcrossSeveralIdatChunks_IsConcatenated()
    {
        byte[] zlib = RawPng.FilteredZlib(RgbaRows, 4, 4);
        int third = zlib.Length / 3;
        byte[] png = new RawPng()
            .Ihdr(3, 2, 8, Rgba)
            .Idat(zlib[..third])
            .Idat(zlib[third..(2 * third)])
            .Idat(zlib[(2 * third)..])
            .Iend()
            .ToArray();

        PngDecoder.Decode(png).Rgba.ShouldBe([.. RgbaRows[0], .. RgbaRows[1]]);
    }

    [Fact]
    public void Decode_AncillaryChunks_AreIgnored()
    {
        byte[] png = new RawPng()
            .Ihdr(3, 2, 8, Rgba)
            .Chunk("gAMA", [0, 0, 0xB1, 0x8F])
            .Chunk("tEXt", "Software\0test"u8.ToArray())
            .Idat(RawPng.FilteredZlib(RgbaRows, 4, 2))
            .Chunk("tIME", [0x07, 0xEA, 10, 10, 12, 0, 0])
            .Iend()
            .ToArray();

        PngDecoder.Decode(png).Rgba.ShouldBe([.. RgbaRows[0], .. RgbaRows[1]]);
    }

    // ---------------------------------------------------------------------------------------
    // Fail loudly: every shape we do not model is rejected with a message that says why.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Decode_MissingSignature_Throws()
    {
        byte[] png = new RawPng(writeSignature: false).Ihdr(1, 1, 8, Rgba).Iend().ToArray();

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png)).Message
            .ShouldContain("signature", Case.Sensitive);
    }

    [Fact]
    public void Decode_CorruptChunkCrc_Throws()
    {
        byte[] png = new RawPng()
            .Chunk("IHDR", RawPng.IhdrData(3, 2, 8, Rgba), corruptCrc: true)
            .Idat(RawPng.FilteredZlib(RgbaRows, 4, 0))
            .Iend()
            .ToArray();

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png)).Message
            .ShouldContain("CRC", Case.Sensitive);
    }

    [Fact]
    public void Decode_TruncatedFile_Throws()
    {
        byte[] whole = Png(3, 2, Rgba, RawPng.FilteredZlib(RgbaRows, 4, 0));

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(whole.AsSpan(0, whole.Length - 6)));
    }

    [Fact]
    public void Decode_FirstChunkNotIhdr_Throws()
    {
        byte[] png = new RawPng().Idat(RawPng.FilteredZlib(RgbaRows, 4, 0)).Iend().ToArray();

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png)).Message
            .ShouldContain("IHDR", Case.Sensitive);
    }

    [Fact]
    public void Decode_NoIdat_Throws()
    {
        byte[] png = new RawPng().Ihdr(3, 2, 8, Rgba).Iend().ToArray();

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png)).Message
            .ShouldContain("IDAT", Case.Sensitive);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(4)]
    [InlineData(1)]
    public void Decode_BitDepthOtherThanEight_ThrowsNamingTheDepth(byte bitDepth)
    {
        byte[] png = Png(3, 2, Rgba, RawPng.FilteredZlib(RgbaRows, 4, 0), bitDepth);

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png)).Message
            .ShouldContain($"bit depth {bitDepth}", Case.Sensitive);
    }

    [Fact]
    public void Decode_Interlaced_Throws()
    {
        byte[] png = new RawPng()
            .Ihdr(3, 2, 8, Rgba, interlace: 1)
            .Idat(RawPng.FilteredZlib(RgbaRows, 4, 0))
            .Iend()
            .ToArray();

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png)).Message
            .ShouldContain("interlace", Case.Sensitive);
    }

    [Fact]
    public void Decode_UnknownColourType_Throws()
    {
        byte[] png = Png(3, 2, colourType: 5, RawPng.FilteredZlib(RgbaRows, 4, 0));

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png)).Message
            .ShouldContain("colour type 5", Case.Sensitive);
    }

    [Fact]
    public void Decode_UnknownFilterType_Throws()
    {
        byte[] png = Png(3, 2, Rgba, RawPng.FilteredZlib(RgbaRows, 4, filterType: 9));

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png)).Message
            .ShouldContain("filter type 9", Case.Sensitive);
    }

    [Fact]
    public void Decode_PixelDataShorterThanImage_Throws()
    {
        // Rows for a 3x2 image declared as 3x3.
        byte[] png = Png(3, 3, Rgba, RawPng.FilteredZlib(RgbaRows, 4, 0));

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png));
    }

    [Fact]
    public void Decode_UnknownCriticalChunk_Throws()
    {
        // An uppercase first letter marks a chunk critical: a decoder must not skip it.
        byte[] png = new RawPng()
            .Ihdr(3, 2, 8, Rgba)
            .Chunk("ZZZZ", [1, 2, 3])
            .Idat(RawPng.FilteredZlib(RgbaRows, 4, 0))
            .Iend()
            .ToArray();

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png)).Message
            .ShouldContain("ZZZZ", Case.Sensitive);
    }

    [Fact]
    public void Decode_HugeDeclaredDimensions_ThrowsInsteadOfAllocating()
    {
        byte[] png = Png(100_000, 100_000, Rgba, RawPng.FilteredZlib(RgbaRows, 4, 0));

        Should.Throw<InvalidDataException>(() => PngDecoder.Decode(png));
    }

    private static byte[] Png(int width, int height, byte colourType, byte[] zlib, byte bitDepth = 8) =>
        new RawPng().Ihdr(width, height, bitDepth, colourType).Idat(zlib).Iend().ToArray();
}
