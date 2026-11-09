#nullable enable

using System.Buffers.Binary;
using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Issue #478: the managed DXIL container hash, so a DirectX 12 compile signs its DXIL on every host.
///
/// <para>Retail D3D12 refuses an unsigned DXIL container (all-zero hash) at pipeline-state creation.
/// DXC signs through <c>dxil.dll</c>, which only loads on Windows, so a Linux/macOS compile used to
/// emit unsigned DXIL (warned <c>SD0214</c>). The signature is no cryptographic secret: it is
/// Microsoft's open-sourced "retail" container hash (DirectXShaderCompiler
/// <c>lib/DxilHash/DxilHash.cpp</c>, <c>ComputeHashRetail</c>; spec: microsoft/hlsl-specs
/// <c>proposals/infra/INF-0004-validator-hashing.md</c>), an MD5 variant over the container bytes
/// after the 20-byte prefix (<c>DXBC</c> magic plus the 16-byte hash), with a modified final-block
/// padding: the bit count rides in the first word of the last block and <c>1 | (byteCount &lt;&lt; 1)</c> in
/// the last. Computed here in managed code, it is byte-identical to <c>dxil.dll</c>'s (pinned by the
/// Windows-only oracle test over the fixture corpus).</para>
/// </summary>
internal static class DxilContainerHash
{
    /// <summary>The <c>DXBC</c> magic plus the 16-byte hash: the hash covers everything after this.</summary>
    internal const int PrefixLength = 20;

    private const int HashOffset = 4;
    private const int HashLength = 16;

    // The RFC 1321 round constants and rotate amounts (the file's S11..S44 and 0xd76aa478...).
    private static readonly uint[] K =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    private static readonly int[] Shift =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    /// <summary>True when <paramref name="container"/> is a <c>DXBC</c> container whose 16-byte hash is all zero.</summary>
    internal static bool IsUnsigned(ReadOnlySpan<byte> container)
    {
        if (!HasContainerPrefix(container))
            return false;
        return container.Slice(HashOffset, HashLength).IndexOfAnyExcept((byte)0) < 0;
    }

    /// <summary>
    /// Writes the retail container hash into a <c>DXBC</c> container whose hash is all zero, in place.
    /// A container that is not a <c>DXBC</c> container, or already carries a non-zero hash (signed by
    /// <c>dxil.dll</c>), is left untouched. Returns whether it signed.
    /// </summary>
    internal static bool SignInPlace(byte[] container)
    {
        if (!IsUnsigned(container))
            return false;

        Compute(container.AsSpan(PrefixLength), container.AsSpan(HashOffset, HashLength));
        return true;
    }

    private static readonly string[] DxcUnsignedWarnings =
    [
        "DXIL.dll not found.  Resulting DXIL will not be signed for use in release environments.",
        "DXIL signing library (dxil.dll,libdxil.so) not found.  Resulting DXIL will not be signed for use in release environments.",
    ];

    /// <summary>
    /// True for DXC's own "Resulting DXIL will not be signed" warning (it says no <c>dxil</c> validator
    /// is bound, the case on Linux/macOS). Matched exactly, so no other diagnostic can be hidden by it.
    /// </summary>
    internal static bool IsUnsignedNotice(ShaderError warning) =>
        warning.Severity == ShaderErrorSeverity.Warning
        && DxcUnsignedWarnings.Any(w =>
            warning.Message.Equals(w, StringComparison.Ordinal)
            || warning.Message.Equals("warning: " + w, StringComparison.Ordinal));

    private static bool HasContainerPrefix(ReadOnlySpan<byte> container) =>
        container.Length >= PrefixLength
        && container[0] == (byte)'D' && container[1] == (byte)'X' && container[2] == (byte)'B' && container[3] == (byte)'C';

    /// <summary>
    /// <c>ComputeHashRetail</c>: hashes <paramref name="data"/> (the container bytes after the 20-byte
    /// prefix) into the 16-byte <paramref name="hash"/>.
    /// </summary>
    internal static void Compute(ReadOnlySpan<byte> data, Span<byte> hash)
    {
        int byteCount = data.Length;
        int leftOver = byteCount & 0x3f;
        int fullBlocks = byteCount >> 6;

        uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
        Span<uint> x = stackalloc uint[16];

        for (int i = 0; i < fullBlocks; i++)
        {
            LoadWords(data.Slice(i * 64, 64), x);
            Round(x, ref a0, ref b0, ref c0, ref d0);
        }

        // The tail: the remainder plus padding, in one block (remainder < 56 bytes) or two.
        Span<byte> tail = stackalloc byte[128];
        tail.Clear();
        ReadOnlySpan<byte> remainder = data.Slice(fullBlocks * 64);
        uint bits = unchecked((uint)byteCount << 3);
        uint last = unchecked(1u | ((uint)byteCount << 1));

        int tailBlocks;
        if (leftOver < 56)
        {
            // [bit count][remainder][0x80, zeros...][1 | byteCount << 1]
            BinaryPrimitives.WriteUInt32LittleEndian(tail, bits);
            remainder.CopyTo(tail.Slice(4));
            tail[4 + leftOver] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(tail.Slice(60), last);
            tailBlocks = 1;
        }
        else
        {
            // Block 1: [remainder][0x80, zeros...]. Block 2: [bit count][zeros][1 | byteCount << 1].
            remainder.CopyTo(tail);
            tail[leftOver] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(tail.Slice(64), bits);
            BinaryPrimitives.WriteUInt32LittleEndian(tail.Slice(124), last);
            tailBlocks = 2;
        }

        for (int i = 0; i < tailBlocks; i++)
        {
            LoadWords(tail.Slice(i * 64, 64), x);
            Round(x, ref a0, ref b0, ref c0, ref d0);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(hash, a0);
        BinaryPrimitives.WriteUInt32LittleEndian(hash.Slice(4), b0);
        BinaryPrimitives.WriteUInt32LittleEndian(hash.Slice(8), c0);
        BinaryPrimitives.WriteUInt32LittleEndian(hash.Slice(12), d0);
    }

    private static void LoadWords(ReadOnlySpan<byte> block, Span<uint> x)
    {
        for (int j = 0; j < 16; j++)
            x[j] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(j * 4, 4));
    }

    private static void Round(ReadOnlySpan<uint> x, ref uint stateA, ref uint stateB, ref uint stateC, ref uint stateD)
    {
        uint a = stateA, b = stateB, c = stateC, d = stateD;

        for (int i = 0; i < 64; i++)
        {
            uint f;
            int g;
            if (i < 16)
            {
                f = (b & c) | (~b & d);
                g = i;
            }
            else if (i < 32)
            {
                f = (b & d) | (c & ~d);
                g = (5 * i + 1) & 15;
            }
            else if (i < 48)
            {
                f = b ^ c ^ d;
                g = (3 * i + 5) & 15;
            }
            else
            {
                f = c ^ (b | ~d);
                g = (7 * i) & 15;
            }

            f = unchecked(f + a + K[i] + x[g]);
            a = d;
            d = c;
            c = b;
            b = unchecked(b + uint.RotateLeft(f, Shift[i]));
        }

        unchecked
        {
            stateA += a;
            stateB += b;
            stateC += c;
            stateD += d;
        }
    }
}
