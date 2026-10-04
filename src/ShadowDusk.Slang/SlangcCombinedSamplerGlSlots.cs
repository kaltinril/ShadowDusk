#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;

namespace ShadowDusk.Slang;

/// <summary>
/// OpenGL: a combined sampler the author declared with a sampler register
/// (<c>Sampler2D X : register(sN)</c>) lands on texture unit N, exactly where <c>mgfxc</c>
/// puts the legacy combined <c>sampler2D X : register(sN)</c>.
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
/// author wrote keeps the split-pair rule: only the halves of a combined sampler are touched.</para>
/// </remarks>
internal static class SlangcCombinedSamplerGlSlots
{
    /// <summary>
    /// Removes the sampler register from the sampler half of each combined sampler in
    /// <paramref name="combinedSamplers"/> (texture names, as the author wrote them) and returns
    /// the unit each one pins.
    /// </summary>
    public static (string Hlsl, IReadOnlyDictionary<string, int> Slots) Pin(
        string hlsl, IReadOnlyCollection<string> combinedSamplers)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string texture in combinedSamplers)
        {
            // 'SamplerState X_sampler_0 : register(s2);' (a single sampler, not an array: an
            // array of combined samplers is refused on OpenGL, issue #356).
            var half = new Regex(
                $@"^(?<decl>[ \t]*SamplerState[ \t]+{Regex.Escape(texture)}_sampler_\d+)[ \t]*:[ \t]*register[ \t]*\([ \t]*s(?<slot>\d+)[ \t]*\)[ \t]*;",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            Match match = half.Match(hlsl);
            if (!match.Success)
                continue;
            slots[texture] = int.Parse(match.Groups["slot"].Value, CultureInfo.InvariantCulture);
            hlsl = half.Replace(hlsl, m => m.Groups["decl"].Value + ";");
        }
        return (hlsl, slots);
    }
}
