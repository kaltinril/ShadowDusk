#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;

namespace ShadowDusk.Slang;

/// <summary>
/// OpenGL: a combined sampler the author declared with a sampler register
/// (<c>Sampler2D X : register(sN)</c>) lands on texture unit N, exactly where <c>mgfxc</c>
/// puts the legacy combined <c>sampler2D X : register(sN)</c>; and the units are filled in the
/// author's DECLARATION order, as <c>mgfxc</c> fills them.
/// </summary>
/// <remarks>
/// <para>slangc splits the combined sampler into <c>Texture2D X</c> (named back to the author's
/// global, issue #302) and <c>SamplerState X_sampler_0 : register(sN)</c> (the author's register
/// kept on the sampler half, issue #292). Handed to the GL allocator as is, that SamplerState is
/// a MODERN <c>register(sN)</c>, which mgfxc's split-pair rule reads as a reservation: the texture
/// then took the lowest FREE unit, so <c>register(s0)</c> put it on unit 1, off SpriteBatch's unit
/// 0 (the issue #252 symptom, for author-written registers), while DirectX 11 sampled t0.</para>
/// <para>Measured with <c>mgfxc</c> 3.8.4.1 <c>/Profile:OpenGL</c>, legacy combined samplers:
/// <c>register(s0)</c>, <c>(s1)</c>, <c>(s2)</c> give units 0, 1, 2; <c>A : register(s1)</c> +
/// <c>B : register(s0)</c> give A 1, B 0; <c>A : register(s2)</c> + an unregistered <c>B</c> give
/// A 2, B 0; an unregistered <c>A</c> + <c>B : register(s0)</c> give B 0, A 1. That is the
/// allocator's legacy rule (an explicit register pins its texture, the rest take the lowest free
/// unit, <c>SpirvCombinedSamplerPairs.ResolveSlots</c>), so the sampler half's register is moved
/// out of the HLSL (no reservation) and into <c>CompilerOptions.CombinedSamplerGlSlots</c> (the
/// pin), keyed by the texture parameter. A split <c>Texture2D</c> + <c>SamplerState</c> pair the
/// author wrote keeps the split-pair rule: only the halves of a combined sampler are touched, and
/// a combined sampler may share its register with such a SamplerState (mgfxc accepts that).</para>
/// <para>Two combined samplers pinning ONE unit are refused (<see cref="DuplicateUnitCode"/>):
/// fxc refuses the legacy pair (<c>X4500</c>, overlapping register semantics) and so do the
/// DirectX targets, and the allocator would otherwise silently move the second.</para>
/// <para>Declaration order: slangc emits globals in FIRST-USE order (measured, v2026.14.1:
/// <c>Texture2D T; SamplerState S : register(s0); Sampler2D A;</c> sampled A first comes back with
/// A declared first), while mgfxc fills units in the author's declaration order (T unit 1, A unit
/// 2 there). <see cref="DeclarationOrder"/> gives the allocator the author's order.</para>
/// </remarks>
internal static class SlangcCombinedSamplerGlSlots
{
    /// <summary><c>SD0644</c>: two combined samplers declare the same sampler register on OpenGL.</summary>
    public const string DuplicateUnitCode = "SD0644";

    /// <summary>
    /// Removes the sampler register from the sampler half of each combined sampler in
    /// <paramref name="combinedSamplers"/> (texture names, as the author wrote them) and returns
    /// the unit each one pins, or <see cref="DuplicateUnitCode"/> when two pin one unit.
    /// </summary>
    public static Result<(string Hlsl, IReadOnlyDictionary<string, int> Slots), ShaderError> Pin(
        string hlsl, IReadOnlyCollection<string> combinedSamplers, string slangSource, string sourceName)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string texture in combinedSamplers)
        {
            // 'SamplerState X_sampler_0 : register(s2);' or '... : register(s2, space1);' (a single
            // sampler, not an array: an array of combined samplers is refused on OpenGL, issue
            // #356). OpenGL has no register spaces, so the space does not change the unit.
            var half = new Regex(
                $@"^(?<decl>[ \t]*SamplerState[ \t]+{Regex.Escape(texture)}_sampler_\d+)[ \t]*:[ \t]*register[ \t]*\([ \t]*s(?<slot>\d+)[ \t]*(?:,[ \t]*space\d+[ \t]*)?\)[ \t]*;",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            Match match = half.Match(hlsl);
            if (!match.Success)
                continue;
            slots[texture] = int.Parse(match.Groups["slot"].Value, CultureInfo.InvariantCulture);
            hlsl = half.Replace(hlsl, m => m.Groups["decl"].Value + ";");
        }

        foreach (IGrouping<int, string> unit in slots.GroupBy(s => s.Value, s => s.Key))
        {
            if (unit.Count() < 2)
                continue;
            string masked = SlangSourceMask.Mask(slangSource);
            var located = unit
                .Select(name => (Name: name, Declaration: Regex.Match(
                    masked, $@"\bSampler\w*(?:\s*<[^;{{}}]*?>)?\s+{Regex.Escape(name)}\b", RegexOptions.CultureInvariant)))
                .OrderBy(d => d.Declaration.Success ? d.Declaration.Index : int.MaxValue)
                .ThenBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
            Match second = located[1].Declaration;
            return Result<(string, IReadOnlyDictionary<string, int>), ShaderError>.Fail(new ShaderError(
                File: sourceName,
                Line: second.Success ? LineOf(masked, second.Index) : 0,
                Column: second.Success ? ColumnOf(masked, second.Index) : 0,
                Code: DuplicateUnitCode,
                Message: $"OpenGL target: the combined samplers {string.Join(" and ", located.Select(d => "'" + d.Name + "'"))} " +
                         $"all declare register(s{unit.Key}). A combined sampler's sampler register is its texture unit (as for " +
                         "mgfxc's legacy 'sampler2D X : register(sN)'), and one unit holds one texture: fxc refuses the legacy " +
                         "pair (X4500, overlapping register semantics) and the DirectX targets refuse it too. Give each combined " +
                         "sampler its own register, or leave the register off and let the units be assigned."));
        }
        return Result<(string, IReadOnlyDictionary<string, int>), ShaderError>.Ok((hlsl, slots));
    }

    /// <summary>
    /// The author's declaration order of the global textures and combined samplers in
    /// <paramref name="entryText"/> (the entry's preprocess-only text when a pass read it, else
    /// its raw text), by name, for <c>CompilerOptions.GlTextureDeclarationOrder</c>. A name it does
    /// not declare (one from an imported module, or declared through a macro in a raw text) is
    /// absent, and then the allocator keeps slangc's order for the whole shader, as before.
    /// </summary>
    public static IReadOnlyDictionary<string, int> DeclarationOrder(string entryText, string sourceName, bool rawSource)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SlangcGlobalNameCollisions.GlobalDeclaration declaration in
                 SlangcGlobalNameCollisions.Scan(entryText, sourceName, rawSource))
        {
            if (declaration.Kind != SlangcGlobalNameCollisions.DeclarationKind.Variable)
                continue;
            // One name declared twice (two namespaces, or a text the scan mis-scoped) cannot say
            // which declaration is the texture: no order at all, slangc's own order stands.
            if (!order.TryAdd(declaration.Name, order.Count))
                return new Dictionary<string, int>();
        }
        return order;
    }

    /// <summary>
    /// The raw entry source with every conditional block (<c>#if</c>/<c>#ifdef</c>/<c>#ifndef</c>
    /// through its <c>#endif</c>, all branches) blanked, for <see cref="DeclarationOrder"/>: what is
    /// left is compiled whatever the macros are, in this order. Null when the raw text cannot speak
    /// for slangc's declaration order at all: a <c>#define</c> or <c>#include</c> (a macro use or an
    /// included file can add or rename a declaration), a token paste, a line splice, a <c>-D</c>
    /// name the source spells, or unbalanced conditionals. A texture declared inside a blanked
    /// block is simply absent, and an absent texture leaves the allocator in slangc's own order
    /// (the review's repro: <c>#if FEATURE_X / Texture2D B; Texture2D A; / #else / Texture2D A;
    /// Texture2D B; / #endif</c> must not be ordered by its inactive branch).
    /// </summary>
    public static string? UnconditionalRawText(string slangSource, IReadOnlyList<ShadowDusk.Core.Preprocessor.UserDefine> defines)
    {
        if (slangSource.Contains("##", StringComparison.Ordinal)
            || Regex.IsMatch(slangSource, @"\\\r?\n")
            || Regex.IsMatch(slangSource, @"^[ \t]*#[ \t]*(?:define|include)\b", RegexOptions.Multiline)
            || defines.Any(d => Regex.IsMatch(slangSource, $@"(?<!\w){Regex.Escape(d.Name)}(?!\w)")))
        {
            return null;
        }

        string[] lines = SlangSourceMask.Mask(slangSource).Split('\n');
        int depth = 0;
        // Braces in the conditional text between two directives: a segment that opens or closes
        // a scope it does not also close or open (a struct header written per branch, PR #384
        // review) would leave the text outside the block mis-scoped, so no raw order at all.
        int segmentBraces = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            Match directive = Regex.Match(lines[i], @"^[ \t]*#[ \t]*(?<word>\w+)");
            if (directive.Success)
            {
                if (depth > 0 && segmentBraces != 0)
                    return null;
                segmentBraces = 0;
            }
            else if (depth > 0)
            {
                segmentBraces += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
            }

            string word = directive.Success ? directive.Groups["word"].Value : "";
            if (word is "if" or "ifdef" or "ifndef")
                depth++;
            bool blank = depth > 0 || directive.Success;
            if (word == "endif" && --depth < 0)
                return null;
            if (blank)
                lines[i] = new string(' ', lines[i].Length);
        }
        return depth == 0 ? string.Join('\n', lines) : null;
    }

    private static int LineOf(string text, int offset)
    {
        int line = 1;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
                line++;
        }
        return line;
    }

    private static int ColumnOf(string text, int offset)
    {
        int lineStart = offset == 0 ? -1 : text.LastIndexOf('\n', offset - 1);
        return offset - lineStart;
    }
}
