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
/// <para><b>Why the crash is a crash</b> (issue #145, now source-grounded in MonoGame
/// 3.8.5's <c>native/monogame/vulkan/MGG_Vulkan.cpp</c>): <c>MGVK_UpdateDescriptors</c>
/// recovers a texture slot as <c>int slot = binding - 32</c>. An auto-numbered image lands
/// at raw binding 0/1 (the shift only moves EXPLICIT register annotations), so <c>slot</c>
/// goes to <c>-32</c> and the runtime indexes <c>device-&gt;textures[stage][-32]</c>. The
/// separate <c>VK_DESCRIPTOR_TYPE_SAMPLER</c> half additionally falls into an unhandled
/// <c>assert(0)</c> branch, leaving an uninitialised <c>VkWriteDescriptorSet</c>.</para>
///
/// <para><b>Existing explicit registers are authoritative and are never renumbered.</b> A
/// declaration that already carries <c>register(tN)</c>/<c>register(sN)</c> is left
/// byte-identical; its index is RESERVED so an auto-assigned pair can never collide with it
/// (two descriptor-set-layout bindings at the same binding number is invalid regardless of
/// shape). Note <c>-fvk-t-shift</c> and <c>-fvk-s-shift</c> both add 32, so <c>t2</c> and
/// <c>s2</c> occupy the SAME raw binding — texture and sampler indices share ONE reservation
/// space, and an index is only shared deliberately, by the two halves of a pair.</para>
///
/// <para><b>Deliberately whole-file, not <c>#if VULKAN</c>-scoped.</b> A texture
/// declaration is often shared/unconditional across all targets (only its sampler differs
/// per <c>#if</c> branch, e.g. legacy <c>sampler2D</c> vs. modern <c>SamplerState</c>), so
/// scoping this rewrite to inside <c>#if VULKAN</c> spans misses it entirely and breaks
/// the pairing. Declarations belonging to a branch DXC will discard are rewritten too; that
/// is harmless (the branch never reaches codegen) and it is REQUIRED for the legacy path,
/// where the surviving branch is the one FxPreParser converted — see
/// <see cref="SynthesizedTextureSuffix"/>.</para>
///
/// <para>Runs on a Vulkan-private copy of the source, like <see cref="GlStructOutputColorRewriter"/>
/// does for GL — DirectX/OpenGL/FNA bytes are unaffected. A shader with no
/// <c>Texture2D</c>/<c>SamplerState</c> declarations, or where every declaration already
/// carries an explicit register, is a no-op (returns the input unchanged).</para>
/// </summary>
internal static class VulkanTextureSamplerBindingRewriter
{
    // A global declaration, with or without an existing register annotation. The "idx" group
    // is present only when the declaration already carries one — those are left byte-identical
    // and their index is reserved.
    //
    // EVERY texture dimensionality is matched, not just Texture2D: a TextureCube/Texture3D
    // left unpaired lands as a standalone SAMPLED_IMAGE at a low binding, which is the same
    // shape that access-violates (found by the corpus-wide structural gate on
    // examples/ExCubeSamplerHidef.fx and ExVolumeTextureHidef.fx, issue #145). The optional
    // template argument covers the "Texture2D<float4> Name : register(t0);" form MonoGame's
    // own Macros/Include layer emits.
    private static readonly Regex TextureDecl =
        new(@"\b(?<type>Texture(?:1D|2D|3D|Cube))(?<tmpl>\s*<[^>;{}]*>)?\s+(?<name>\w+)\s*(?::\s*register\s*\(\s*t(?<idx>\d+)\s*\)\s*)?;",
            RegexOptions.Compiled);

    // Both the modern "SamplerState S;" and the SM4/SM6 shorthand "sampler S;" (what
    // MonoGame's Include.fxh/Macros.fxh declare next to a Texture2D) — the latter is a real
    // sampler object at SM6, not the legacy FX9 sampler2D form FxPreParser converts earlier.
    private static readonly Regex SamplerDecl =
        new(@"\b(?<type>SamplerState|sampler)\s+(?<name>\w+)\s*(?::\s*register\s*\(\s*s(?<idx>\d+)\s*\)\s*)?;",
            RegexOptions.Compiled);

    // texture.Sample(sampler, ...) — the pairing signal. FxPreParser's legacy conversion
    // emits exactly this shape for a converted tex2D call, so legacy-source pairs are
    // discovered here too.
    private static readonly Regex SampleCall =
        new(@"(\w+)\s*\.\s*Sample\w*\s*\(\s*(\w+)\s*[,)]", RegexOptions.Compiled);

    /// <summary>
    /// The suffix FxPreParser's legacy-<c>sampler2D</c> conversion (<c>SynthTextureName</c>)
    /// gives the <c>Texture2D</c> it synthesizes for a bare <c>sampler S;</c>.
    ///
    /// <para>These declarations were once excluded from the rewrite outright. That was the
    /// root cause of issue #145's second failure mode: on the legacy path EVERY texture is
    /// synthesized, so excluding them left every legacy shader's pair un-co-located — image
    /// auto-numbered at raw binding 0/1, sampler shifted to 32/33 — which is precisely the
    /// separate-descriptor shape that access-violates in MonoGame's native draw path. They
    /// are now paired like any other declaration; the original concern (a synthesized
    /// declaration belonging to a branch this compile discards) is a non-issue because the
    /// index is shared with its own sampler and reservation prevents collisions.</para>
    /// </summary>
    private const string SynthesizedTextureSuffix = "_SDTexture";

    public static string Rewrite(string hlsl)
    {
        var textureNames = new List<string>();
        var samplerNames = new List<string>();

        // Indices already pinned by the source. Textures and samplers share one space (both
        // shifts are +32), so one reservation set covers both.
        var explicitIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var reserved = new HashSet<int>();

        foreach (Match m in TextureDecl.Matches(hlsl))
            Collect(m, textureNames, explicitIndex, reserved);
        foreach (Match m in SamplerDecl.Matches(hlsl))
            Collect(m, samplerNames, explicitIndex, reserved);

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
        // An explicit register on EITHER half fixes the pair's index; otherwise the pair
        // takes the lowest index nobody has reserved or been given.
        var registerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int next = 0;

        foreach (string tex in textureNames)
        {
            if (registerIndex.ContainsKey(tex))
                continue;

            pairedSampler.TryGetValue(tex, out string? samp);

            int index;
            if (explicitIndex.TryGetValue(tex, out int fixedTex))
                index = fixedTex;
            else if (samp is not null && explicitIndex.TryGetValue(samp, out int fixedSamp))
                index = fixedSamp;
            // The paired sampler may already be assigned because the SAME sampler name is
            // declared in two mutually-exclusive #if branches (e.g. a modern SamplerState
            // for Vulkan and FxPreParser's converted form for the legacy branch). Follow it
            // so BOTH branches stay co-located — only one of them survives the compile.
            else if (samp is not null && registerIndex.TryGetValue(samp, out int assignedSamp))
                index = assignedSamp;
            else
                index = NextFree(ref next, reserved, registerIndex);

            registerIndex[tex] = index;
            if (samp is not null && !registerIndex.ContainsKey(samp))
                registerIndex[samp] = explicitIndex.TryGetValue(samp, out int own) ? own : index;
        }

        foreach (string samp in samplerNames)
        {
            if (registerIndex.ContainsKey(samp))
                continue;

            registerIndex[samp] = explicitIndex.TryGetValue(samp, out int fixedSamp)
                ? fixedSamp
                : NextFree(ref next, reserved, registerIndex);
        }

        // Only declarations that DON'T already carry a register are rewritten; an
        // explicitly-registered declaration is returned byte-identical. The declared type
        // (and any template argument) is preserved verbatim — only the register clause is
        // added.
        string result = TextureDecl.Replace(hlsl, m =>
            !m.Groups["idx"].Success && registerIndex.TryGetValue(m.Groups["name"].Value, out int ti)
                ? $"{m.Groups["type"].Value}{m.Groups["tmpl"].Value} {m.Groups["name"].Value} : register(t{ti});"
                : m.Value);

        result = SamplerDecl.Replace(result, m =>
            !m.Groups["idx"].Success && registerIndex.TryGetValue(m.Groups["name"].Value, out int si)
                ? $"{m.Groups["type"].Value} {m.Groups["name"].Value} : register(s{si});"
                : m.Value);

        return result;
    }

    private static void Collect(
        Match m,
        List<string> names,
        Dictionary<string, int> explicitIndex,
        HashSet<int> reserved)
    {
        string name = m.Groups["name"].Value;
        if (!names.Contains(name))
            names.Add(name);

        if (!m.Groups["idx"].Success)
            return;

        int index = int.Parse(m.Groups["idx"].Value, System.Globalization.CultureInfo.InvariantCulture);
        reserved.Add(index);
        // First explicit annotation for a name wins (the same name can appear in two
        // mutually-exclusive #if branches; only one survives the compile).
        if (!explicitIndex.ContainsKey(name))
            explicitIndex[name] = index;
    }

    private static int NextFree(ref int next, HashSet<int> reserved, Dictionary<string, int> assigned)
    {
        while (reserved.Contains(next) || assigned.ContainsValue(next))
            next++;
        return next;
    }
}
