#nullable enable

using System.Text;

namespace ShadowDusk.Compiler.Slang;

/// <summary>
/// Blanks comments and string-literal contents so a regex scan over Slang source only sees code.
/// The result has the same length and the same newlines as the input, so match indices and line
/// numbers map straight back onto the original text. String quotes are kept so a scan can still
/// see that a literal sits at a position and read its text from the original by index.
/// </summary>
internal static class SlangSourceMask
{
    public static string Mask(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    sb.Append(text[i] == '\r' ? '\r' : ' ');
                    i++;
                }
                continue;
            }

            if (c == '/' && next == '*')
            {
                sb.Append("  ");
                i += 2;
                // Block comments do not nest in Slang/HLSL: the first "*/" closes, so "/* /* */" is closed.
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    sb.Append(text[i] is '\n' or '\r' ? text[i] : ' ');
                    i++;
                }
                if (i < text.Length)
                {
                    sb.Append("  ");
                    i += 2;
                }
                continue;
            }

            if (c == '"')
            {
                sb.Append('"');
                i++;
                // An unterminated literal ends at the line break rather than swallowing the file.
                while (i < text.Length && text[i] != '"' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] != '\n')
                    {
                        sb.Append("  ");
                        i += 2;
                        continue;
                    }
                    sb.Append(text[i] == '\r' ? '\r' : ' ');
                    i++;
                }
                if (i < text.Length && text[i] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
