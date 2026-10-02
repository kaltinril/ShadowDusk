#nullable enable

using System.Security.Cryptography;
using System.Text;

namespace ShadowDusk.Validation.SharedMgfx;

/// <summary>
/// Reads and rewrites the <b>source-file string an MGFX v11 container stores per shader</b>
/// (issue #274). BCL only, so the two content-pipeline gates that link it
/// (<c>validation/MgcbPlugin</c>, <c>validation/ContentBuilder</c>) stay free of any ShadowDusk
/// reference and keep measuring what a consumer's build actually produced.
///
/// <para><b>Why the gates need it.</b> DirectX 12 and Vulkan are always MGFX v11. The ShadowDusk
/// CLI writes the source path it was given into that string, as the <c>mgfxc</c> CLI does;
/// ShadowDusk's content processor writes <c>&lt;unknown&gt;</c>, as MonoGame's stock
/// <c>EffectProcessor</c> does, because a content build is handed an absolute path that must not
/// end up in the game's content. So for a v11 target the processor's payload is, by design, the
/// CLI's bytes with only this string (and the effect key derived from the body) different, and
/// "byte-identical to the CLI" has to be asserted through <see cref="Replace"/>.</para>
///
/// <para>The walk follows MonoGame's own <c>Effect</c>/<c>Shader</c> reader up to the end of the
/// shader table (header, constant buffers, shader records), which is the same layout whichever
/// compiler wrote the file, so it reads a stock <c>.xnb</c> payload and a ShadowDusk one alike.</para>
/// </summary>
internal static class MgfxSourceFile
{
    /// <summary>What MonoGame's stock <c>EffectProcessor</c> writes (measured, <c>dotnet-mgcb</c> 3.8.5).</summary>
    public const string Stock = "<unknown>";

    private const int HeaderLength = 10;   // "MGFX" + version + profile + int32 effect key
    private const int KeyOffset    = 6;
    private const int FooterLength = 4;    // trailing "MGFX"

    /// <summary>The container's MGFX version byte.</summary>
    public static byte Version(byte[] mgfx) => mgfx[4];

    /// <summary>
    /// Every shader record's source-file string, in record order. Empty for a container older
    /// than v11, which has no such field.
    /// </summary>
    public static IReadOnlyList<string> ReadAll(byte[] mgfx) => Walk(mgfx).Select(f => f.Value).ToList();

    /// <summary>
    /// <paramref name="mgfx"/> with every shader record's source-file string replaced by
    /// <paramref name="replacement"/> and the header's effect key recomputed the way ShadowDusk's
    /// writer derives it (the first four bytes of an MD5 of the body), i.e. the exact bytes
    /// ShadowDusk writes for the same compile under that embedded name.
    /// </summary>
    public static byte[] Replace(byte[] mgfx, string replacement)
    {
        IReadOnlyList<Field> fields = Walk(mgfx);
        if (fields.Count == 0)
            throw new InvalidOperationException("the container has no source-file field to replace (MGFX v10?)");

        byte[] encoded = LengthPrefixed(replacement);

        using var body = new MemoryStream();
        int cursor = HeaderLength;
        foreach (Field field in fields)
        {
            body.Write(mgfx.AsSpan(cursor, field.Offset - cursor));
            body.Write(encoded);
            cursor = field.Offset + field.Length;
        }
        body.Write(mgfx.AsSpan(cursor, mgfx.Length - FooterLength - cursor));
        byte[] newBody = body.ToArray();

        using var output = new MemoryStream();
        output.Write(mgfx.AsSpan(0, KeyOffset));
        output.Write(MD5.HashData(newBody).AsSpan(0, 4));
        output.Write(newBody);
        output.Write(mgfx.AsSpan(mgfx.Length - FooterLength));
        return output.ToArray();
    }

    /// <summary>Whether <paramref name="haystack"/> contains <paramref name="text"/> as UTF-8.</summary>
    public static bool ContainsUtf8(byte[] haystack, string text)
        => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    /// <summary>One source-file string: where its length prefix starts, how many bytes it spans, and its value.</summary>
    private sealed record Field(int Offset, int Length, string Value);

    private static IReadOnlyList<Field> Walk(byte[] mgfx)
    {
        if (mgfx.Length < HeaderLength + FooterLength
            || mgfx[0] != 'M' || mgfx[1] != 'G' || mgfx[2] != 'F' || mgfx[3] != 'X')
        {
            throw new InvalidOperationException("not an MGFX container");
        }

        if (Version(mgfx) <= 10)
            return [];

        using var stream = new MemoryStream(mgfx);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        stream.Position = HeaderLength;

        // Constant buffers: name, int16 size, then (int32 parameter index, uint16 offset) pairs.
        int constantBuffers = reader.ReadInt32();
        for (int i = 0; i < constantBuffers; i++)
        {
            reader.ReadString();
            reader.ReadInt16();
            int parameters = reader.ReadInt32();
            stream.Position += parameters * 6L;
        }

        var fields = new List<Field>();
        int shaders = reader.ReadInt32();
        for (int i = 0; i < shaders; i++)
        {
            reader.ReadBoolean();                       // isVertexShader

            int start = (int)stream.Position;
            string sourceFile = reader.ReadString();    // THE field (v11+)
            fields.Add(new Field(start, (int)stream.Position - start, sourceFile));
            reader.ReadString();                        // entry point (v11+)

            int bytecode = reader.ReadInt32();
            stream.Position += bytecode;

            int samplers = reader.ReadByte();
            for (int s = 0; s < samplers; s++)
            {
                stream.Position += 3;                   // type, texture slot, sampler slot
                if (reader.ReadBoolean())               // baked sampler state
                    stream.Position += 8 + 4 + 4 + 4;   // 8 bytes, 2 x int32, 1 x float
                reader.ReadString();                    // name
                reader.ReadByte();                      // parameter index
            }

            int boundBuffers = reader.ReadByte();
            stream.Position += boundBuffers;

            int attributes = reader.ReadByte();
            for (int a = 0; a < attributes; a++)
            {
                reader.ReadString();                    // name
                stream.Position += 4;                   // usage, index, int16 location
            }
        }

        if (stream.Position > mgfx.Length - FooterLength)
            throw new InvalidOperationException("the shader table runs past the end of the container");

        return fields;
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
