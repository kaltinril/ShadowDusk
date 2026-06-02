#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// <see cref="ManagedMd5"/> exists because the .NET 8 browser/WASM runtime has no
/// <c>System.Security.Cryptography.MD5</c>. It is only safe to use in
/// <c>MgfxWriter.ComputeEffectKey</c> if it is byte-identical to standard MD5 — a
/// wrong-but-consistent hash would silently break cross-host byte-identity (CLAUDE.md
/// constraint #3) and the effect cache key, and NO other test would catch it (the key
/// lives in the .mgfx header, not the rendered image, and the byte-identity tests
/// compare ShadowDusk-vs-ShadowDusk). This test is that guard.
/// </summary>
public sealed class ManagedMd5Tests
{
    [Fact]
    public void MatchesRfc1321KnownVectors()
    {
        // RFC 1321 Appendix A.5 test suite.
        Hex(ManagedMd5.HashData(Bytes(""))).Should().Be("d41d8cd98f00b204e9800998ecf8427e");
        Hex(ManagedMd5.HashData(Bytes("a"))).Should().Be("0cc175b9c0f1b6a831c399e269772661");
        Hex(ManagedMd5.HashData(Bytes("abc"))).Should().Be("900150983cd24fb0d6963f7d28e17f72");
        Hex(ManagedMd5.HashData(Bytes("message digest"))).Should().Be("f96b697d7cb7938d525a2f31aaf161d0");
        Hex(ManagedMd5.HashData(Bytes("abcdefghijklmnopqrstuvwxyz"))).Should().Be("c3fcd3d76192e4007dfb496cca67e13b");
        Hex(ManagedMd5.HashData(Bytes("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")))
            .Should().Be("d174ab98d277d9f5a5611c2c9f419d9f");
        Hex(ManagedMd5.HashData(Bytes("12345678901234567890123456789012345678901234567890123456789012345678901234567890")))
            .Should().Be("57edf4a22be3c955ac49da2e2107b67a");
    }

    [Theory]
    // Cover every interesting MD5 padding boundary: a block is 64 bytes, the length
    // field is the last 8, so the 0x80 pad/length straddle behaves specially around
    // 55/56/63/64/65/119/120 bytes. Also a couple of multi-block sizes.
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(54)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(57)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(119)]
    [InlineData(120)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(200)]
    [InlineData(1000)]
    public void MatchesBclMd5_AtPaddingBoundariesAndBeyond(int length)
    {
        // Deterministic pseudo-data (no Random — keeps the test reproducible).
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)((i * 31 + 7) & 0xFF);

        byte[] mine = ManagedMd5.HashData(data);
        byte[] bcl = MD5.HashData(data);

        mine.Should().Equal(bcl, "ManagedMd5 must be byte-identical to BCL MD5 for length {0}", length);
    }

    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
