#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Collapses the per-element resource bindings a Shader Model 5 <c>RDEF</c> carries for an ARRAY
/// of textures or samplers into the one binding per array that Shader Model 4 reflection reports,
/// which is the view real <c>mgfxc</c> builds its DirectX_11 parameter table from (issue #339).
///
/// <para><b>Measured 2026-10-02</b> with d3dcompiler_47 (<c>/Gec /O3</c>, mgfxc's own flags) on
/// <c>Texture2D Tex[2] : register(t0)</c> sampled as <c>Tex[0]</c> and <c>Tex[1]</c>:</para>
/// <list type="bullet">
///   <item><c>ps_4_0</c> (and <c>ps_4_0_level_9_3</c>): ONE binding <c>Tex</c>, BindPoint 0,
///   BindCount 2. The BindPoint is the array's base register and the BindCount its declared size
///   whichever elements are used: <c>Tex[1]</c> alone still reflects <c>Tex</c> t0 count 2, and
///   <c>Tex[0] + Tex[3]</c> of a 4-array reflects <c>Tex</c> t0 count 4. An array declared at
///   <c>register(t1)</c> reflects <c>Tex</c> t1 count 2 after the texture at t0.</item>
///   <item><c>ps_5_0</c>: one binding PER ELEMENT, named with the index, each BindCount 1:
///   <c>Tex[0]</c> t0, <c>Tex[1]</c> t1 (and <c>Tex[1]</c> t1 alone when only element 1 is read).
///   vkd3d-shader's DXBC follows the same convention, and <c>D3DReflect</c> reports these records
///   as they are stored.</item>
/// </list>
///
/// <para><c>mgfxc</c> compiles a DirectX_11 shader at the model the author wrote (<c>ps_4_0</c> in
/// every MonoGame template), walks the texture bindings and makes one sampler record per binding
/// whose parameter is named by the binding, so a texture array is ONE effect parameter
/// (<c>Tex</c>) with ONE record at the array's base slot (3.8.4.1 and 3.8.5 alike). ShadowDusk
/// compiles DirectX 11 at Shader Model 5 (<c>Vkd3dShaderCompiler</c> / <c>D3DCompileRequest</c>),
/// so its RDEF has the per-element records; before this collapse they became parameters
/// <c>Tex[0]</c>, <c>Tex[1]</c>, ... that no author ever names (<c>effect.Parameters["Tex"]</c>
/// was null). The collapse reproduces the Shader Model 4 view from the Shader Model 5 records:
/// the base name, the base slot (<c>slot - index</c>, the same for every element), and the
/// element count as <c>ArrayLength</c>. Non-array bindings pass through untouched, so no byte of
/// an array-free effect moves. The DXIL reflector (DirectX 12) already reports an array this way
/// (<c>BindCount</c>), so the two DirectX extractors now agree.</para>
/// </summary>
public static class ResourceArrayBindings
{
    // `Name[index]`: the per-element spelling SM5 RDEF uses. An HLSL identifier cannot contain
    // '[', so a match is always an array element.
    private static readonly Regex Element = new(
        @"^(?<name>[^\[\]]+)\[(?<index>\d+)\]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="effect"/> with every run of per-element texture and sampler
    /// bindings collapsed into one binding per array; the same instance when it has none.
    /// </summary>
    /// <param name="effect">A reflected shader stage (typically <see cref="RdefReader"/> output).</param>
    public static ReflectedEffect Collapse(ReflectedEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        bool hasTextureElements = effect.Textures.Any(t => IsElement(t.Name));
        bool hasSamplerElements = effect.Samplers.Any(s => IsElement(s.Name));
        if (!hasTextureElements && !hasSamplerElements)
            return effect;

        return effect with
        {
            Textures = hasTextureElements ? CollapseTextures(effect.Textures) : effect.Textures,
            Samplers = hasSamplerElements ? CollapseSamplers(effect.Samplers) : effect.Samplers,
        };
    }

    /// <summary>True when <paramref name="name"/> is spelled as an array element (<c>Name[i]</c>).</summary>
    public static bool IsElement(string name) => Element.IsMatch(name);

    private static IReadOnlyList<TextureReflection> CollapseTextures(IReadOnlyList<TextureReflection> textures)
    {
        var groups = Group(textures, t => t.Name, t => t.BindSlot);
        var result = new List<TextureReflection>(textures.Count);
        foreach (var group in groups)
        {
            if (group.Elements is null)
            {
                result.Add(textures[group.FirstIndex]);
                continue;
            }

            TextureReflection first = textures[group.FirstIndex];
            result.Add(first with
            {
                Name        = group.Name,
                BindSlot    = group.BaseSlot,
                ArrayLength = group.Length,
            });
        }
        return result;
    }

    private static IReadOnlyList<SamplerReflection> CollapseSamplers(IReadOnlyList<SamplerReflection> samplers)
    {
        var groups = Group(samplers, s => s.Name, s => s.BindSlot);
        var result = new List<SamplerReflection>(samplers.Count);
        foreach (var group in groups)
        {
            if (group.Elements is null)
            {
                result.Add(samplers[group.FirstIndex]);
                continue;
            }

            SamplerReflection first = samplers[group.FirstIndex];
            result.Add(first with
            {
                Name        = group.Name,
                BindSlot    = group.BaseSlot,
                ArrayLength = group.Length,
            });
        }
        return result;
    }

    /// <summary>
    /// One entry per output binding, in first-occurrence order: a plain binding
    /// (<see cref="Elements"/> null) or an array (<see cref="Elements"/> = the (index, slot) pairs).
    /// </summary>
    private sealed class Run(string name, int firstIndex)
    {
        public string Name { get; } = name;
        public int FirstIndex { get; } = firstIndex;
        public List<(int Index, int Slot)>? Elements { get; set; }

        /// <summary>The array's base register: <c>slot - index</c>, which every element agrees on
        /// for fxc's and vkd3d's output; the minimum is taken so one odd record cannot push the
        /// base past a real element, and a base below zero (impossible for well-formed RDEF) falls
        /// back to the lowest element slot.</summary>
        public int BaseSlot
        {
            get
            {
                List<(int Index, int Slot)> elements = Elements!;
                int fromIndex = elements.Min(e => e.Slot - e.Index);
                return fromIndex >= 0 ? fromIndex : elements.Min(e => e.Slot);
            }
        }

        /// <summary>The element count: the highest index read plus one (the declared size when
        /// the last element is read; otherwise the smallest size that holds every element read).</summary>
        public int Length => Elements!.Max(e => e.Index) + 1;
    }

    private static List<Run> Group<T>(IReadOnlyList<T> bindings, Func<T, string> name, Func<T, int> slot)
    {
        var runs = new List<Run>();
        var arraysByName = new Dictionary<string, Run>(StringComparer.Ordinal);
        for (int i = 0; i < bindings.Count; i++)
        {
            Match m = Element.Match(name(bindings[i]));
            if (!m.Success)
            {
                runs.Add(new Run(name(bindings[i]), i));
                continue;
            }

            string baseName = m.Groups["name"].Value;
            int index = int.Parse(m.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (!arraysByName.TryGetValue(baseName, out Run? run))
            {
                run = new Run(baseName, i) { Elements = new List<(int, int)>() };
                arraysByName[baseName] = run;
                runs.Add(run);
            }
            run.Elements!.Add((index, slot(bindings[i])));
        }
        return runs;
    }
}
