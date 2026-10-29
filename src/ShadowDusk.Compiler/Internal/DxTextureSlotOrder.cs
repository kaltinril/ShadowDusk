#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// DirectX 11: puts the textures of one compiled shader on the <c>tN</c> slots fxc (and so
/// <c>mgfxc</c> and KNI's pipeline) gives them, where the SM4 rewrite of a legacy shader leaves
/// the backend numbering them in declaration order instead.
///
/// <para><b>The rule</b> (measured against <c>mgfxc</c> 3.8.4.1 <c>/Profile:DirectX_11</c>,
/// 2026-10-28, the 39 shapes of <c>tests/fixtures/golden/dx11-texture-slots/</c> plus the 27
/// legacy-sampler goldens), per compiled shader:</para>
/// <list type="number">
/// <item>A texture declared with an explicit <c>register(tN)</c> keeps <c>tN</c>, and every such
/// register is out of circulation whether or not the shader reads that texture. (One deliberate
/// difference: <c>mgfxc</c> does NOT bind a LEGACY sampler's texture at the register written on
/// the <c>texture2D T : register(tN)</c> its <c>sampler_state</c> names; fxc reserves <c>tN</c> for
/// the unread texture object and gives the sampler's own texture the next free slot. XNA, and KNI's
/// own tests, bind it at <c>tN</c>, and so does ShadowDusk: the author's register is honoured.)</item>
/// <item>Then every LEGACY sampler's texture the shader reads (<c>sampler2D A;</c>, a
/// <c>sampler_state</c> block, sampled with <c>tex2D</c> and its family), in the order the shader
/// first reads it: evaluation order, so an argument before the call that takes it and an inner
/// read before the dependent read around it. A legacy sampler's own <c>register(sN)</c> decides
/// its SAMPLER slot only, never its texture slot.</item>
/// <item>Then every MODERN texture (<c>Texture2D T;</c>) without a register, in declaration
/// order.</item>
/// </list>
/// <para>Each takes the lowest free slot. With no legacy texture in the shader the rule is plain
/// declaration order, which is what the backend already does, so nothing is touched.</para>
///
/// <para>The first compile's own instruction stream supplies the read order
/// (<see cref="DxbcResourceUseOrder"/>) and its RDEF the set of textures that survived dead-code
/// elimination (a read the compiler drops takes no slot, in fxc as here). When the plan differs
/// from what the backend chose, the caller recompiles with each texture's slot pinned by an
/// explicit <c>register(tN)</c> on its declaration.</para>
/// </summary>
internal static class DxTextureSlotOrder
{
    /// <summary>
    /// The slot each reflected texture of the first compile should take, or <c>null</c> when the
    /// backend's slots already follow the rule (or the shader has no legacy texture, or a texture
    /// array, which the rule does not model).
    /// </summary>
    /// <param name="textures">The first compile's reflected textures.</param>
    /// <param name="firstReadOrder">Texture registers in first-read order (<see cref="DxbcResourceUseOrder"/>).</param>
    /// <param name="legacyTextures">Names of the textures legacy samplers sample through.</param>
    /// <param name="explicitRegisters">Texture name -> explicit <c>register(tN)</c>, from the preprocessed view.</param>
    public static IReadOnlyDictionary<string, int>? Plan(
        IReadOnlyList<TextureReflection> textures,
        IReadOnlyList<int> firstReadOrder,
        IReadOnlySet<string> legacyTextures,
        IReadOnlyDictionary<string, int> explicitRegisters)
    {
        if (textures.Count == 0 || textures.Any(t => t.ArrayLength is not null))
            return null;
        if (!textures.Any(t => legacyTextures.Contains(t.Name)))
            return null;
        if (textures.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != textures.Count)
            return null;

        var plan = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<int>(explicitRegisters.Values);
        foreach (TextureReflection t in textures)
        {
            if (explicitRegisters.TryGetValue(t.Name, out int register))
                plan[t.Name] = register;
        }

        int ReadRank(TextureReflection t)
        {
            for (int i = 0; i < firstReadOrder.Count; i++)
            {
                if (firstReadOrder[i] == t.BindSlot)
                    return i;
            }
            return int.MaxValue;
        }

        IEnumerable<TextureReflection> legacy = textures
            .Where(t => legacyTextures.Contains(t.Name) && !plan.ContainsKey(t.Name))
            .OrderBy(ReadRank)
            .ThenBy(t => t.BindSlot);
        IEnumerable<TextureReflection> modern = textures
            .Where(t => !legacyTextures.Contains(t.Name) && !plan.ContainsKey(t.Name))
            .OrderBy(t => t.BindSlot);

        int next = 0;
        foreach (TextureReflection t in legacy.Concat(modern).ToList())
        {
            while (taken.Contains(next))
                next++;
            plan[t.Name] = next;
            taken.Add(next);
        }

        return textures.All(t => plan[t.Name] == t.BindSlot) ? null : plan;
    }

    /// <summary>
    /// <paramref name="text"/> with every declaration of each planned texture carrying its planned
    /// <c>register(tN)</c>, or <c>null</c> when a declaration cannot be pinned safely: none found
    /// (declared through a macro), one in a form other than <c>Type Name;</c> (a declarator list,
    /// an annotation), or one already pinned to a different register.
    /// </summary>
    public static string? Pin(string text, IReadOnlyDictionary<string, int> plan)
    {
        foreach ((string name, int slot) in plan.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string n = Regex.Escape(name);
            const string type = @"(?<![A-Za-z0-9_])[Tt]exture\w*(?:\s*<[^<>;]*>)?\s+";
            int any = Regex.Matches(text, type + n + "(?![A-Za-z0-9_])").Count;
            if (any == 0)
                return null;

            var registered = Regex.Matches(text, type + n + @"\s*:\s*register\s*\(\s*[tT](\d+)\s*\)\s*;");
            if (registered.Any(m => m.Groups[1].Value != slot.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                return null;

            var plain = new Regex("(" + type + n + @")\s*;");
            int plainCount = plain.Matches(text).Count;
            if (plainCount + registered.Count != any)
                return null;

            if (plainCount > 0)
                text = plain.Replace(text, m => $"{m.Groups[1].Value} : register(t{slot});");
        }
        return text;
    }

    /// <summary>Whether <paramref name="textures"/> sit exactly on <paramref name="plan"/>.</summary>
    public static bool Matches(IReadOnlyList<TextureReflection> textures, IReadOnlyDictionary<string, int> plan) =>
        textures.Count == plan.Count && textures.All(t => plan.TryGetValue(t.Name, out int slot) && slot == t.BindSlot);
}
