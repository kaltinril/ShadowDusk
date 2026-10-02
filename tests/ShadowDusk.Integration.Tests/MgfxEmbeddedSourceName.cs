#nullable enable

using System.Security.Cryptography;
using System.Text;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Rewrites the source-file string an MGFX v11 container stores per shader (issue #274), so a
/// test can state "these two compiles differ in the embedded source name and in nothing else".
///
/// <para>Two things move when that string changes, and this reproduces both: the string itself
/// (a <see cref="BinaryWriter"/> length-prefixed UTF-8 string, once per shader record), and the
/// header's 4-byte effect key, which <c>MgfxWriter</c> derives from an MD5 of the whole body.</para>
/// </summary>
internal static class MgfxEmbeddedSourceName
{
    private const int HeaderLength = 10;   // "MGFX" + version + profile + int32 effect key
    private const int KeyOffset    = 6;
    private const int FooterLength = 4;    // trailing "MGFX"

    /// <summary>
    /// Returns <paramref name="mgfx"/> with every shader record's embedded
    /// <paramref name="from"/> replaced by <paramref name="to"/> and the effect key recomputed.
    /// Throws if <paramref name="from"/> is not embedded at all: a rewrite that changed nothing
    /// would turn the caller's comparison into a tautology.
    /// </summary>
    public static byte[] Replace(byte[] mgfx, string from, string to)
    {
        byte[] needle      = LengthPrefixed(from);
        byte[] replacement = LengthPrefixed(to);

        ReadOnlySpan<byte> body = mgfx.AsSpan(HeaderLength, mgfx.Length - HeaderLength - FooterLength);

        using var rewritten = new MemoryStream();
        int replaced = 0;
        while (true)
        {
            int hit = body.IndexOf(needle);
            if (hit < 0)
            {
                rewritten.Write(body);
                break;
            }

            rewritten.Write(body[..hit]);
            rewritten.Write(replacement);
            body = body[(hit + needle.Length)..];
            replaced++;
        }

        if (replaced == 0)
            throw new InvalidOperationException($"the container does not embed the source name '{from}'");

        byte[] newBody = rewritten.ToArray();

        using var output = new MemoryStream();
        output.Write(mgfx.AsSpan(0, KeyOffset));
        output.Write(MD5.HashData(newBody).AsSpan(0, 4));
        output.Write(newBody);
        output.Write(mgfx.AsSpan(mgfx.Length - FooterLength));
        return output.ToArray();
    }

    /// <summary>A string as <see cref="BinaryWriter.Write(string)"/> serializes it.</summary>
    private static byte[] LengthPrefixed(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            writer.Write(value);
        return stream.ToArray();
    }
}
