#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// A <b>GL-only</b> HLSL source rewrite (Phase 41 GAP-2) that retargets a pixel shader's
/// multi-render-target struct output semantics from the legacy D3D9 <c>: COLOR&lt;n&gt;</c> to the
/// modern <c>: SV_Target&lt;n&gt;</c>, so DXC's HLSL → SPIR-V path (the OpenGL backend) accepts them.
///
/// <para><b>Why this exists and why it is GL-only.</b> A Nez-style deferred effect returns a
/// STRUCT whose members carry the output semantics:
/// <code>struct PixelMultiTextureOut { float4 color : COLOR0; float4 normal : COLOR1; };</code>
/// <c>FxPreParser</c>'s existing <c>: COLOR</c> → <c>: SV_Target</c> rewrite (B6) only fires
/// on the FUNCTION-RETURN form (token before <c>COLOR</c> is <c>)</c>), deliberately excluding
/// struct members, so these reach DXC unrewritten and DXC rejects them
/// (<c>"Semantic COLOR is invalid for shader model: ps"</c>). The DirectX backend (vkd3d) accepts
/// <c>COLOR</c> outputs natively, and ShadowDusk runs <c>FxPreParser</c> ONCE producing a
/// single shared source for both backends — so rewriting in the pre-parser would change the DX
/// bytes too (a byte-identity regression). This rewrite therefore runs DOWNSTREAM of the shared
/// pre-parse, applied to a private copy of the source fed ONLY to the OpenGL DXC compiles.</para>
///
/// <para><b>Interpolant safety.</b> Only a struct that is the RETURN TYPE of a pixel-shader entry
/// point is rewritten. A PS-<i>input</i> / VS-<i>output</i> interpolant struct (e.g.
/// <c>VertexShaderOutput { float4 Color : COLOR0; }</c>) is a valid DXC input semantic and is never
/// touched, because it is a parameter type, not a PS entry's return type. Pixel-entry names come
/// from the already-parsed techniques (no re-scan / guessing).</para>
/// </summary>
internal static class GlStructOutputColorRewriter
{
    /// <summary>
    /// Returns <paramref name="hlsl"/> with each pixel-entry RETURN STRUCT's member
    /// <c>: COLOR&lt;n&gt;</c> semantics rewritten to <c>: SV_Target&lt;n&gt;</c>. A no-op (returns the
    /// input reference unchanged) when no pixel entry returns a struct with COLOR members — so a
    /// shader without this shape is byte-identical.
    /// </summary>
    public static string Rewrite(string hlsl, IReadOnlyList<TechniqueInfo> techniques)
    {
        // 1) Distinct pixel-entry function names (from the already-parsed techniques).
        var pixelEntries = new HashSet<string>(StringComparer.Ordinal);
        foreach (TechniqueInfo tech in techniques)
            foreach (PassInfo pass in tech.Passes)
                if (!string.IsNullOrEmpty(pass.PixelEntryPoint))
                    pixelEntries.Add(pass.PixelEntryPoint!);

        if (pixelEntries.Count == 0)
            return hlsl;

        // 2) Resolve each PS entry's return-type identifier (the token before the entry name in
        //    its top-level function definition). A builtin return (float4, etc.) yields a name
        //    that matches no struct, so it is harmlessly skipped in step 3.
        var outputStructs = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in pixelEntries)
        {
            string? returnType = FindFunctionReturnType(hlsl, entry);
            if (returnType is not null)
                outputStructs.Add(returnType);
        }

        if (outputStructs.Count == 0)
            return hlsl;

        // 2b) SAFETY: never rewrite a struct that is ALSO used as a function PARAMETER type — it
        //     is a shader INPUT there (its COLOR0 is a valid DXC input interpolant), and rewriting
        //     the shared definition to SV_Target would break that input use. A struct used as both
        //     a PS return AND an input is pathological; we leave it unrewritten so the GL compile
        //     surfaces the original, LOUD COLOR error rather than a silently-wrong rewrite.
        outputStructs.RemoveWhere(s => IsUsedAsParameterType(hlsl, s));
        if (outputStructs.Count == 0)
            return hlsl;

        // 3) Rewrite the COLOR members of each resolved output struct only.
        string result = hlsl;
        foreach (string structName in outputStructs)
            result = RewriteStructColorMembers(result, structName);

        return result;
    }

    /// <summary>
    /// True if <paramref name="structName"/> is used as a function PARAMETER type — i.e. the
    /// pattern <c>( [in|out|inout|const] structName ident</c> or the same after a <c>,</c>. A
    /// parameter position is the only place a bare <c>StructName identifier</c> follows <c>(</c>/
    /// <c>,</c> (a CALL passes a value/expression, not <c>Type ident</c>; a cast is
    /// <c>(StructName)</c> followed by <c>)</c>). A local declaration (<c>StructName o;</c> inside
    /// a body) follows <c>{</c>/<c>;</c>, never <c>(</c>/<c>,</c>, so it does not match.
    /// </summary>
    private static bool IsUsedAsParameterType(string hlsl, string structName)
    {
        int i = 0;
        while (i < hlsl.Length)
        {
            i = SkipCommentAt(hlsl, i, out bool skipped);
            if (skipped) continue;
            if (i >= hlsl.Length) break;

            if (hlsl[i] == '(' || hlsl[i] == ',')
            {
                int t = SkipParamQualifiers(hlsl, SkipWsAndComments(hlsl, i + 1));
                if (t < hlsl.Length && MatchesWordAt(hlsl, t, structName))
                {
                    int afterType = SkipWsAndComments(hlsl, t + structName.Length);
                    if (afterType < hlsl.Length && IsIdentStart(hlsl[afterType]))
                        return true; // 'structName <paramName>' in a parameter position
                }
            }
            i++;
        }
        return false;
    }

    /// <summary>Skip leading HLSL parameter qualifiers (<c>in</c>/<c>out</c>/<c>inout</c>/
    /// <c>const</c>/<c>uniform</c>) so a qualified parameter type is still recognized.</summary>
    private static int SkipParamQualifiers(string s, int i)
    {
        while (true)
        {
            int matched = -1;
            foreach (string q in ParamQualifiers)
            {
                if (MatchesWordAt(s, i, q)) { matched = i + q.Length; break; }
            }
            if (matched < 0) return i;
            i = SkipWsAndComments(s, matched);
        }
    }

    private static readonly string[] ParamQualifiers = { "in", "out", "inout", "const", "uniform" };

    // -- Member-semantic rewrite (only within the named struct's balanced braces). ---------

    // ': COLOR' or ': COLOR<digit>' as a complete token — the legacy D3D9 PS output semantic.
    // CASE-INSENSITIVE: HLSL semantics are case-insensitive, and the sibling FxPreParser B6
    // function-return rewrite matches the same way (OrdinalIgnoreCase), so a struct authored
    // ': Color0' / ': color0' must be retargeted exactly like ': COLOR0'. The replacement always
    // emits the canonical 'SV_Target', which DXC accepts regardless of the source case.
    private static readonly Regex ColorMemberSemantic =
        new(@"(:\s*)COLOR(\d?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Note: the COLOR-member regex runs over the whole struct body, so a COLOR token inside a
    // COMMENT within the struct body (e.g. `/* legacy : COLOR1 */`) is also rewritten. This is
    // harmless — DXC ignores comments — so it is not special-cased.
    private static string RewriteStructColorMembers(string hlsl, string structName)
    {
        if (!TryFindStructBodyOpen(hlsl, structName, out int openBrace))
            return hlsl;

        int closeBrace = MatchBrace(hlsl, openBrace);
        if (closeBrace < 0)
            return hlsl; // unbalanced (should not happen in valid HLSL) — do not corrupt

        string body = hlsl.Substring(openBrace, closeBrace - openBrace + 1);
        string rewritten = ColorMemberSemantic.Replace(body, "${1}SV_Target${2}");
        if (ReferenceEquals(rewritten, body) || rewritten == body)
            return hlsl;

        return hlsl.Substring(0, openBrace) + rewritten + hlsl.Substring(closeBrace + 1);
    }

    // -- PS-entry return-type resolution. ----------------------------------------------------

    /// <summary>
    /// Finds the top-level function definition of <paramref name="entry"/> and returns the
    /// identifier immediately before it (its return type), or <c>null</c> if not found. The
    /// definition shape is <c>&lt;returnType&gt; &lt;entry&gt; (</c> at brace depth 0; a CALL to the
    /// entry sits at depth &gt; 0 (inside a body) and is preceded by an operator/keyword, so it is
    /// never mistaken for the definition.
    /// </summary>
    private static string? FindFunctionReturnType(string hlsl, string entry)
    {
        int depth = 0;
        int i = 0;
        while (i < hlsl.Length)
        {
            i = SkipCommentAt(hlsl, i, out bool skipped);
            if (skipped) continue;
            if (i >= hlsl.Length) break;

            char c = hlsl[i];
            if (c == '{') { depth++; i++; continue; }
            if (c == '}') { if (depth > 0) depth--; i++; continue; }

            if (depth == 0 && IsIdentStart(c) && MatchesWordAt(hlsl, i, entry))
            {
                int nameStart = i;
                int nameEnd = i + entry.Length; // exclusive

                // Next significant char must be '(' (a function name).
                int after = SkipWsAndComments(hlsl, nameEnd);
                if (after < hlsl.Length && hlsl[after] == '(')
                {
                    // Previous significant token must be a plain identifier (the return type),
                    // and not a keyword that precedes a call/return.
                    string? prev = PreviousIdentifier(hlsl, nameStart);
                    if (prev is not null && !IsKeyword(prev))
                        return prev;
                }
                i = nameEnd;
                continue;
            }

            i++;
        }
        return null;
    }

    /// <summary>The identifier ending just before <paramref name="pos"/> (skipping whitespace and
    /// comments), or <c>null</c> if the preceding significant token is not an identifier.</summary>
    private static string? PreviousIdentifier(string s, int pos)
    {
        int i = pos - 1;
        // skip whitespace + comments backwards
        while (i >= 0)
        {
            if (char.IsWhiteSpace(s[i])) { i--; continue; }
            // end of a line comment / block comment is hard to scan backwards reliably; a
            // return-type identifier is never preceded by a comment in practice, so if we hit a
            // comment terminator we bail (treat as "no identifier").
            if (s[i] == '/' || s[i] == '*') return null;
            break;
        }
        if (i < 0 || !IsIdentChar(s[i]))
            return null;

        int end = i + 1; // exclusive
        while (i >= 0 && IsIdentChar(s[i])) i--;
        int start = i + 1;
        // The token must start with an identifier-start char (not a digit).
        return IsIdentStart(s[start]) ? s.Substring(start, end - start) : null;
    }

    private static bool TryFindStructBodyOpen(string hlsl, string structName, out int openBrace)
    {
        openBrace = -1;
        int depth = 0;
        int i = 0;
        while (i < hlsl.Length)
        {
            i = SkipCommentAt(hlsl, i, out bool skipped);
            if (skipped) continue;
            if (i >= hlsl.Length) break;

            char c = hlsl[i];
            if (c == '{') { depth++; i++; continue; }
            if (c == '}') { if (depth > 0) depth--; i++; continue; }

            if (depth == 0 && c == 's' && MatchesWordAt(hlsl, i, "struct"))
            {
                int afterStruct = SkipWsAndComments(hlsl, i + "struct".Length);
                if (afterStruct < hlsl.Length && MatchesWordAt(hlsl, afterStruct, structName))
                {
                    int afterName = SkipWsAndComments(hlsl, afterStruct + structName.Length);
                    if (afterName < hlsl.Length && hlsl[afterName] == '{')
                    {
                        openBrace = afterName;
                        return true;
                    }
                }
                i += "struct".Length;
                continue;
            }

            i++;
        }
        return false;
    }

    // -- Low-level scanning helpers (comment-aware). -----------------------------------------

    /// <summary>If a <c>//</c> or <c>/* */</c> comment begins at <paramref name="i"/>, advance past
    /// it and set <paramref name="skipped"/> = true; otherwise return <paramref name="i"/> unchanged
    /// with <paramref name="skipped"/> = false.</summary>
    private static int SkipCommentAt(string s, int i, out bool skipped)
    {
        skipped = false;
        if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
        {
            int nl = s.IndexOf('\n', i);
            skipped = true;
            return nl < 0 ? s.Length : nl;
        }
        if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
        {
            int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
            skipped = true;
            return end < 0 ? s.Length : end + 2;
        }
        return i;
    }

    private static int SkipWsAndComments(string s, int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
            {
                int nl = s.IndexOf('\n', i);
                if (nl < 0) return s.Length;
                i = nl;
                continue;
            }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return s.Length;
                i = end + 2;
                continue;
            }
            return i;
        }
        return i;
    }

    /// <summary>Brace-match from an opening <c>{</c> to its <c>}</c>, skipping comments; -1 if
    /// unbalanced.</summary>
    private static int MatchBrace(string s, int open)
    {
        int depth = 0;
        int i = open;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
            {
                int nl = s.IndexOf('\n', i);
                if (nl < 0) return -1;
                i = nl;
                continue;
            }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return -1;
                i = end + 2;
                continue;
            }
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
            i++;
        }
        return -1;
    }

    /// <summary>True if <paramref name="word"/> appears at <paramref name="i"/> with identifier
    /// boundaries on both sides (ordinal, case-sensitive).</summary>
    private static bool MatchesWordAt(string s, int i, string word)
    {
        if (i + word.Length > s.Length) return false;
        if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
        if (i > 0 && IsIdentChar(s[i - 1])) return false;
        int after = i + word.Length;
        if (after < s.Length && IsIdentChar(s[after])) return false;
        return true;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // HLSL keywords that can precede an `entry(` token but are NOT a return type (so a call or
    // statement is never mistaken for the function definition).
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "return", "if", "else", "for", "while", "do", "switch", "case",
    };

    private static bool IsKeyword(string s) => Keywords.Contains(s);
}
