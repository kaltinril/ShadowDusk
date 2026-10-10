#nullable enable

using System.Text;
using Shouldly;
using Xunit;

namespace ShadowDusk.TestPng.Tests;

public sealed class Crc32Tests
{
    [Fact]
    public void Compute_StandardCheckVector_MatchesPublishedValue()
    {
        // The CRC-32/ISO-HDLC check value from the CRC catalogue.
        Crc32.Compute(Encoding.ASCII.GetBytes("123456789")).ShouldBe(0xCBF43926u);
    }

    [Fact]
    public void Compute_EmptyInput_IsZero()
    {
        Crc32.Compute([]).ShouldBe(0u);
    }

    [Fact]
    public void Compute_IendChunkType_MatchesTheCrcEveryPngEndsWith()
    {
        // Every PNG ends AE 42 60 82: the CRC of the 4 bytes "IEND" with no data.
        Crc32.Compute(Encoding.ASCII.GetBytes("IEND")).ShouldBe(0xAE426082u);
    }

    [Fact]
    public void Compute_TwoSpans_EqualsComputeOverTheirConcatenation()
    {
        byte[] first = Encoding.ASCII.GetBytes("IHDR");
        byte[] second = [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0];

        Crc32.Compute(first, second).ShouldBe(Crc32.Compute([.. first, .. second]));
    }
}
