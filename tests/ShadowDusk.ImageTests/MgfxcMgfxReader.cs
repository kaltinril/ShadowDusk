#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.ImageTests;

/// <summary>
/// Pragmatic reader for .mgfx files produced by the original MonoGame
/// <c>mgfxc</c> tool — distinct from ShadowDusk's own .mgfx format (which
/// uses the inverted <c>XFGM</c> signature and a different field layout).
///
/// <para>
/// The MonoGame mgfx-version-10 binary format has a complex layout
/// (cbuffers / shaders / parameters / techniques) that is fully documented in
/// MonoGame's <c>EffectReader.cs</c> source. We do not need a full parser
/// here; the only data the cross-validation suite requires is:
/// </para>
/// <list type="bullet">
///   <item>The raw UTF-8 GLSL source text(s) embedded in the file (the shader
///         blobs themselves).</item>
///   <item>The first technique's first pass — every candidate is a single
///         pass, so additional metadata is unnecessary.</item>
/// </list>
/// <para>
/// We extract the GLSL via a text scan rather than a binary walk:
/// MojoShader-generated GLSL always begins with <c>#ifdef GL_ES</c> and ends
/// with <c>}\r\n\r\n</c> immediately before the next structured field. The
/// scan is robust against any version-10 layout changes that don't affect
/// the GLSL payload itself.
/// </para>
/// <para>
/// If a future need arises for parameter offsets or technique names, switch
/// to a proper binary walker — until then, text scanning keeps the reader
/// simple and version-tolerant.
/// </para>
/// </summary>
public sealed class MgfxcMgfxReader
{
    /// <summary>Every GLSL shader blob found in the file, in source order.</summary>
    public IReadOnlyList<string> GlslShaders { get; }

    private MgfxcMgfxReader(IReadOnlyList<string> glslShaders)
    {
        GlslShaders = glslShaders;
    }

    /// <summary>
    /// Parses an mgfxc-format mgfx blob and returns the embedded GLSL shader
    /// source strings. Throws <see cref="InvalidDataException"/> if the
    /// signature isn't <c>MGFX</c>.
    /// </summary>
    public static MgfxcMgfxReader Parse(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < 6)
            throw new InvalidDataException($"Blob too small ({blob.Length} bytes) to be an mgfx file.");

        if (blob[0] != (byte)'M' || blob[1] != (byte)'G' || blob[2] != (byte)'F' || blob[3] != (byte)'X')
            throw new InvalidDataException(
                $"Invalid mgfx signature: expected \"MGFX\" at offset 0, found \"{(char)blob[0]}{(char)blob[1]}{(char)blob[2]}{(char)blob[3]}\".");

        // Decode the whole blob as UTF-8. Non-ASCII binary bytes will turn
        // into replacement chars but won't break the text scan — the GLSL
        // sections themselves are pure ASCII.
        string text = Encoding.UTF8.GetString(blob);

        // Match every GLSL block: starts with `#ifdef GL_ES` and ends at the
        // last `}` followed by line-ending(s) before the next binary chunk
        // (which always begins with a small byte then printable identifier
        // characters — e.g. `\x01\0\0\0\0\x05ps_s0\0`). We rely on the fact
        // that a GLSL `}` is unambiguous in this context.
        var matches = Regex.Matches(text, @"#ifdef GL_ES[\s\S]*?\}\r?\n", RegexOptions.Multiline);
        var shaders = new List<string>(matches.Count);
        foreach (Match m in matches)
            shaders.Add(m.Value);

        return new MgfxcMgfxReader(shaders);
    }
}
