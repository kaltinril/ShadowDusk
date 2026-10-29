#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// DirectX 11: puts the <c>t</c>-register resources of one compiled shader on the slots fxc (and so
/// <c>mgfxc</c> and KNI's pipeline) gives them, where the SM4 rewrite of a legacy shader leaves the
/// backend numbering them in declaration order instead.
///
/// <para><b>The rule</b> (measured against <c>mgfxc</c> 3.8.4.1 <c>/Profile:DirectX_11</c>,
/// 2026-10-28, the shapes of <c>tests/fixtures/golden/dx11-texture-slots/</c> plus the 27
/// legacy-sampler goldens), per compiled shader:</para>
/// <list type="number">
/// <item>Every explicit <c>register(tN)</c> on a <c>t</c>-register declaration (any texture, typed,
/// structured or byte-address buffer, <c>tbuffer</c>) takes <c>tN</c> out of circulation, whether
/// or not anything reads it. A MODERN resource read directly keeps its register.</item>
/// <item>Then every LEGACY sampler's texture (<c>sampler2D A;</c>, a <c>sampler_state</c> block,
/// read with <c>tex2D</c> and its family) the shader reads, in the order it first reads it:
/// EVALUATION order, so an argument before the call that takes it and an inner read before the
/// dependent read around it. fxc gives each legacy sampler its own texture object, so a register
/// written on the <c>texture2D T : register(tN)</c> a <c>sampler_state</c> names only RESERVES
/// <c>tN</c>; the sampler's texture takes the next free slot like any other legacy texture. A
/// legacy sampler's own <c>register(sN)</c> decides its sampler slot only.</item>
/// <item>Then every MODERN resource without a register, in declaration order.</item>
/// </list>
/// <para>Each takes the lowest free slot. With no legacy texture in the shader the rule is plain
/// declaration order, which the backend already gives, so nothing is touched.</para>
///
/// <para><b>Where the evaluation order comes from.</b> fxc numbers the slots in evaluation order
/// before it optimises or schedules, so no compiled instruction stream carries the order:
/// <c>d3dcompiler_47</c> hoists the reads feeding a <c>clip</c>/<c>discard</c> and reorders
/// independent reads (<c>tex2D(A) * tex2D(B) + tex2D(C) * 2</c> is emitted C first), and
/// vkd3d-shader's parser swaps the operands of <c>&gt;</c> and <c>&lt;=</c>. The order is read
/// from the SOURCE (<see cref="HlslReadOrder"/>), identically on every backend and host, and the set
/// of resources that survived optimisation from the shipped bytes' own RDEF.</para>
///
/// <para>When the plan differs from what the backend chose, the caller recompiles with each
/// resource's declaration pinned by an explicit <c>register(tN)</c> (<see cref="Pin"/>).</para>
/// </summary>
internal static class DxTextureSlotOrder
{
    /// <summary>
    /// The slot each <c>t</c>-register resource of the shipped compile should take, or <c>null</c>
    /// when the backend's slots already follow the rule (or the shader has no legacy texture).
    /// </summary>
    /// <param name="resources">The shipped compile's <c>t</c>-register bindings (name, slot).</param>
    /// <param name="firstReadOrder">Resource names in evaluation (first-read) order.</param>
    /// <param name="legacyTextures">Names of the textures legacy samplers sample through.</param>
    /// <param name="explicitRegisters">Every explicit <c>t</c> register that applies to this stage, by name.</param>
    public static IReadOnlyDictionary<string, int>? Plan(
        IReadOnlyList<(string Name, int Slot)> resources,
        IReadOnlyList<string> firstReadOrder,
        IReadOnlySet<string> legacyTextures,
        IReadOnlyList<(string Name, int Slot)> explicitRegisters)
    {
        if (!resources.Any(r => legacyTextures.Contains(r.Name)))
            return null;
        if (resources.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != resources.Count)
            return null;

        var plan = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<int>(explicitRegisters.Select(r => r.Slot));
        foreach ((string name, _) in resources)
        {
            if (legacyTextures.Contains(name))
                continue;
            foreach ((string regName, int slot) in explicitRegisters)
            {
                if (string.Equals(regName, name, StringComparison.Ordinal))
                    plan[name] = slot;
            }
        }

        int ReadRank(string name)
        {
            int i = -1;
            for (int k = 0; k < firstReadOrder.Count; k++)
            {
                if (string.Equals(firstReadOrder[k], name, StringComparison.Ordinal))
                {
                    i = k;
                    break;
                }
            }
            return i < 0 ? int.MaxValue : i;
        }

        var legacy = resources
            .Where(r => legacyTextures.Contains(r.Name))
            .OrderBy(r => ReadRank(r.Name))
            .ThenBy(r => r.Slot);
        var modern = resources
            .Where(r => !legacyTextures.Contains(r.Name) && !plan.ContainsKey(r.Name))
            .OrderBy(r => r.Slot);

        int next = 0;
        foreach ((string name, _) in legacy.Concat(modern).ToList())
        {
            while (taken.Contains(next))
                next++;
            plan[name] = next;
            taken.Add(next);
        }

        return resources.All(r => plan[r.Name] == r.Slot) ? null : plan;
    }

    private const string ResourceType =
        @"(?:[Tt]exture\w*|Buffer|StructuredBuffer|ByteAddressBuffer|tbuffer)(?:\s*<[^<>;]*>)?";

    /// <summary>
    /// <paramref name="text"/> with every top-level declaration of each planned resource carrying
    /// its planned <c>register(tN)</c> (an existing <c>t</c> register clause is replaced), or
    /// <c>null</c> when a declaration cannot be pinned safely: none found at top level (declared
    /// through a macro), or one in another form (a declarator list, an array, an annotation). Text in
    /// comments, preprocessor lines and anything inside braces or parentheses (function bodies,
    /// parameters, <c>cbuffer</c> blocks) is never matched; a declaration in an <c>#if</c> branch the
    /// compile does not take is pinned too, which is harmless.
    /// </summary>
    public static string? Pin(string text, IReadOnlyDictionary<string, int> plan)
    {
        bool[] topLevel = TopLevelCode(text);
        foreach ((string name, int slot) in plan.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string n = Regex.Escape(name);
            var any = new Regex(@"(?<![A-Za-z0-9_])" + ResourceType + @"\s+" + n + "(?![A-Za-z0-9_])");
            var pinnable = new Regex(
                @"(?<![A-Za-z0-9_])(" + ResourceType + @"\s+" + n + @")\s*" +
                @"(?::\s*register\s*\(\s*(?:[vp]s\w*\s*,\s*)?[tT]\d+\s*(?:,\s*space\d+\s*)?\)\s*)?(?<end>[;{])");

            var anyMatches = any.Matches(text).Where(m => topLevel[m.Index]).ToList();
            if (anyMatches.Count == 0)
                return null;
            var pinMatches = pinnable.Matches(text).Where(m => topLevel[m.Index]).ToList();
            if (pinMatches.Count != anyMatches.Count)
                return null;

            for (int i = pinMatches.Count - 1; i >= 0; i--)
            {
                Match m = pinMatches[i];
                // A tbuffer block keeps its `{`; every other declaration ends at its `;`.
                string end = m.Groups["end"].Value == "{" ? " {" : ";";
                string replacement = $"{m.Groups[1].Value} : register(t{slot}){end}";
                text = text[..m.Index] + replacement + text[(m.Index + m.Length)..];
            }
            topLevel = TopLevelCode(text);
        }
        return text;
    }

    /// <summary>
    /// For each character: whether it is code at brace and parenthesis depth 0, outside comments,
    /// string literals and preprocessor lines.
    /// </summary>
    private static bool[] TopLevelCode(string text)
    {
        var mask = new bool[text.Length + 1];
        int braces = 0, parens = 0;
        bool lineStart = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                lineStart = true;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }
            if (lineStart && c == '#')
            {
                // A directive runs to the end of the line, continuations included.
                while (i < text.Length && !(text[i] == '\n' && (i == 0 || text[i - 1] != '\\'))) i++;
                lineStart = true;
                continue;
            }
            if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"' && text[i] != '\n')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                continue;
            }
            if (c == '\n') { lineStart = true; continue; }
            if (!char.IsWhiteSpace(c)) lineStart = false;
            switch (c)
            {
                case '{': braces++; break;
                case '}': braces = Math.Max(0, braces - 1); break;
                case '(': parens++; break;
                case ')': parens = Math.Max(0, parens - 1); break;
                default:
                    mask[i] = braces == 0 && parens == 0;
                    break;
            }
        }
        return mask;
    }

    /// <summary>Whether <paramref name="resources"/> sit exactly on <paramref name="plan"/>.</summary>
    public static bool Matches(IReadOnlyList<(string Name, int Slot)> resources, IReadOnlyDictionary<string, int> plan) =>
        resources.Count == plan.Count && resources.All(r => plan.TryGetValue(r.Name, out int slot) && slot == r.Slot);
}
