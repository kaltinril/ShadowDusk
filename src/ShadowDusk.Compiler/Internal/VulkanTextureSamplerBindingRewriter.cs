#nullable enable

using System.Text.RegularExpressions;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// A <b>Vulkan-only</b> HLSL source rewrite: forces each texture+sampler pair used
/// together (<c>texture.Sample(sampler, ...)</c>) onto matching explicit HLSL registers
/// (<c>register(tN)</c> / <c>register(sN)</c>, same <c>N</c> for both halves of a pair).
///
/// <para><b>Why this exists.</b> Confirmed by a minimal repro against a real DesktopVK
/// runtime (2026-07-18, see <c>plan/DONE/PHASE-32-appendix/vulkan-mgfx-format-spec.md</c>): a
/// texture/sampler pair declared WITHOUT explicit registers gets auto-numbered by DXC to
/// DIFFERENT raw SPIR-V bindings, and MonoGame's native Vulkan draw path crashes
/// (<c>AccessViolationException</c>) on that shape. The SAME pair with matching explicit
/// registers gets shifted by <c>-fvk-t-shift</c>/<c>-fvk-s-shift</c> (<see cref="ShadowDusk.HLSL.Dxc.DxcFlagBuilder"/>)
/// onto the SAME raw binding, which — as ONE combined descriptor
/// (<see cref="ShadowDusk.Core.VulkanShaderCodeWrapper"/>) — draws correctly. This is the
/// only pattern proven to work; this rewrite makes ShadowDusk always produce it, rather
/// than leaving it to DXC's auto-numbering (which has no reason to co-locate a pair).</para>
///
/// <para><b>Deliberately whole-file, not <c>#if VULKAN</c>-scoped.</b> A texture
/// declaration is often shared/unconditional across all targets (only its sampler differs
/// per <c>#if</c> branch, e.g. legacy <c>sampler2D</c> vs. modern <c>SamplerState</c>), so
/// scoping this rewrite to inside <c>#if VULKAN</c> spans misses it entirely and breaks
/// the pairing. The one real hazard from scanning the whole file — FxPreParser's own
/// legacy-<c>sampler2D</c>-to-modern-syntax conversion, which can inject a bare, unregistered
/// <c>Texture2D &lt;name&gt;_SDTexture;</c> synthesized declaration into the file for an
/// unrelated (non-Vulkan) branch — is guarded against by name, not by scope (see
/// <see cref="SynthesizedTextureSuffix"/>).</para>
///
/// <para>Runs on a Vulkan-private copy of the source, like <see cref="GlStructOutputColorRewriter"/>
/// does for GL — DirectX/OpenGL/FNA bytes are unaffected. A shader with no
/// <c>Texture2D</c>/<c>SamplerState</c> declarations, or where every declaration already
/// carries an explicit register, is a no-op (returns the input unchanged).</para>
/// </summary>
internal static class VulkanTextureSamplerBindingRewriter
{
    // Matches a global declaration with NO existing register annotation (the semicolon
    // follows the identifier directly, give or take whitespace) — an already-registered
    // declaration has ": register(...)" in between and does not match, so it is left alone.
    private static readonly Regex TextureDecl = new(@"\bTexture2D\s+(\w+)\s*;", RegexOptions.Compiled);
    private static readonly Regex SamplerDecl = new(@"\bSamplerState\s+(\w+)\s*;", RegexOptions.Compiled);

    // texture.Sample(sampler, ...) — the pairing signal.
    private static readonly Regex SampleCall =
        new(@"(\w+)\s*\.\s*Sample\w*\s*\(\s*(\w+)\s*[,)]", RegexOptions.Compiled);

    // FxPreParser's own legacy-sampler2D-splitting (SynthTextureName) synthesizes a
    // texture name with this suffix for a bare `sampler X;` (no texture reference) in an
    // unrelated branch — never touch a name FxPreParser already owns.
    private const string SynthesizedTextureSuffix = "_SDTexture";

    public static string Rewrite(string hlsl)
    {
        var textureNames = TextureDecl.Matches(hlsl).Select(m => m.Groups[1].Value)
            .Where(n => !n.EndsWith(SynthesizedTextureSuffix, StringComparison.Ordinal)).ToList();
        var samplerNames = SamplerDecl.Matches(hlsl).Select(m => m.Groups[1].Value).ToList();

        if (textureNames.Count == 0 && samplerNames.Count == 0)
            return hlsl;

        // First texture.Sample(sampler, ...) pairing wins per texture name.
        var pairedSampler = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in SampleCall.Matches(hlsl))
        {
            string tex = m.Groups[1].Value, samp = m.Groups[2].Value;
            if (textureNames.Contains(tex) && samplerNames.Contains(samp) && !pairedSampler.ContainsKey(tex))
                pairedSampler[tex] = samp;
        }

        // Assign a shared index per pair (or a fresh one for an unpaired resource) in
        // declaration order, so the result is deterministic regardless of usage order.
        var registerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int next = 0;
        foreach (string tex in textureNames)
        {
            if (registerIndex.ContainsKey(tex))
                continue;
            int index = next++;
            registerIndex[tex] = index;
            if (pairedSampler.TryGetValue(tex, out string? samp) && !registerIndex.ContainsKey(samp))
                registerIndex[samp] = index;
        }
        foreach (string samp in samplerNames)
            if (!registerIndex.ContainsKey(samp))
                registerIndex[samp] = next++;

        string result = TextureDecl.Replace(hlsl, m =>
            registerIndex.TryGetValue(m.Groups[1].Value, out int ti)
                ? $"Texture2D {m.Groups[1].Value} : register(t{ti});"
                : m.Value);
        result = SamplerDecl.Replace(result, m =>
            registerIndex.TryGetValue(m.Groups[1].Value, out int si)
                ? $"SamplerState {m.Groups[1].Value} : register(s{si});"
                : m.Value);

        return result;
    }
}
