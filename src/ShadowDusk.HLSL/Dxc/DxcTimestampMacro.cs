#nullable enable

using ShadowDusk.Core;
using ShadowDusk.HLSL.Preprocessing;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Refuses a DXC input that uses <c>__TIMESTAMP__</c> (issue #444), before DXC is called.
/// </summary>
/// <remarks>
/// <para>The pinned DXC (Vortice.Dxc 3.3.4's <c>dxcompiler</c>) ends the process with a fail-fast
/// (<c>0xC0000409</c>) when its preprocessor EXPANDS the <c>__TIMESTAMP__</c> macro: the
/// modification time of an in-memory source it cannot stat. Measured 2026-10-29 with a
/// preprocess-only run (<c>-P</c>) as well as a full compile, so it is every DXC call: the OpenGL,
/// Vulkan and DirectX 12 compiles, the raylib and SkSL converters, the zero-technique fallback's
/// preprocess run, and the entry-point prototype proof (<see cref="DxcSpirvEntryRedeclarations"/>),
/// which itself runs DXC's preprocessor. No .NET code can catch it.</para>
///
/// <para>The name is refused wherever it appears as a token, outside comments and string or
/// character literals, whether or not DXC would expand it there: whether it would (an inactive
/// <c>#if</c> branch, a macro body nobody uses) can only be answered by running DXC's
/// preprocessor, which is the run that crashes. A comment or a string that mentions it is not a
/// token and is compiled as before. Not caught: a name assembled by token pasting
/// (<c>__TIME ## STAMP__</c>), which still reaches DXC.</para>
/// </remarks>
internal static class DxcTimestampMacro
{
    /// <summary>The registered diagnostic code for a <c>__TIMESTAMP__</c> DXC would crash on.</summary>
    public const string ErrorCode = "SD0238";

    private const string Name = "__TIMESTAMP__";

    /// <summary>The <see cref="ErrorCode"/> refusal for <paramref name="source"/>, or null when it has no <c>__TIMESTAMP__</c> token.</summary>
    /// <param name="source">The text about to be handed to DXC (flattened: its <c>#line</c> directives map positions to the author's files).</param>
    /// <param name="sourceFileName">Display name used in diagnostics.</param>
    public static ShaderError? Refusal(string source, string? sourceFileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Contains(Name, StringComparison.Ordinal))
            return null;

        int at = FindToken(source);
        if (at < 0)
            return null;

        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < at; i++)
        {
            if (source[i] == '\n' || (source[i] == '\r' && (i + 1 >= source.Length || source[i + 1] != '\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }
        (string file, int mappedLine) = new SourceLineMap(source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), sourceFileName ?? string.Empty)
            .Resolve(line);
        return new ShaderError(
            File: file,
            Line: mappedLine,
            Column: at - lineStart + 1,
            Code: ErrorCode,
            Message: "'__TIMESTAMP__' is used here. The pinned DXC crashes the whole process when its preprocessor " +
                     "expands it (the modification time of a source it compiles from memory; issue #444), so " +
                     "ShadowDusk refuses any effect that names it before DXC runs, on every target DXC compiles " +
                     "(OpenGL, Vulkan, DirectX 12, raylib, SkSL). Remove it, or replace it with a macro of your own " +
                     "(a /Defines value, for example).");
    }

    /// <summary>The offset of the first <c>__TIMESTAMP__</c> identifier outside comments and literals, or -1.</summary>
    private static int FindToken(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] is not ('\n' or '\r'))
                    i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
            }
            else if (c is '"' or '\'')
            {
                i++;
                while (i < text.Length && text[i] != c && text[i] is not ('\n' or '\r'))
                    i += text[i] == '\\' ? 2 : 1;
                i++;
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                if (i - start == Name.Length && string.CompareOrdinal(text, start, Name, 0, Name.Length) == 0)
                    return start;
            }
            else if (char.IsAsciiDigit(c))
            {
                // A number (with any suffix letters) is one token: never the start of a name.
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                    i++;
            }
            else
            {
                i++;
            }
        }
        return -1;
    }
}
