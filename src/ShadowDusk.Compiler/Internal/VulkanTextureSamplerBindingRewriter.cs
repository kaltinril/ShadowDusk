#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core;

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
/// <para><b>The TEXTURE's register decides the pair's index, as it decides the slot on
/// DirectX.</b> The index this rewrite gives a pair is the effect's slot for it: the Vulkan
/// slot is the SPIR-V binding minus 32 (<see cref="VulkanResourceSlots"/>), which is the
/// register. So a texture with <c>register(tN)</c> keeps N, and a texture without one takes the
/// lowest index no explicitly-registered texture holds, in declaration order, which is the
/// <c>t</c> register DirectX gives it (an unregistered texture beside <c>register(s2)</c> is
/// slot 0 on DirectX 11 and 12, where SpriteBatch binds its texture, so it is index 0 here
/// too). The sampler always moves onto its texture's index: one combined descriptor per pair,
/// so a sampler's own explicit register is overridden whenever it disagrees. (Before the slot
/// was taken from the binding, an unregistered texture INHERITED its sampler's register; the
/// record still said slot 0 but the descriptor read slot 2, and neither matched DirectX.)
/// Indices that must stay free: every explicit texture register, and the explicit register of
/// a sampler no texture is sampled through but that the shader still REFERENCES (it keeps its
/// own binding). A sampler named nowhere but its own declaration is dead code DXC removes and
/// reserves nothing. So an unregistered texture's index equals its DirectX t register except in
/// one case: a live, unpaired sampler holds that number (DirectX keeps t and s registers apart,
/// while Vulkan's bindings are one space, so the texture moves past it). Two
/// descriptor-set-layout bindings at the same binding number is invalid regardless of shape,
/// so an index is only shared deliberately, by the two halves of a pair. Note
/// <c>-fvk-t-shift</c> and <c>-fvk-s-shift</c> both add 32, so <c>t2</c> and <c>s2</c> occupy
/// the SAME raw binding: texture and sampler indices share ONE space.</para>
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
/// does for GL — DirectX/OpenGL/FNA bytes are unaffected. A shader with no texture/sampler
/// declarations is a no-op, as is one whose declarations already carry explicit registers
/// that agree with the assignment this rewrite computes (the overwhelmingly common case:
/// every corpus fixture is byte-identical through it).</para>
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
    // The Array/MS suffixes matter: "Texture2DArray" is a real declaration in MonoGame's own
    // TextureArrayEffect.fx, and without them the `\s+` after the type simply fails to match,
    // so the declaration is invisible — not paired, and its explicit register never reserved.
    private static readonly Regex TextureDecl =
        new(@"\b(?<type>Texture(?:1D|2D|3D|Cube)(?:Array)?(?:MS(?:Array)?)?)(?<tmpl>\s*<[^>;{}]*>)?\s+(?<name>\w+)\s*(?::\s*register\s*\(\s*t(?<idx>\d+)\s*\)\s*)?;",
            RegexOptions.Compiled);

    // Both the modern "SamplerState S;" and the SM4/SM6 shorthand "sampler S;" (what
    // MonoGame's Include.fxh/Macros.fxh declare next to a Texture2D) — the latter is a real
    // sampler object at SM6, not the legacy FX9 sampler2D form FxPreParser converts earlier.
    // SamplerComparisonState is included: it is a real sampler object competing for the same
    // s-register space (MonoGame's CustomSpriteBatchEffectComparisonSampler.fx declares one),
    // so leaving it out both skipped its pairing and let its register go unreserved.
    private static readonly Regex SamplerDecl =
        new(@"\b(?<type>SamplerComparisonState|SamplerState|sampler)\s+(?<name>\w+)\s*(?::\s*register\s*\(\s*s(?<idx>\d+)\s*\)\s*)?;",
            RegexOptions.Compiled);

    // texture.Sample(sampler, ...) — the pairing signal. FxPreParser's legacy conversion
    // emits exactly this shape for a converted tex2D call, so legacy-source pairs are
    // discovered here too.
    // Gather* is included alongside Sample*: Tex.Gather(Samp, uv) / GatherRed / GatherCmp all
    // take a sampler, and missing them left the pair un-co-located at separate bindings.
    private static readonly Regex SampleCall =
        new(@"(\w+)\s*\.\s*(?:Sample|Gather)\w*\s*\(\s*(\w+)\s*[,)]", RegexOptions.Compiled);

    // Preprocessor conditionals, tracked so the shared-sampler diagnostic below can tell
    // "two textures + one sampler in ONE live code path" (broken on Vulkan, SD0028) apart
    // from "the same sampler re-paired across mutually-exclusive #if branches" (legal —
    // only one branch survives the compile).
    private static readonly Regex ConditionalDirective =
        new(@"^[ \t]*#[ \t]*(?<d>ifdef|ifndef|if|elif|else|endif)\b",
            RegexOptions.Compiled | RegexOptions.Multiline);

    // #line N ["file"] — the preprocessor plants these (macro prelude + include flattening),
    // so a diagnostic offset in the flattened text can be mapped back to the author's file/line.
    private static readonly Regex LineDirective =
        new(@"^[ \t]*#[ \t]*line[ \t]+(?<n>\d+)(?:[ \t]+""(?<f>[^""]*)"")?", RegexOptions.Compiled);

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

    // `register(t1, space0)`: the register clause of a texture, sampler or constant buffer with an
    // explicit register SPACE. A space is a Vulkan descriptor set, and MonoGame's DesktopVK has
    // exactly one per stage (the vertex shader's set 0, the pixel shader's set 1, the
    // -auto-binding-space DxcFlagBuilder passes), so an explicit space can only put a resource in
    // a set the runtime never binds.
    private static readonly Regex RegisterSpace =
        new(@"\bregister\s*\(\s*(?<reg>[tsb]\d+)\s*,\s*space\d+\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// Drops the <c>, spaceN</c> from every texture, sampler and constant-buffer register clause,
    /// so DXC puts the resource in the stage's own descriptor set. <c>mgfxc</c> 3.8.5 keeps the
    /// space: <c>register(t1, space0)</c> in a pixel shader lands in set 0, and both its build and
    /// ShadowDusk's earlier one loaded into real DesktopVK and drew (0,0,0,0) (measured
    /// 2026-10-28). Returns the text unchanged when no clause names a space.
    /// </summary>
    internal static string StripRegisterSpaces(string hlsl)
    {
        string masked = ResourceArrayDiagnostics.MaskCommentsAndStrings(hlsl);
        MatchCollection clauses = RegisterSpace.Matches(masked);
        if (clauses.Count == 0)
            return hlsl;

        var result = new System.Text.StringBuilder(hlsl.Length);
        int copied = 0;
        foreach (Match m in clauses)
        {
            result.Append(hlsl, copied, m.Index - copied).Append("register(").Append(m.Groups["reg"].Value).Append(')');
            copied = m.Index + m.Length;
        }
        result.Append(hlsl, copied, hlsl.Length - copied);
        return result.ToString();
    }

    public static string Rewrite(string hlsl)
    {
        hlsl = StripRegisterSpaces(hlsl);

        var textureNames = new List<string>();
        var samplerNames = new List<string>();

        // Indices already pinned by the source. Textures and samplers share one space (both
        // shifts are +32); which of them must stay free is decided once the pairs are known.
        var explicitIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var explicitDecls = new List<(string Name, int Index)>();

        // Every scan below reads a copy with comments and string literals blanked (offsets and
        // newlines preserved), so a declaration or a Sample call that only appears in a comment
        // is not a resource. The preprocessed text keeps the author's comments, and a header
        // such as "rewrites `texture T;` -> `Texture2D T;`" used to be collected as a texture T
        // that took index 0 and pushed the shader's real pair to t1 (examples/
        // ExLegacyTextureDiscard.fx: its sprite sampler `s0` then read slot 1, not the slot 0
        // SpriteBatch binds).
        string masked = ResourceArrayDiagnostics.MaskCommentsAndStrings(hlsl);
        MatchCollection textureDecls = TextureDecl.Matches(masked);
        MatchCollection samplerDecls = SamplerDecl.Matches(masked);

        foreach (Match m in textureDecls)
            Collect(m, textureNames, explicitIndex, explicitDecls);
        foreach (Match m in samplerDecls)
            Collect(m, samplerNames, explicitIndex, explicitDecls);

        if (textureNames.Count == 0 && samplerNames.Count == 0)
            return hlsl;

        // First texture.Sample(sampler, ...) pairing wins per texture name. Alongside the
        // pairing, every call is checked against the earlier calls on the same sampler:
        // a SECOND texture on one sampler in the same live code path would co-locate both
        // textures on one combined descriptor, leaving the later one unbound at draw
        // (bug-hunt 2026-07-27 M5) — fail loudly (SD0028) instead. Calls in mutually
        // exclusive #if branches are exempt: only one branch survives the compile, which
        // is exactly the legal cross-branch re-pairing shape the assignment below supports.
        var pairedSampler = new Dictionary<string, string>(StringComparer.Ordinal);
        var callsBySampler = new Dictionary<string, List<(string Texture, int Offset, (int Group, int Branch)[] Branches)>>(StringComparer.Ordinal);
        MatchCollection directives = ConditionalDirective.Matches(masked);
        var branchStack = new List<(int Group, int Branch)>();
        int nextDirective = 0, nextGroup = 0;

        foreach (Match m in SampleCall.Matches(masked))
        {
            // Advance the conditional-branch cursor to this call's position.
            while (nextDirective < directives.Count && directives[nextDirective].Index < m.Index)
                ApplyDirective(directives[nextDirective++].Groups["d"].Value, branchStack, ref nextGroup);

            string tex = m.Groups[1].Value, samp = m.Groups[2].Value;
            if (!textureNames.Contains(tex) || !samplerNames.Contains(samp))
                continue;

            if (!pairedSampler.ContainsKey(tex))
                pairedSampler[tex] = samp;

            (int, int)[] branches = branchStack.ToArray();
            if (!callsBySampler.TryGetValue(samp, out var calls))
                callsBySampler[samp] = calls = new List<(string, int, (int, int)[])>();

            foreach (var (earlierTex, _, earlierBranches) in calls)
            {
                if (earlierTex != tex && !MutuallyExclusive(earlierBranches, branches))
                    ThrowSharedSampler(hlsl, samp, earlierTex, tex, m.Index);
            }

            calls.Add((tex, m.Index, branches));
        }

        // Indices an auto-assigned texture must not take: every explicit TEXTURE register, and
        // the explicit register of a sampler no texture is sampled through (it keeps its own
        // binding). A PAIRED sampler's register reserves nothing: the sampler moves onto its
        // texture's index, so its own number never reaches a binding. (Reserving it used to
        // push an unregistered texture off the t-register DirectX gives it.)
        // An unpaired sampler only needs its binding kept free when the shader REFERENCES it
        // (passed to a helper, say): one named nowhere but in its declaration is dead code that
        // DXC removes, and reserving its register would push a texture off the t-register
        // DirectX gives it (`Texture2D A; SamplerState S : register(s1);` beside an unused
        // `SamplerState U : register(s0)` is slot 0 on DirectX 11 and 12, where SpriteBatch binds).
        var pairedSamplers = new HashSet<string>(pairedSampler.Values, StringComparer.Ordinal);
        var deadSamplers = new HashSet<string>(StringComparer.Ordinal);
        foreach (string samp in samplerNames)
        {
            if (pairedSamplers.Contains(samp))
                continue;
            int declarations = samplerDecls.Count(d => d.Groups["name"].Value == samp);
            int mentions = Regex.Matches(masked, $@"\b{Regex.Escape(samp)}\b").Count;
            if (mentions <= declarations)
                deadSamplers.Add(samp);
        }

        var reserved = new HashSet<int>();
        foreach (var (name, index) in explicitDecls)
            if (textureNames.Contains(name) || (!pairedSamplers.Contains(name) && !deadSamplers.Contains(name)))
                reserved.Add(index);

        // Assign a shared index per pair (or a fresh one for an unpaired resource) in
        // declaration order, so the result is deterministic regardless of usage order.
        // The TEXTURE's explicit register fixes the pair's index; an unregistered texture takes
        // the lowest index nobody has reserved or been given, and its sampler follows it.
        var registerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        // index -> the sampler the texture holding it is paired with (null if unpaired).
        // Two textures may share an index ONLY when they name the same sampler: that is the
        // same-sampler-in-two-#if-branches case below, where just one branch survives the
        // compile. Two textures paired to DIFFERENT samplers on one binding is an invalid
        // descriptor-set layout. (Two textures sharing one sampler within a SINGLE branch —
        // the shape that would silently co-locate and leave the second texture unbound —
        // was rejected above with SD0028, so it can never reach this assignment.)
        var indexPairSampler = new Dictionary<int, string?>();
        int next = 0;

        // Textures whose register the SOURCE pins are assigned FIRST. Their index is
        // authoritative among textures, so a pair that INHERITS an index must be the one that
        // moves. Walking plain declaration order made the collision check order-dependent: if
        // the inheriting pair came first it took the index, and the explicitly-registered
        // texture was then dropped on top of it (the check below only guards the inheriting
        // path). Declaration order is preserved within each group, so output stays deterministic.
        var texturesExplicitFirst = new List<string>(textureNames.Count);
        foreach (string tex in textureNames)
            if (explicitIndex.ContainsKey(tex))
                texturesExplicitFirst.Add(tex);
        foreach (string tex in textureNames)
            if (!explicitIndex.ContainsKey(tex))
                texturesExplicitFirst.Add(tex);

        foreach (string tex in texturesExplicitFirst)
        {
            if (registerIndex.ContainsKey(tex))
                continue;

            pairedSampler.TryGetValue(tex, out string? samp);

            int index;
            bool inherited = false;
            if (explicitIndex.TryGetValue(tex, out int fixedTex))
                index = fixedTex;
            // A sampler's own explicit register is NOT inherited: the slot is the texture's,
            // as on DirectX (see the class remarks).
            // The paired sampler may already be assigned because the SAME sampler name is
            // declared in two mutually-exclusive #if branches (e.g. a modern SamplerState
            // for Vulkan and FxPreParser's converted form for the legacy branch). Follow it
            // so BOTH branches stay co-located — only one of them survives the compile.
            else if (samp is not null && registerIndex.TryGetValue(samp, out int assignedSamp))
                (index, inherited) = (assignedSamp, true);
            else
                index = NextFree(ref next, reserved, registerIndex);

            // An inherited index (the cross-branch case above) bypasses `reserved`, so it can
            // collide with a DIFFERENT pair's index, which would put two textures on one
            // binding: the invalid layout that access-violates in MonoGame's descriptor
            // writer. Pair co-location is the invariant that must hold, so move this pair to a
            // free index instead.
            if (inherited
                && indexPairSampler.TryGetValue(index, out string? owner)
                && !(owner is not null && samp is not null && owner == samp))
            {
                index = NextFree(ref next, reserved, registerIndex);
            }

            registerIndex[tex] = index;
            indexPairSampler[index] = samp;

            // The sampler always follows its texture. Honouring the sampler's OWN explicit
            // register here would re-split a pair we just moved, and would also silently
            // split a pair whose two halves carry disagreeing explicit registers — the exact
            // separate-descriptor shape this rewriter exists to prevent.
            if (samp is not null && !registerIndex.ContainsKey(samp))
                registerIndex[samp] = index;
        }

        foreach (string samp in samplerNames)
        {
            if (registerIndex.ContainsKey(samp))
                continue;

            // A dead sampler's register was not reserved, so a texture may now hold it; move the
            // dead declaration to a free index rather than leave two declarations on one register
            // (it never reaches the SPIR-V either way).
            registerIndex[samp] = explicitIndex.TryGetValue(samp, out int fixedSamp)
                                  && !(deadSamplers.Contains(samp) && registerIndex.ContainsValue(fixedSamp))
                ? fixedSamp
                : NextFree(ref next, reserved, registerIndex);
        }

        // A declaration is rewritten when it carries no register, or when co-locating its
        // pair required a different index than the one it declared. An explicit register
        // that already agrees with the assignment is returned byte-identical.
        // Overriding a disagreeing explicit register is deliberate: an invalid descriptor
        // layout access-violates at draw, so pair co-location outranks a SAMPLER's register
        // (the texture's register is the slot and is kept; see the class remarks).
        static bool NeedsRewrite(Match m, Dictionary<string, int> assigned, out int index) =>
            assigned.TryGetValue(m.Groups["name"].Value, out index)
            && (!m.Groups["idx"].Success
                || int.Parse(m.Groups["idx"].Value, System.Globalization.CultureInfo.InvariantCulture) != index);

        // The matches were found in the masked copy, so each is spliced into the ORIGINAL text at
        // the same offset (masking keeps every offset, and a match lies outside any comment or
        // string, where the two texts are identical). Texture and sampler declarations never
        // overlap (different keywords), so one ordered pass places both.
        var edits = new List<(int Index, int Length, string Text)>();
        foreach (Match m in textureDecls)
            if (NeedsRewrite(m, registerIndex, out int ti))
                edits.Add((m.Index, m.Length, $"{m.Groups["type"].Value}{m.Groups["tmpl"].Value} {m.Groups["name"].Value} : register(t{ti});"));
        foreach (Match m in samplerDecls)
            if (NeedsRewrite(m, registerIndex, out int si))
                edits.Add((m.Index, m.Length, $"{m.Groups["type"].Value} {m.Groups["name"].Value} : register(s{si});"));

        if (edits.Count == 0)
            return hlsl;

        edits.Sort((a, b) => a.Index.CompareTo(b.Index));
        var result = new System.Text.StringBuilder(hlsl.Length + edits.Count * 16);
        int copied = 0;
        foreach (var (index, length, text) in edits)
        {
            result.Append(hlsl, copied, index - copied).Append(text);
            copied = index + length;
        }
        result.Append(hlsl, copied, hlsl.Length - copied);
        return result.ToString();
    }

    private static void Collect(
        Match m,
        List<string> names,
        Dictionary<string, int> explicitIndex,
        List<(string Name, int Index)> explicitDecls)
    {
        string name = m.Groups["name"].Value;
        if (!names.Contains(name))
            names.Add(name);

        if (!m.Groups["idx"].Success)
            return;

        int index = int.Parse(m.Groups["idx"].Value, System.Globalization.CultureInfo.InvariantCulture);
        explicitDecls.Add((name, index));
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

    // ---- Shared-sampler detection (bug-hunt 2026-07-27 M5) ---------------------------

    /// <summary>
    /// Advances the conditional-branch stack over one preprocessor directive. Each
    /// <c>#if/#ifdef/#ifndef</c> opens a new group at branch 0; <c>#elif/#else</c> moves the
    /// innermost group to its next branch; <c>#endif</c> closes the group.
    /// </summary>
    private static void ApplyDirective(string directive, List<(int Group, int Branch)> stack, ref int nextGroup)
    {
        switch (directive)
        {
            case "if" or "ifdef" or "ifndef":
                stack.Add((nextGroup++, 0));
                break;
            case "elif" or "else" when stack.Count > 0:
                stack[^1] = (stack[^1].Group, stack[^1].Branch + 1);
                break;
            case "endif" when stack.Count > 0:
                stack.RemoveAt(stack.Count - 1);
                break;
        }
    }

    /// <summary>
    /// Whether two code positions can never both survive one compile: true only when, at
    /// the first point their conditional paths diverge, they sit in DIFFERENT branches of
    /// the SAME <c>#if</c> group. Sibling <c>#if</c> groups (or one position nested inside
    /// the other's path) can both be live, so they are NOT exclusive.
    /// </summary>
    private static bool MutuallyExclusive((int Group, int Branch)[] a, (int Group, int Branch)[] b)
    {
        int common = Math.Min(a.Length, b.Length);
        for (int i = 0; i < common; i++)
        {
            if (a[i].Group != b[i].Group)
                return false;
            if (a[i].Branch != b[i].Branch)
                return true;
        }
        return false;
    }

    private static void ThrowSharedSampler(string hlsl, string sampler, string firstTexture, string secondTexture, int offset)
    {
        var (file, line, column) = Locate(hlsl, offset);
        throw new VulkanSamplerSharingException(new ShaderError(
            File:    file,
            Line:    line,
            Column:  column,
            Code:    "SD0028",
            Message: $"Vulkan: textures '{firstTexture}' and '{secondTexture}' are both sampled through the single "
                   + $"sampler '{sampler}' in the same code path. Vulkan's combined image-sampler binding model "
                   + $"needs a distinct descriptor per texture, so both textures would land on one binding, "
                   + $"leaving '{secondTexture}' unbound at draw (a silent wrong render on DesktopVK). Give each "
                   + $"texture its own SamplerState (e.g. 'SamplerState {secondTexture}Sampler;') and sample each "
                   + $"texture through its own sampler."));
    }

    /// <summary>
    /// Maps a character offset in the (possibly preprocessor-flattened) source to a
    /// file/line/column, honouring the <c>#line N "file"</c> markers the preprocessor
    /// plants after its macro prelude and around flattened includes. With no markers the
    /// file is empty and the line/column are physical (the caller fills the file in).
    /// </summary>
    internal static (string File, int Line, int Column) Locate(string text, int offset)
    {
        string file = "";
        int line = 1;
        int pos = 0;
        while (true)
        {
            int newline = text.IndexOf('\n', pos);
            int end = newline < 0 ? text.Length : newline;
            if (offset <= end)
                return (file, line, offset - pos + 1);

            Match m = LineDirective.Match(text, pos, end - pos);
            if (m.Success)
            {
                line = int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (m.Groups["f"].Success)
                    file = m.Groups["f"].Value;
            }
            else
            {
                line++;
            }

            pos = newline + 1;
        }
    }
}
