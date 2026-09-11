#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.Slang;

/// <summary>
/// Merges one or more slangc <c>-target hlsl</c> emissions (one per discovered entry point)
/// into a single HLSL body for the synthesized <c>.fx</c> technique.
///
/// <para><b>Why a merge step exists at all:</b> slangc -target hlsl emits a fully
/// self-contained translation unit PER ENTRY POINT — every struct/cbuffer/resource it
/// actually uses is redeclared in full, not imported/shared across invocations (confirmed
/// empirically, Phase 66 A3: compiling a VS+PS pair's vertex and fragment entries
/// separately reproduces the identical <c>VSOutput_0</c> struct text in both outputs, byte
/// for byte). A VS+PS pair's two translation units can't just be concatenated — HLSL
/// rejects a type redeclared with the same name twice, even when the two declarations are
/// identical text. This performs the dedup: shared top-level declarations collapse to one
/// copy, and each entry's own function definition survives untouched.</para>
///
/// <para><b>What this deliberately does NOT do (left for A4):</b> resolve a genuine name
/// COLLISION — two declarations with the SAME slangc-mangled name but DIFFERENT bodies.
/// That can't happen from one shader's own VS+PS pair (mangling is a deterministic function
/// of the original identifier, Phase 65 §2), so it is not a case this stage needs to guard;
/// if it ever occurred, both copies are emitted and the downstream compiler's own
/// redefinition error is the (loud, correct) result — never a silent pick of one.</para>
/// </summary>
internal static class SlangHlslMerger
{
    // slangc's HLSL emission threads a '#line <n> ["file"]' directive ahead of every
    // top-level declaration and function. This is what makes per-declaration splitting
    // possible without parsing HLSL: each '#line' marks the start of the next self-contained
    // item, and everything before the first one is the shared boilerplate header
    // (#pragma pack_matrix, the NVAPI/loop-unroll #ifdef guards).
    private static readonly Regex LineDirective = new(
        @"^#line\b.*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Merges the per-entry-point HLSL translation units slangc produced, in the order
    /// given, deduplicating identical top-level declarations. A single-entry input (the
    /// common pixel-only case) is returned unchanged — no merge work to do.
    /// </summary>
    public static string Merge(IReadOnlyList<string> perEntryHlsl)
    {
        if (perEntryHlsl.Count == 0)
            return "";
        if (perEntryHlsl.Count == 1)
            return perEntryHlsl[0];

        var sb = new StringBuilder();
        // Ordinal: this key is generated HLSL text, never user-facing/locale-sensitive.
        var seenDeclarations = new HashSet<string>(StringComparer.Ordinal);
        bool preludeWritten = false;

        foreach (string unit in perEntryHlsl)
        {
            MatchCollection lineDirectives = LineDirective.Matches(unit);

            // The shared '#pragma pack_matrix'/NVAPI-guard header is identical across every
            // entry's output (same slangc invocation flags, same target) — keep one copy.
            if (!preludeWritten)
            {
                int preludeEnd = lineDirectives.Count > 0 ? lineDirectives[0].Index : unit.Length;
                sb.Append(unit, 0, preludeEnd);
                preludeWritten = true;
            }

            for (int i = 0; i < lineDirectives.Count; i++)
            {
                int start = lineDirectives[i].Index;
                int end = i + 1 < lineDirectives.Count ? lineDirectives[i + 1].Index : unit.Length;
                string block = unit[start..end];

                // Dedup key = the block's BODY only (the '#line' directive line itself
                // dropped), so two entries redeclaring the identical struct/cbuffer collapse
                // to one copy even though their '#line' numbers can legitimately differ
                // (e.g. when the input arrived over stdin under different invocations).
                int firstNewline = block.IndexOf('\n');
                string body = firstNewline >= 0 ? block[(firstNewline + 1)..].Trim() : "";

                if (body.Length == 0 || seenDeclarations.Add(body))
                    sb.Append(block);
            }
        }

        return sb.ToString();
    }
}
