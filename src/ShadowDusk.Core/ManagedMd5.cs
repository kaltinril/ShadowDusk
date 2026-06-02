#nullable enable

using System;

namespace ShadowDusk.Core;

/// <summary>
/// Self-contained, dependency-free MD5 (RFC 1321) used only to derive the MGFX
/// effect-cache key (<see cref="MgfxWriter"/>). It exists because the .NET 8 browser
/// (WASM) runtime does NOT provide <c>System.Security.Cryptography.MD5</c> —
/// <c>MD5.HashData</c> throws <c>Cryptography_UnknownHashAlgorithm</c> there (the
/// WASM crypto provider only exposes the SHA family via SubtleCrypto). The faithful
/// in-browser compile pipeline (Phase 23) reaches the MGFX writer, so the desktop-only
/// MD5 made <c>WasmShaderCompiler.CompileAsync</c> fail on EVERY shader.
///
/// <para>MD5 is a fixed standard, so this produces bytes <b>identical</b> to the BCL
/// <c>MD5</c> on every platform — desktop output is unchanged (the byte-identity /
/// determinism gates keep passing) and the browser path now works, giving the SAME
/// effect key cross-host. The key is only a MonoGame cache key; it does not affect
/// loading or rendering, but keeping it byte-identical preserves ShadowDusk's
/// cross-host reproducibility (CLAUDE.md constraint #3).</para>
/// </summary>
internal static class ManagedMd5
{
    /// <summary>Computes the 16-byte MD5 digest of <paramref name="input"/>.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> input)
    {
        // RFC 1321 initial register values (little-endian).
        uint a0 = 0x67452301u, b0 = 0xefcdab89u, c0 = 0x98badcfeu, d0 = 0x10325476u;

        // Padded length: message + 0x80 + zero pad to 56 mod 64 + 8-byte bit length.
        long bitLen = (long)input.Length * 8;
        int padded = input.Length + 1;
        while (padded % 64 != 56) padded++;
        padded += 8;

        byte[] msg = new byte[padded];
        input.CopyTo(msg);
        msg[input.Length] = 0x80;
        for (int i = 0; i < 8; i++)
            msg[padded - 8 + i] = (byte)(bitLen >> (8 * i)); // little-endian length

        Span<uint> m = stackalloc uint[16];
        for (int chunk = 0; chunk < padded; chunk += 64)
        {
            for (int j = 0; j < 16; j++)
            {
                int p = chunk + j * 4;
                m[j] = (uint)(msg[p] | (msg[p + 1] << 8) | (msg[p + 2] << 16) | (msg[p + 3] << 24));
            }

            uint a = a0, b = b0, c = c0, d = d0;
            for (int i = 0; i < 64; i++)
            {
                uint f;
                int g;
                if (i < 16)      { f = (b & c) | (~b & d);        g = i; }
                else if (i < 32) { f = (d & b) | (~d & c);        g = (5 * i + 1) % 16; }
                else if (i < 48) { f = b ^ c ^ d;                 g = (3 * i + 5) % 16; }
                else             { f = c ^ (b | ~d);              g = (7 * i) % 16; }

                f = f + a + K[i] + m[g];
                a = d; d = c; c = b;
                b += LeftRotate(f, S[i]);
            }

            a0 += a; b0 += b; c0 += c; d0 += d;
        }

        byte[] digest = new byte[16];
        WriteLe(digest, 0, a0);
        WriteLe(digest, 4, b0);
        WriteLe(digest, 8, c0);
        WriteLe(digest, 12, d0);
        return digest;
    }

    private static uint LeftRotate(uint x, int c) => (x << c) | (x >> (32 - c));

    private static void WriteLe(byte[] dst, int off, uint v)
    {
        dst[off] = (byte)v;
        dst[off + 1] = (byte)(v >> 8);
        dst[off + 2] = (byte)(v >> 16);
        dst[off + 3] = (byte)(v >> 24);
    }

    // Per-round left-rotate amounts.
    private static readonly int[] S =
    {
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    };

    // Binary integer parts of the sines of integers (radians) * 2^32 (RFC 1321 T table).
    private static readonly uint[] K =
    {
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee,
        0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
        0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa,
        0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed,
        0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
        0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
        0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
        0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
        0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    };
}
