#nullable enable

using System.Text;
using ShadowDusk.Core;

namespace ShadowDusk.HLSL;

/// <summary>
/// Rewrites D3D9 stage-scoped register reservations for a single-stage SM1–3 compile.
/// D3D9-era HLSL can scope a register bind to one shader stage —
/// <c>float4 Color : register(vs, c0);</c> / <c>: register(ps, c1);</c> /
/// <c>sampler s : register(ps, s0);</c> — but vkd3d 1.17's HLSL compiler fails with
/// E5017 "Reservation shader target" on that stage-scoped form while fully honoring
/// the plain <c>: register(c0)</c> form. The FNA fx_2_0 pipeline therefore rewrites
/// the source PER STAGE before each vkd3d compile (once for vs_3_0, once for ps_3_0).
/// </summary>
public static class Sm3StageReservationRewriter
{
    /// <summary>Rewrites stage-scoped register reservations for a single-stage compile:
    /// the compiling stage's <c>register(vs|ps, X)</c> loses its stage prefix; the other
    /// stage's reservation is removed entirely (vkd3d then auto-assigns; the CTAB reports
    /// the actual register, which is what the fx_2_0 parameter binding consumes).</summary>
    /// <param name="source">The HLSL source to rewrite.</param>
    /// <param name="stage">The stage about to be compiled.</param>
    public static string Rewrite(string source, ShaderStage stage)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        int n = source.Length;

        while (i < n)
        {
            char c = source[i];

            // Line comment — copy through to (and including) the newline.
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                int end = source.IndexOf('\n', i);
                end = end < 0 ? n : end + 1;
                sb.Append(source, i, end - i);
                i = end;
                continue;
            }

            // Block comment — copy through to (and including) the closing '*/'.
            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                sb.Append(source, i, end - i);
                i = end;
                continue;
            }

            // String literal — copy through to the closing unescaped '"'.
            if (c == '"')
            {
                int j = i + 1;
                while (j < n && source[j] != '"')
                {
                    if (source[j] == '\\' && j + 1 < n)
                        j++; // skip the escaped character
                    j++;
                }
                if (j < n)
                    j++; // consume the closing quote
                sb.Append(source, i, j - i);
                i = j;
                continue;
            }

            // Candidate reservation: ': register ( vs|ps , <body> )'.
            if (c == ':' && TryMatchStageReservation(source, i, out int spanEnd, out char stageChar, out string body))
            {
                bool isCompilingStage = stage == ShaderStage.Vertex ? stageChar == 'v' : stageChar == 'p';
                if (isCompilingStage)
                {
                    // Compiling stage: keep the reservation, drop the stage prefix.
                    sb.Append(": register(").Append(body.Trim()).Append(')');
                }
                // Other stage: remove the whole annotation including the leading ':'.

                // Preserve any newlines from the consumed span so a (rare) multi-line
                // reservation does not shift later diagnostic line numbers.
                for (int k = i; k < spanEnd; k++)
                {
                    char ch = source[k];
                    if (ch is '\n' or '\r')
                        sb.Append(ch);
                }

                i = spanEnd;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// At a ':' character, attempts to match the stage-scoped reservation pattern
    /// '<c>: register ( vs|ps , &lt;body&gt; )</c>' with flexible whitespace and a
    /// case-insensitive stage token. On a match, returns the exclusive end offset of
    /// the closing ')', the lowercased stage discriminator ('v' or 'p'), and the raw
    /// body text between the ',' and the matching ')'. The plain stage-less form
    /// '<c>: register(c0)</c>' never matches and is left untouched.
    /// </summary>
    private static bool TryMatchStageReservation(string source, int colonIndex, out int spanEnd, out char stageChar, out string body)
    {
        spanEnd = 0;
        stageChar = '\0';
        body = string.Empty;

        int i = SkipWhitespace(source, colonIndex + 1);

        // 'register' keyword (tolerantly case-insensitive), not a prefix of a longer identifier.
        const string keyword = "register";
        if (i + keyword.Length > source.Length ||
            string.Compare(source, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }
        i += keyword.Length;
        if (i < source.Length && IsIdentifierChar(source[i]))
            return false;

        i = SkipWhitespace(source, i);
        if (i >= source.Length || source[i] != '(')
            return false;
        i++;

        i = SkipWhitespace(source, i);

        // Stage token: exactly 'vs' or 'ps' (case-insensitive), followed by ','.
        if (i + 2 > source.Length)
            return false;
        char first = char.ToLowerInvariant(source[i]);
        char second = char.ToLowerInvariant(source[i + 1]);
        if (first is not ('v' or 'p') || second != 's')
            return false;
        i += 2;

        int afterStage = SkipWhitespace(source, i);
        if (afterStage >= source.Length || source[afterStage] != ',')
            return false; // e.g. 'register(vsConstant)' — not a stage token.
        i = afterStage + 1;

        // Body: everything up to the matching ')'. Real reservation bodies are tiny
        // ("c0", "s15"); the scan is capped so adversarial source full of unterminated
        // 'register(vs,' candidates can't make every candidate rescan to EOF (quadratic).
        const int maxBodyLength = 256;
        int bodyStart = i;
        int depth = 1;
        while (i < source.Length && i - bodyStart <= maxBodyLength)
        {
            char ch = source[i];
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth == 0)
                {
                    body = source.Substring(bodyStart, i - bodyStart);
                    stageChar = first;
                    spanEnd = i + 1;
                    return true;
                }
            }
            i++;
        }

        return false; // Unterminated or implausibly long '(' — leave the source untouched.
    }

    private static int SkipWhitespace(string source, int i)
    {
        while (i < source.Length && char.IsWhiteSpace(source[i]))
            i++;
        return i;
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';
}
