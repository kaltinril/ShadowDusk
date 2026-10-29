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
/// while Vulkan's bindings are one space, so the texture moves past it). "Live" is textual: the
/// name appears somewhere besides its declarations, so a sampler named only in a function that
/// is never called, or by a same-named struct member, also counts as live. That errs toward
/// reserving, the safe direction (a texture steps past a register DXC then frees; it can never
/// land on a binding a live sampler keeps). Pairs are also followed through helper functions
/// (see <see cref="HelperPairs"/>). Two
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
        new(@"\b(?<type>Texture(?:1D|2D|3D|Cube)(?:Array)?(?:MS(?:Array)?)?)(?<tmpl>\s*<[^>;{}]*>)?\s+(?<name>\w+)\s*(?::\s*register\s*\(\s*[tT](?<idx>\d+)\s*\)\s*)?;",
            RegexOptions.Compiled);

    // Both the modern "SamplerState S;" and the SM4/SM6 shorthand "sampler S;" (what
    // MonoGame's Include.fxh/Macros.fxh declare next to a Texture2D) — the latter is a real
    // sampler object at SM6, not the legacy FX9 sampler2D form FxPreParser converts earlier.
    // SamplerComparisonState is included: it is a real sampler object competing for the same
    // s-register space (MonoGame's CustomSpriteBatchEffectComparisonSampler.fx declares one),
    // so leaving it out both skipped its pairing and let its register go unreserved.
    private static readonly Regex SamplerDecl =
        new(@"\b(?<type>SamplerComparisonState|SamplerState|sampler)\s+(?<name>\w+)\s*(?::\s*register\s*\(\s*[sS](?<idx>\d+)\s*\)\s*)?;",
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

    /// <summary>
    /// The suffix of a texture ALIAS: a second declaration of one texture, synthesized when it is
    /// sampled through a second sampler (<c>Tex_SDVkAlias1</c>). The pipeline drops the alias's
    /// own effect parameter and binds its record to the original's.
    /// </summary>
    internal const string AliasSuffix = "_SDVkAlias";

    // `register(t1, space0)`: the register clause of a texture, sampler or constant buffer with an
    // explicit register SPACE. A space is a Vulkan descriptor set, and MonoGame's DesktopVK has
    // exactly one per stage (the vertex shader's set 0, the pixel shader's set 1, the
    // -auto-binding-space DxcFlagBuilder passes), so an explicit space can only put a resource in
    // a set the runtime never binds.
    //
    // Uppercase register letters too: DXC's SPIR-V backend rejects `register(T1)` with an
    // internal compiler error ("unknown register type found", X0000, no line) where its DXIL
    // backend (DirectX 12) and vkd3d (DirectX 11) accept it as HLSL allows, so on Vulkan the letter
    // is written lowercase. `u` (a UAV) is included so its space is dropped like any other; the
    // UAV itself is then refused by name (SD0233), MonoGame's Vulkan format having no binding for it.
    private static readonly Regex RegisterSpace =
        new(@"\bregister\s*\(\s*(?<l>[tTsSbBuU])(?<n>\d+)\s*(?<space>,\s*[sS][pP][aA][cC][eE]\d+\s*)?\)", RegexOptions.Compiled);

    /// <summary>
    /// Drops the <c>, spaceN</c> from every texture, sampler, constant-buffer and UAV register
    /// clause, so DXC puts the resource in the stage's own descriptor set, and lowercases an
    /// uppercase register letter (see <see cref="RegisterSpace"/>). <c>mgfxc</c> 3.8.5 keeps the
    /// space: <c>register(t1, space0)</c> in a pixel shader lands in set 0, and both its build and
    /// ShadowDusk's earlier one loaded into real DesktopVK and drew (0,0,0,0) (measured
    /// 2026-10-28). Returns the text unchanged when no clause names a space or uses an uppercase letter.
    /// </summary>
    internal static string StripRegisterSpaces(string hlsl)
    {
        string masked = ResourceArrayDiagnostics.MaskCommentsAndStrings(hlsl);
        var clauses = RegisterSpace.Matches(masked)
            .Where(m => m.Groups["space"].Success || char.IsUpper(m.Groups["l"].Value[0]))
            .ToList();
        if (clauses.Count == 0)
            return hlsl;

        var result = new System.Text.StringBuilder(hlsl.Length);
        int copied = 0;
        foreach (Match m in clauses)
        {
            result.Append(hlsl, copied, m.Index - copied)
                  .Append("register(").Append(char.ToLowerInvariant(m.Groups["l"].Value[0])).Append(m.Groups["n"].Value).Append(')');
            copied = m.Index + m.Length;
        }
        result.Append(hlsl, copied, hlsl.Length - copied);
        return result.ToString();
    }

    /// <summary>A texture alias the rewrite declared: <paramref name="Texture"/> read through
    /// <paramref name="Sampler"/> by the shader, at <paramref name="Offset"/> in the returned text.</summary>
    internal sealed record Alias(string Texture, string Sampler, string Name, int Offset);

    /// <summary>The whole-file rewrite: every function counts as reachable and every line as live.</summary>
    public static string Rewrite(string hlsl) => Rewrite(hlsl, entryPoint: null, skippedLines: null, out _);

    /// <summary>
    /// The rewrite for ONE compiled shader: only the <c>Sample</c> calls and helper calls in
    /// functions reachable from <paramref name="entryPoint"/> pair a texture with a sampler, and a
    /// sampler that shader never names is free to move. Each compiled shader is one Vulkan
    /// descriptor layout, so a texture read through sampler A in one pass and B in another needs
    /// no alias. <see langword="null"/> (or an entry point not defined in the text) is the
    /// whole file. <paramref name="aliases"/> lists the aliases declared (see <see cref="AliasSuffix"/>).
    /// </summary>
    /// <param name="hlsl">The compile's flattened source.</param>
    /// <param name="entryPoint">The shader's entry point, or <see langword="null"/> for the whole file.</param>
    /// <param name="skippedLines">The 1-based lines of <paramref name="hlsl"/> an untaken conditional
    /// group drops (<c>FxMacroPreprocessor.SkippedLines</c>), or <see langword="null"/> when that is not
    /// known. A <c>Sample</c> call, a helper call or a sampler mention on such a line is not code this
    /// compile can run, so it pairs nothing and keeps no sampler live: a call in <c>#if 0</c> no longer
    /// gives a texture its sampler (it used to win the pairing as the first call in the file, leaving
    /// the live pairing on a second, aliased descriptor away from slot 0).</param>
    /// <param name="aliases">The aliases the rewrite declared.</param>
    internal static string Rewrite(string hlsl, string? entryPoint, IReadOnlySet<int>? skippedLines, out IReadOnlyList<Alias> aliases)
    {
        aliases = Array.Empty<Alias>();
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

        // What this shader can reach: a Sample or helper call on a line an untaken #if drops, or
        // inside a function the entry point never calls, is dead code for it (its first textual
        // pairing must not decide the pair). StripRegisterSpaces keeps every line where it was, so
        // the skipped lines still number this text.
        Func<int, bool> liveLine = LiveLines(masked, skippedLines);
        Func<int, bool> reachable = ReachableScope(masked, entryPoint, liveLine);
        Func<int, bool> inScope = offset => liveLine(offset) && reachable(offset);

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

        // Pairing events in source order: every direct `Tex.Sample(Samp, ...)` on globals, plus
        // the pairs a helper function makes when it is called with global resources (see
        // HelperPairs), each at its call site so the #if branch tracking applies to it too.
        // TexAt is the offset of the texture identifier of a DIRECT call (-1 for a helper pair):
        // the place a call can be pointed at an alias.
        var events = new List<(string Tex, string Samp, int Offset, int TexAt)>();
        foreach (Match m in SampleCall.Matches(masked))
            if (inScope(m.Index))
                events.Add((m.Groups[1].Value, m.Groups[2].Value, m.Index, m.Groups[1].Index));
        events.AddRange(HelperPairs(masked, textureNames, samplerNames, liveLine)
            .Where(p => inScope(p.Offset))
            .Select(p => (p.Tex, p.Samp, p.Offset, -1)));
        events.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // One texture sampled DIRECTLY through a second, different sampler (the linear + point
        // idiom) needs a second combined descriptor holding the same image. Each extra pairing
        // gets an ALIAS declaration of the texture (`Tex_SDVkAlias1`, on its own register, written
        // next to the original) and its calls are pointed at the alias; the pipeline folds the
        // alias back into the original's effect parameter, so setting `Tex` binds both slots. A
        // second sampler reached only through a helper is not aliased (the helper's body would
        // have to change): it ends as SD0232.
        var aliasOf = new Dictionary<(string Tex, string Samp), string>();
        var aliasesByTexture = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var aliasCallEdits = new List<(int Index, int Length, string Text)>();
        var declaredAliases = new List<(string Texture, string Sampler, string Name, int TexAt)>();
        // The #if branches of every call that made a texture's PRIMARY pairing: a second sampler
        // is aliased only when its call can be live together with one of them. A call mutually
        // exclusive with all of them is the other branch of an #if: it is left alone, and the
        // compile resolves it from the preprocessed source (CompilationPipeline's retry), where only
        // the live branch exists. Aliasing it would leave the alias the only live texture.
        var primaryBranches = new Dictionary<string, List<(int Group, int Branch)[]>>(StringComparer.Ordinal);

        foreach (var (eventTex, samp, offset, texAt) in events)
        {
            // Advance the conditional-branch cursor to this call's position.
            while (nextDirective < directives.Count && directives[nextDirective].Index < offset)
                ApplyDirective(directives[nextDirective++].Groups["d"].Value, branchStack, ref nextGroup);

            string tex = eventTex;
            if (!textureNames.Contains(tex) || !samplerNames.Contains(samp))
                continue;

            (int, int)[] branches = branchStack.ToArray();
            if (pairedSampler.TryGetValue(tex, out string? primary) && primary == samp)
                primaryBranches[tex].Add(branches);

            if (primary is not null && primary != samp && texAt >= 0
                && !tex.Contains(AliasSuffix, StringComparison.Ordinal)
                && primaryBranches[tex].Any(p => !MutuallyExclusive(p, branches)))
            {
                if (!aliasOf.TryGetValue((tex, samp), out string? alias))
                {
                    if (!aliasesByTexture.TryGetValue(tex, out var list))
                        aliasesByTexture[tex] = list = new List<string>();
                    alias = $"{tex}{AliasSuffix}{list.Count + 1}";
                    list.Add(alias);
                    aliasOf[(tex, samp)] = alias;
                    textureNames.Add(alias);
                    pairedSampler[alias] = samp;
                    declaredAliases.Add((tex, samp, alias, texAt));
                }
                aliasCallEdits.Add((texAt, tex.Length, alias));
                tex = alias;
            }

            if (!pairedSampler.ContainsKey(tex))
            {
                pairedSampler[tex] = samp;
                primaryBranches[tex] = [branches];
            }

            if (!callsBySampler.TryGetValue(samp, out var calls))
                callsBySampler[samp] = calls = new List<(string, int, (int, int)[])>();

            foreach (var (earlierTex, _, earlierBranches) in calls)
            {
                if (earlierTex != tex && !MutuallyExclusive(earlierBranches, branches))
                    ThrowSharedSampler(hlsl, samp, earlierTex, tex, offset);
            }

            calls.Add((tex, offset, branches));
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
            if (!pairedSamplers.Contains(samp) && !Referenced(samp, samplerDecls))
                deadSamplers.Add(samp);

        // Likewise an unregistered texture this shader never references (one FxPreParser declared for a
        // legacy sampler whose calls sit in a skipped branch, say) is dead code: it is numbered after
        // every other texture, so it never moves a live one off the t-register DirectX gives it.
        // (Before the skipped lines were known, the skipped branch's call paired it onto its sampler's
        // index, out of the way; examples such as Dissolve.fx declare both forms.)
        var deadTextures = new HashSet<string>(StringComparer.Ordinal);
        foreach (string tex in textureNames)
            if (!pairedSampler.ContainsKey(tex) && !explicitIndex.ContainsKey(tex) && !Referenced(tex, textureDecls))
                deadTextures.Add(tex);

        // Whether the shader names the resource anywhere it can reach (outside every function, or in a
        // function the entry point reaches) besides its own declarations.
        bool Referenced(string name, MatchCollection declarations)
        {
            var declarationSpans = declarations.Where(d => d.Groups["name"].Value == name).Select(d => (d.Index, End: d.Index + d.Length)).ToList();
            return Regex.Matches(masked, $@"\b{Regex.Escape(name)}\b")
                .Any(m => !declarationSpans.Any(d => m.Index >= d.Index && m.Index < d.End) && inScope(m.Index));
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
        // Aliases (appended to textureNames after the declared textures) come last, so they never
        // move a declared texture's index.
        foreach (string tex in textureNames)
            if (!explicitIndex.ContainsKey(tex) && !deadTextures.Contains(tex))
                texturesExplicitFirst.Add(tex);
        foreach (string tex in textureNames)
            if (deadTextures.Contains(tex))
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
        var edits = new List<(int Index, int Length, string Text)>(aliasCallEdits);
        foreach (Match m in textureDecls)
        {
            string name = m.Groups["name"].Value;
            string declaration = NeedsRewrite(m, registerIndex, out int ti)
                ? $"{m.Groups["type"].Value}{m.Groups["tmpl"].Value} {name} : register(t{ti});"
                : m.Value;
            // Each alias is declared right after the texture it aliases, same type and template.
            if (aliasesByTexture.TryGetValue(name, out var textureAliases))
                foreach (string alias in textureAliases)
                    declaration += $" {m.Groups["type"].Value}{m.Groups["tmpl"].Value} {alias} : register(t{registerIndex[alias]});";
            if (!ReferenceEquals(declaration, m.Value))
                edits.Add((m.Index, m.Length, declaration));
        }
        foreach (Match m in samplerDecls)
            if (NeedsRewrite(m, registerIndex, out int si))
                edits.Add((m.Index, m.Length, $"{m.Groups["type"].Value} {m.Groups["name"].Value} : register(s{si});"));

        if (edits.Count == 0)
            return hlsl;

        edits.Sort((a, b) => a.Index.CompareTo(b.Index));
        var result = new System.Text.StringBuilder(hlsl.Length + edits.Count * 16);
        // Where each alias's first call lands in the returned text, for the caller's warning.
        var firstCall = declaredAliases.ToDictionary(a => a.TexAt, a => a);
        var reported = new List<Alias>();
        int copied = 0;
        foreach (var (index, length, text) in edits)
        {
            result.Append(hlsl, copied, index - copied);
            if (firstCall.TryGetValue(index, out var declared))
                reported.Add(new Alias(declared.Texture, declared.Sampler, declared.Name, result.Length));
            result.Append(text);
            copied = index + length;
        }
        result.Append(hlsl, copied, hlsl.Length - copied);
        aliases = reported;
        return result.ToString();
    }

    // ---- Reachability -----------------------------------------------------------------

    /// <summary>
    /// Whether an offset is code the shader whose entry point is <paramref name="entryPoint"/>
    /// can execute: outside every function body (declarations, techniques), or inside a function
    /// reachable from the entry point through calls by name (every overload of a called name
    /// counts, the conservative direction). With no entry point, or one the text does not
    /// define, everything is in scope.
    /// </summary>
    private static Func<int, bool> ReachableScope(string masked, string? entryPoint, Func<int, bool> live)
    {
        if (entryPoint is null)
            return _ => true;

        var bodies = new List<(string Name, int Start, int End)>();
        foreach (Match m in FunctionHeader.Matches(masked))
        {
            int start = m.Index + m.Length - 1;
            int end = MatchingBrace(masked, start);
            if (end > start)
                bodies.Add((m.Groups["fn"].Value, start, end));
        }
        if (!bodies.Any(b => b.Name == entryPoint))
            return _ => true;

        var reachable = new HashSet<string>(StringComparer.Ordinal) { entryPoint };
        var queue = new Queue<string>(reachable);
        var names = bodies.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            string fn = queue.Dequeue();
            foreach (var body in bodies.Where(b => b.Name == fn))
            {
                foreach (Match call in CallName.Matches(masked.Substring(body.Start, body.End - body.Start)))
                {
                    if (!live(body.Start + call.Index))
                        continue; // a call an untaken #if drops reaches nothing
                    string callee = call.Groups["fn"].Value;
                    if (names.Contains(callee) && reachable.Add(callee))
                        queue.Enqueue(callee);
                }
            }
        }

        // Only the outermost body counts for an offset (control-flow blocks match the header
        // pattern too, but they sit inside a function and share its reachability).
        var outer = bodies.Where(b => !bodies.Any(o => o.Start < b.Start && b.End < o.End)).ToList();
        return offset =>
        {
            foreach (var b in outer)
                if (offset > b.Start && offset < b.End)
                    return reachable.Contains(b.Name);
            return true;
        };
    }

    /// <summary>
    /// Whether an offset of <paramref name="text"/> is on a line the compile keeps: false only on a
    /// line in <paramref name="skippedLines"/> (1-based). Everything is live when the set is null or empty.
    /// </summary>
    private static Func<int, bool> LiveLines(string text, IReadOnlySet<int>? skippedLines)
    {
        if (skippedLines is null || skippedLines.Count == 0)
            return _ => true;

        // Lines numbered as FxMacroPreprocessor numbers them: a lone '\r' ends a line as '\n' and
        // "\r\n" do, so a skipped line found there is the same line here.
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
                lineStarts.Add(i + 1);
        return offset =>
        {
            int index = lineStarts.BinarySearch(offset);
            int line = (index >= 0 ? index : ~index - 1) + 1;
            return !skippedLines.Contains(line);
        };
    }

    // A call by name, a method call (`h.Get(uv)`, a struct method) included: reachability errs toward
    // reaching more, so a method's Sample calls still pair (a member call such as `Tex.Sample(` only
    // marks a same-named function reachable, the conservative direction).
    private static readonly Regex CallName = new(@"(?<!\w)(?<fn>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

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

    // ---- Helper-function pairing ----------------------------------------------------

    // A function definition: name, parameter list (no nested parentheses), an optional return
    // semantic, then the opening brace. Control-flow statements (`if (x) {`) also match; they have
    // no resource parameters, so they contribute nothing.
    private static readonly Regex FunctionHeader =
        new(@"(?<![\w.])(?<fn>[A-Za-z_]\w*)\s*\((?<params>[^()]*)\)\s*(?::\s*\w+\s*)?\{", RegexOptions.Compiled);

    // One parameter: optional modifiers, the type (with an optional template), the name.
    private static readonly Regex ParameterDecl =
        new(@"^\s*(?:(?:in|uniform|const)\s+)*(?<type>\w+)(?:\s*<[^>]*>)?\s+(?<name>\w+)", RegexOptions.Compiled);

    private static readonly Regex ResourceType =
        new(@"^(?:Texture\w*|SamplerState|SamplerComparisonState|sampler)$", RegexOptions.Compiled);

    private static readonly Regex Identifier = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);

    /// <summary>
    /// The global (texture, sampler) pairs made through helper functions: a helper that samples
    /// its own texture/sampler PARAMETERS (<c>float4 H(Texture2D t, SamplerState s, float2 uv)
    /// { return t.Sample(s, uv); }</c>), or a parameter texture through a GLOBAL sampler
    /// (<c>t.Sample(U, uv)</c>), pairs whatever its call site passes (<c>H(B, U, uv)</c> pairs
    /// <c>B</c> with <c>U</c>). Followed through helpers that call helpers. Only bare identifier
    /// arguments are followed (an array element or an expression is not unambiguous); a pair
    /// that stays hidden surfaces after the compile as a sampler with no texture on its binding
    /// (<c>SD0232</c>, after the preprocessed retry), never as a silent split descriptor.
    /// Each pair is reported at its call site.
    /// </summary>
    private static List<(string Tex, string Samp, int Offset)> HelperPairs(
        string masked, IReadOnlyList<string> textureNames, IReadOnlyList<string> samplerNames, Func<int, bool> live)
    {
        var result = new List<(string, string, int)>();

        // name -> (resource parameter names by position, body range); a later definition of the
        // same name (an overload) is skipped: which one a call resolves to is not decidable here.
        var functions = new Dictionary<string, (string?[] Params, int BodyStart, int BodyEnd, int HeaderAt)>(StringComparer.Ordinal);
        var overloaded = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in FunctionHeader.Matches(masked))
        {
            string[] rawParams = m.Groups["params"].Value.Split(',');
            var resourceParams = new string?[rawParams.Length];
            bool any = false;
            for (int i = 0; i < rawParams.Length; i++)
            {
                Match p = ParameterDecl.Match(rawParams[i]);
                if (p.Success && ResourceType.IsMatch(p.Groups["type"].Value))
                {
                    resourceParams[i] = p.Groups["name"].Value;
                    any = true;
                }
            }
            int bodyStart = m.Index + m.Length - 1;
            int bodyEnd = MatchingBrace(masked, bodyStart);
            string fn = m.Groups["fn"].Value;
            if (!any || bodyEnd < 0)
                continue;
            if (!functions.TryAdd(fn, (resourceParams, bodyStart, bodyEnd, m.Groups["fn"].Index)))
                overloaded.Add(fn);
        }
        foreach (string fn in overloaded)
            functions.Remove(fn);
        if (functions.Count == 0)
            return result;

        // A sampling template per function: each side is a parameter index or a global name.
        var templates = functions.ToDictionary(
            f => f.Key,
            f => new HashSet<(int TexParam, string? TexGlobal, int SampParam, string? SampGlobal)>(),
            StringComparer.Ordinal);
        foreach (var (fn, def) in functions)
        {
            foreach (Match s in SampleCall.Matches(masked.Substring(def.BodyStart, def.BodyEnd - def.BodyStart)))
            {
                if (!live(def.BodyStart + s.Index))
                    continue;
                (int tp, string? tg) = Ref(def.Params, s.Groups[1].Value);
                (int sp, string? sg) = Ref(def.Params, s.Groups[2].Value);
                if (tp >= 0 || sp >= 0)
                    templates[fn].Add((tp, tg, sp, sg));
            }
        }

        // Every call site of a helper, with its bare-identifier arguments (null otherwise) and the
        // helper whose body it sits in (null at global scope or in a function with no resources).
        var callSites = new List<(string Fn, string?[] Args, int Offset, string? Enclosing)>();
        foreach (var (fn, def) in functions)
        {
            foreach (Match c in Regex.Matches(masked, $@"(?<![\w.]){Regex.Escape(fn)}\s*\("))
            {
                if (c.Index == def.HeaderAt || !live(c.Index))
                    continue;
                string?[]? args = Arguments(masked, c.Index + c.Length - 1);
                if (args is null)
                    continue;
                string? enclosing = functions.FirstOrDefault(f => c.Index > f.Value.BodyStart && c.Index < f.Value.BodyEnd).Key;
                callSites.Add((fn, args, c.Index, enclosing));
            }
        }

        // Substitute until nothing new: a template resolved at a call site either names two
        // globals (a pair) or still names a parameter of the enclosing helper (a template there).
        var seen = new HashSet<(string, string, int)>();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (fn, args, offset, enclosing) in callSites)
            {
                foreach (var (tp, tg, sp, sg) in templates[fn].ToList())
                {
                    string? tex = tp >= 0 ? (tp < args.Length ? args[tp] : null) : tg;
                    string? samp = sp >= 0 ? (sp < args.Length ? args[sp] : null) : sg;
                    if (tex is null || samp is null)
                        continue;

                    if (enclosing is not null)
                    {
                        (int etp, string? etg) = Ref(functions[enclosing].Params, tex);
                        (int esp, string? esg) = Ref(functions[enclosing].Params, samp);
                        if (etp >= 0 || esp >= 0)
                        {
                            changed |= templates[enclosing].Add((etp, etg, esp, esg));
                            continue;
                        }
                    }

                    if (textureNames.Contains(tex) && samplerNames.Contains(samp) && seen.Add((tex, samp, offset)))
                        result.Add((tex, samp, offset));
                }
            }
        }
        return result;

        static (int Param, string? Global) Ref(string?[] parameters, string name)
        {
            int i = Array.IndexOf(parameters, name);
            return i >= 0 ? (i, null) : (-1, name);
        }
    }

    // The index of the brace closing the one at `open`, or -1.
    private static int MatchingBrace(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    // The top-level, comma-separated arguments of the call whose '(' is at `open`, each the bare
    // identifier it is or null, or null when the call does not close.
    private static string?[]? Arguments(string text, int open)
    {
        var args = new List<string?>();
        int depth = 0, start = open + 1;
        for (int i = open; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}')
            {
                if (--depth == 0)
                {
                    Add(text.Substring(start, i - start));
                    return args.ToArray();
                }
            }
            else if (ch == ',' && depth == 1)
            {
                Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }
        return null;

        void Add(string arg)
        {
            string trimmed = arg.Trim();
            args.Add(Identifier.IsMatch(trimmed) ? trimmed : null);
        }
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
