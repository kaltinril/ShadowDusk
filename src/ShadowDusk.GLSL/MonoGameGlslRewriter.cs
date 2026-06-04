#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.GLSL;

/// <summary>
/// A single sampler discovered while rewriting SPIRV-Cross GLSL into the
/// MonoGame/MojoShader dialect. <see cref="Name"/> is always <c>ps_s{Slot}</c>.
/// </summary>
public sealed record MonoGameGlslSampler(int Slot, string Name);

/// <summary>
/// Result of <see cref="MonoGameGlslRewriter.Rewrite"/>.
/// </summary>
/// <param name="Glsl">The rewritten legacy GLSL source.</param>
/// <param name="Samplers">Samplers in declaration order, renamed to <c>ps_s{k}</c>.</param>
/// <param name="UniformRegisterCount">
/// 0 if there was no uniform block; otherwise the number of
/// <c>ps_uniforms_vec4[]</c> registers (one per member).
/// </param>
public sealed record MonoGameGlslResult(
    string Glsl,
    IReadOnlyList<MonoGameGlslSampler> Samplers,
    int UniformRegisterCount);

/// <summary>
/// Rewrites the modern GLSL that SPIRV-Cross emits (<c>#version 140</c>,
/// <c>in</c>/<c>out</c>, <c>in_var_TEXCOORD0</c>, <c>texture()</c>, a named UBO)
/// into the legacy MojoShader dialect that MonoGame's OpenGL runtime expects
/// (legacy <c>varying</c> names, <c>gl_FragColor</c>, <c>texture2D()</c>,
/// <c>ps_uniforms_vec4[]</c>). This is a pure string transform with no external
/// dependencies.
/// </summary>
public static class MonoGameGlslRewriter
{
    // Matches mgfxc/MojoShader's emitted precision header byte-for-byte. Guarded
    // by `#ifdef GL_ES`, so desktop GLSL skips it entirely and runs at highp.
    //
    // NOTE (Phase 24 Dissolve investigation): mediump CAN flip data-dependent
    // `discard`/tint decisions on boundary texels under real WebGL hardware, where
    // `highp` would be the safer choice for precision-sensitive shaders. The
    // Phase 24 headless harness (ANGLE/SwiftShader) could NOT confirm this — its
    // software GL evaluates mediump and highp identically, so toggling this had
    // zero observed effect. The Dissolve divergence found there was instead the
    // unset slot-1 sampler state (see DISSOLVE-INVESTIGATION.md). Left at mediump
    // to stay faithful to mgfxc; revisit (→ highp) only with real-WebGL-hardware
    // evidence that a precision-sensitive shader needs it.
    private const string PrecisionHeader =
        "#ifdef GL_ES\n" +
        "precision mediump float;\n" +
        "precision mediump int;\n" +
        "#endif\n";

    // uniform sampler2D <id>;
    private static readonly Regex SamplerDecl = new(
        @"^\s*uniform\s+sampler2D\s+([A-Za-z_][A-Za-z0-9_]*)\s*;\s*$",
        RegexOptions.Compiled);

    // in <type> in_var_<SEM>;
    private static readonly Regex InputVaryingDecl = new(
        @"^\s*in\s+(float|vec2|vec3|vec4)\s+(in_var_[A-Za-z0-9_]+)\s*;\s*$",
        RegexOptions.Compiled);

    // out vec4 out_var_SV_Target<N?>;  (case-insensitive on the semantic: HLSL
    // SV_Target ≡ SV_TARGET ≡ sv_target, and DXC mirrors the source spelling).
    private static readonly Regex OutputDecl = new(
        @"^\s*out\s+vec4\s+(out_var_SV_Target[0-9]*)\s*;\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VersionLine = new(
        @"^\s*#version\b.*$",
        RegexOptions.Compiled);

    // layout(binding = N, std140) uniform type_Globals
    private static readonly Regex UniformBlockHeader = new(
        @"^\s*layout\s*\(\s*binding\s*=\s*\d+\s*,\s*std140\s*\)\s*uniform\s+type_Globals\s*$",
        RegexOptions.Compiled);

    // <type> <member>;
    private static readonly Regex UniformMember = new(
        @"^\s*(float|vec2|vec3|vec4|mat4)\s+([A-Za-z_][A-Za-z0-9_]*)\s*;\s*$",
        RegexOptions.Compiled);

    private sealed record InputVarying(string Identifier, string Type, string VaryingName);

    public static MonoGameGlslResult Rewrite(string glsl, ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(glsl);

        // Vertex stage: pass through unchanged for now.
        if (stage == ShaderStage.Vertex)
        {
            return new MonoGameGlslResult(glsl, Array.Empty<MonoGameGlslSampler>(), 0);
        }

        // Normalize Slang-emitted interface names to the DXC convention the rest of
        // this rewriter is keyed to. No-op for DXC/FXC output. (Browser path uses
        // Slang as the HLSL→SPIR-V frontend; SPIRV-Cross then names interface vars
        // after Slang's field/entrypoint identifiers rather than HLSL semantics.)
        glsl = NormalizeSlangNaming(glsl);

        // Non-2D sampler guard (Phase 33 generality). This rewriter only models the
        // plain `sampler2D`/`texture2D` shape (Phase 17's PS-only SpriteBatch corpus).
        // A `samplerCube`/`sampler3D`/`sampler2DArray`/… declaration is neither renamed
        // to ps_s{k} nor sampled with the right builtin — the body's `texture(cube,…)`
        // would be silently rewritten to `texture2D(cube,…)`, invalid GLSL that fails
        // only at GL link time. Fail loudly at compile time instead of emitting
        // silently-broken output (§ Scope: parity-or-loud-failure, never silent break).
        ThrowIfUnsupportedSamplerType(glsl);

        // Normalize newlines to '\n' for processing.
        var lines = glsl.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        var samplers = new List<MonoGameGlslSampler>();
        var samplerRenames = new Dictionary<string, string>(); // original id -> ps_sK
        var inputVaryings = new List<InputVarying>();           // in_var_* -> varying info
        var uniformMembers = new List<(string Type, string Member)>();

        var output = new List<string>();

        // ---- Pass 1: rewrite declarations, collect identifier mappings. ----
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Rule 1: strip #version line.
            if (VersionLine.IsMatch(line))
            {
                continue;
            }

            // Rule 1: strip the 420pack extension block.
            if (line.Trim() == "#ifdef GL_ARB_shading_language_420pack")
            {
                // Skip until matching #endif (3 lines: #ifdef / #extension / #endif).
                int j = i;
                while (j < lines.Length && lines[j].Trim() != "#endif")
                {
                    j++;
                }
                i = j; // loop will i++ past the #endif
                continue;
            }

            // Rule 7: uniform block header.
            if (UniformBlockHeader.IsMatch(line))
            {
                // Consume: header, optional '{', members..., '} _Globals;'
                int j = i + 1;
                // skip an opening brace line if present
                while (j < lines.Length && lines[j].Trim() != "{" && lines[j].Trim().Length == 0)
                {
                    j++;
                }
                if (j < lines.Length && lines[j].Trim() == "{")
                {
                    j++;
                }
                // members until closing '}'
                while (j < lines.Length && !lines[j].TrimStart().StartsWith("}"))
                {
                    var m = UniformMember.Match(lines[j]);
                    if (m.Success)
                    {
                        uniformMembers.Add((m.Groups[1].Value, m.Groups[2].Value));
                    }
                    j++;
                }
                // j now points at the '} _Globals;' line — skip it too.
                if (j < lines.Length)
                {
                    j++;
                }

                output.Add($"uniform vec4 ps_uniforms_vec4[{uniformMembers.Count}];");
                i = j - 1; // loop i++ moves past consumed block
                continue;
            }

            // Rule 3: sampler declaration.
            var samplerMatch = SamplerDecl.Match(line);
            if (samplerMatch.Success)
            {
                var origId = samplerMatch.Groups[1].Value;
                int slot = samplers.Count;
                var newName = $"ps_s{slot}";
                samplers.Add(new MonoGameGlslSampler(slot, newName));
                samplerRenames[origId] = newName;
                output.Add($"uniform sampler2D {newName};");
                continue;
            }

            // Rule 4: input varying declaration.
            var inMatch = InputVaryingDecl.Match(line);
            if (inMatch.Success)
            {
                var type = inMatch.Groups[1].Value;
                var ident = inMatch.Groups[2].Value;
                var varyingName = SemanticToVaryingName(ident);
                inputVaryings.Add(new InputVarying(ident, type, varyingName));
                output.Add($"varying vec4 {varyingName};");
                continue;
            }

            // Rule 5: output declaration — drop it.
            if (OutputDecl.IsMatch(line))
            {
                continue;
            }

            output.Add(line);
        }

        // ---- Pass 2: rewrite identifier USES in the body. ----
        var body = string.Join("\n", output);

        // Samplers: simple whole-word rename (declarations already done, but the
        // ones in the body are uses; declarations were rewritten in pass 1 so a
        // plain word rename is still safe).
        foreach (var (origId, newName) in samplerRenames)
        {
            body = ReplaceWord(body, origId, newName);
        }

        // Input varyings: rename + swizzle, honoring the trailing-'.' exception.
        foreach (var varying in inputVaryings)
        {
            body = ReplaceInputVaryingUses(body, varying);
        }

        // Uniform members: _Globals.<member> -> ps_uniforms_vec4[i]<swizzle>.
        for (int idx = 0; idx < uniformMembers.Count; idx++)
        {
            var (type, member) = uniformMembers[idx];
            var swizzle = SwizzleForType(type);
            string replacement;
            if (type == "mat4")
            {
                replacement = $"ps_uniforms_vec4[{idx}]/*TODO mat*/";
            }
            else
            {
                replacement = $"ps_uniforms_vec4[{idx}]{swizzle}";
            }

            // Match "_Globals.<member>" with a word boundary after the member.
            var pattern = $@"_Globals\.{Regex.Escape(member)}\b";
            body = Regex.Replace(body, pattern, replacement.Replace("$", "$$"));
        }

        // Rule 5: output uses → ps_oC{N} aliases (mgfxc/MojoShader form).
        //
        // mgfxc emits the fragment colour output as a `#define` alias, NOT a raw
        // `gl_FragColor` write — `#define ps_oC0 gl_FragColor` and writes to ps_oC0
        // (verified in tests/fixtures/golden/OpenGL/*.mgfx). KNI's WebGL2/HiDef
        // runtime converter rewrites ONLY that `#define`-aliased form to `out vec4`
        // under GLSL ES 3.00; a raw `gl_FragColor` write slips through untouched and
        // fails ("'gl_FragColor' : undeclared identifier") — issue #7. Emitting the
        // alias makes the one .mgfx load under Reach (WebGL1), HiDef (WebGL2) AND
        // desktop GL, strictly closer to the golden.
        //
        // HLSL SV_Target ≡ SV_Target0 — BOTH are the PRIMARY single colour output,
        // and mgfxc maps BOTH to `ps_oC0 → gl_FragColor`. Only SV_Target1/2/… (true
        // MRT) map to `ps_oC{N} → gl_FragData[N]`. (DXC/SPIRV-Cross spells the
        // primary `out_var_SV_Target` for `: COLOR` but `out_var_SV_Target0` for
        // `: COLOR0` — they MUST collapse to the same ps_oC0, else single-output
        // shaders like Sepia/Dissolve wrongly emit gl_FragData[0].)
        //
        // The `#define` lines are assembled as a SEPARATE string AFTER the Pass-2
        // regex rewrites (see final assembly) so those passes can't corrupt them,
        // and placed at column 0 in the header before main() — both required by
        // KNI's converter (regex `^#define …` Multiline; the post-conversion
        // `out vec4 ps_oC{N};` must be at global scope before main()).
        var fragmentOutputs = RewriteFragmentOutputs(ref body);

        // Rule 6: texture functions.
        body = Regex.Replace(body, @"\btextureLod\s*\(", "texture2DLod(");
        body = Regex.Replace(body, @"\btextureProj\s*\(", "texture2DProj(");
        body = Regex.Replace(body, @"\btexture\s*\(", "texture2D(");

        // Rule 6b: LOD / projected / gradient sampling guard (Phase 33 generality).
        //
        // The texture-LOD / -Proj / -Grad family CANNOT be emitted in a single GLSL
        // payload that is valid across ALL MonoGame/KNI GL profiles:
        //   • `texture2DLod`/`textureGrad` aren't core GLSL ES 1.00 fragment builtins,
        //     so they're unreliable/invalid under KNI Reach (WebGL1).
        //   • KNI's HiDef/WebGL2 runtime converter rewrites only `texture2D/3D/Cube(`
        //     (suffix immediately followed by '('), NOT the Lod/Proj/Grad variants —
        //     so whatever survives is undefined in GLSL ES 3.00 and the shader fails
        //     to load (the same class of bug as the raw-gl_FragColor one this phase
        //     fixes). ShadowDusk has ONE universal blob and no per-profile knob, so
        //     there is no form that satisfies both Reach and HiDef at once.
        // Per § Scope, the rewriter must therefore FAIL LOUDLY at compile time rather
        // than silently emit GLSL that breaks under HiDef (or Reach). Full support is
        // deferred to the Phase-34 follow-up.
        ThrowIfUnsupportedSampling(body);

        // Rule 8: lower roundEven()/round() to a WebGL1-valid expression.
        // SPIRV-Cross emits roundEven(x) (for HLSL `round`, which DXC maps to
        // OpRoundEven) — a GLSL ES 3.00 / desktop-GL 1.30 builtin that GLSL ES 1.00
        // (WebGL1, KNI's Reach profile) does NOT provide, so the shader fails to
        // load there ("'roundEven': no matching overloaded function found"). bare
        // round() is likewise ES-3.00-only. Lower both to floor(x + 0.5), which is
        // valid in every GLSL profile AND is exactly what mgfxc/MojoShader emits for
        // the same HLSL `round` (golden Pixelated computes `(x+0.5) - fract(x+0.5)`,
        // i.e. floor(x+0.5)) — so this stays faithful, same-backend, to the
        // reference compiler. See ROUNDEVEN-FIX.md.
        body = LowerRoundToFloorHalfUp(body);

        // ---- Assemble final output: precision header + #define block + body. ----
        // The fragment-output `#define` aliases are emitted here, AFTER all Pass-2
        // regex rewrites, so nothing can mangle them. They sit at column 0 in the
        // header (global scope, before main()) — exactly what KNI's ES-3.00
        // converter requires to rewrite `#define X gl_FragColor` → `out vec4 X;`.
        var defineBlock = new StringBuilder();
        foreach (var fo in fragmentOutputs)
        {
            defineBlock.Append("#define ").Append(fo.Alias).Append(' ').Append(fo.Builtin).Append('\n');
        }

        // Trim leading blank lines from the body so the header sits at the top,
        // preserving a single blank line separation.
        var trimmedBody = body.TrimStart('\n');
        var finalGlsl = PrecisionHeader + "\n" + defineBlock + trimmedBody;
        if (!finalGlsl.EndsWith("\n"))
        {
            finalGlsl += "\n";
        }

        return new MonoGameGlslResult(finalGlsl, samplers, uniformMembers.Count);
    }

    /// <summary>A fragment colour output discovered while rewriting the PS body.</summary>
    /// <param name="Alias">The MojoShader alias, always <c>ps_oC{N}</c>.</param>
    /// <param name="Builtin">The GLSL builtin the alias maps to: <c>gl_FragColor</c>
    /// for the primary output (N==0), <c>gl_FragData[N]</c> for MRT outputs.</param>
    private readonly record struct FragmentOutput(string Alias, string Builtin);

    // out_var_SV_Target  or  out_var_SV_Target<N>  used in the body. Case-insensitive
    // on the semantic (HLSL SV_Target ≡ SV_TARGET ≡ sv_target; DXC mirrors the source
    // spelling, e.g. `: SV_TARGET` → out_var_SV_TARGET) so the alias is emitted
    // regardless of how the author cased the return semantic.
    private static readonly Regex OutputUse = new(
        @"\bout_var_SV_Target([0-9]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Replaces every <c>out_var_SV_Target{N?}</c> use in <paramref name="body"/> with
    /// its MojoShader alias <c>ps_oC{N}</c> and returns the distinct outputs (in slot
    /// order) so the caller can emit the matching <c>#define</c> lines.
    ///
    /// <para><b>Primary collapse:</b> HLSL <c>SV_Target</c> ≡ <c>SV_Target0</c> — both
    /// are the single primary colour output. DXC names the former <c>out_var_SV_Target</c>
    /// (no digit) and the latter <c>out_var_SV_Target0</c>; both collapse to
    /// <c>ps_oC0 → gl_FragColor</c>. Only <c>SV_Target1</c>+ (true MRT) become
    /// <c>ps_oC{N} → gl_FragData[N]</c>. A discard-only / no-output shader yields an
    /// empty list (no <c>#define</c>, no <c>gl_FragColor</c>).</para>
    /// </summary>
    /// <exception cref="MonoGameGlslRewriteException">
    /// The body already contains a <c>ps_oC{N}</c> identifier (would be silently
    /// shadowed by the alias) — fail loudly rather than emit ambiguous GLSL.
    /// </exception>
    private static IReadOnlyList<FragmentOutput> RewriteFragmentOutputs(ref string body)
    {
        var matches = OutputUse.Matches(body);
        if (matches.Count == 0)
        {
            // No-output / discard-only shader: nothing to alias.
            return Array.Empty<FragmentOutput>();
        }

        // Distinct output slots, in ascending order. SV_Target (no digit) ≡ slot 0.
        var slots = new SortedSet<int>();
        foreach (Match m in matches)
        {
            slots.Add(m.Groups[1].Value.Length == 0 ? 0 : int.Parse(m.Groups[1].Value));
        }

        // Name-collision guard: a pre-existing ps_oC{N} token (e.g. hand-written HLSL
        // that survived) would be silently shadowed by our alias. Refuse rather than
        // emit ambiguous GLSL.
        foreach (int slot in slots)
        {
            if (Regex.IsMatch(body, $@"\bps_oC{slot}\b"))
            {
                throw new MonoGameGlslRewriteException(
                    $"GLSL rewrite collision: source already contains identifier 'ps_oC{slot}', " +
                    $"which clashes with the MojoShader fragment-output alias. Cannot safely rewrite.");
            }
        }

        // Replace uses: out_var_SV_Target{N?} -> ps_oC{N} (N omitted or 0 -> ps_oC0).
        body = OutputUse.Replace(body, m =>
        {
            int slot = m.Groups[1].Value.Length == 0 ? 0 : int.Parse(m.Groups[1].Value);
            return $"ps_oC{slot}";
        });

        var outputs = new List<FragmentOutput>(slots.Count);
        foreach (int slot in slots)
        {
            // Slot 0 is the primary colour output (gl_FragColor); 1+ are MRT
            // (gl_FragData[N]). Matches mgfxc's golden output exactly.
            string builtin = slot == 0 ? "gl_FragColor" : $"gl_FragData[{slot}]";
            outputs.Add(new FragmentOutput($"ps_oC{slot}", builtin));
        }

        return outputs;
    }

    // LOD / projected / gradient sampling builtins that have no single-blob form
    // valid across desktop GL + KNI Reach (WebGL1) + KNI HiDef (WebGL2). Maps the
    // emitted GLSL token → the HLSL intrinsic that produced it, for the diagnostic.
    private static readonly (string Glsl, string Hlsl)[] UnsupportedSampling =
    {
        ("texture2DLod",  "tex2Dlod / SampleLevel"),
        ("texture2DProj", "tex2Dproj"),
        ("texture2DGrad", "tex2Dgrad / SampleGrad"),
        ("textureLod",    "tex2Dlod / SampleLevel"),
        ("textureProj",   "tex2Dproj"),
        ("textureGrad",   "tex2Dgrad / SampleGrad"),
    };

    // A `uniform sampler<KIND> <id>;` declaration whose KIND is anything other than
    // the plain 2D form this rewriter models (samplerCube, sampler3D, sampler2DArray,
    // sampler2DShadow, …). `(?!2D\b)` lets `sampler2D` through but catches the rest.
    private static readonly Regex NonPlain2DSamplerDecl = new(
        @"^\s*uniform\s+(?:[a-z]+\s+)?sampler(?!2D\s)([A-Za-z0-9]+)\s+[A-Za-z_][A-Za-z0-9_]*\s*;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Throws <see cref="MonoGameGlslRewriteException"/> if any sampler declaration is
    /// not the plain <c>sampler2D</c> shape the MojoShader rewrite models. See the
    /// call site for why a non-2D sampler would otherwise produce silently-broken GLSL.
    /// </summary>
    private static void ThrowIfUnsupportedSamplerType(string glsl)
    {
        Match m = NonPlain2DSamplerDecl.Match(glsl);
        if (m.Success)
        {
            string kind = "sampler" + m.Groups[1].Value;
            throw new MonoGameGlslRewriteException(
                $"Unsupported sampler type for the MonoGame/KNI GL target: '{kind}'. The MojoShader-" +
                $"dialect rewrite models only 'sampler2D' (the PS-only SpriteBatch corpus); a " +
                $"'{kind}' would be emitted as silently-broken GLSL (e.g. texture2D() on a non-2D " +
                $"sampler) that fails at GL link time. Use a Texture2D, or extend the rewriter. " +
                $"(Tracked for Phase 34.)");
        }
    }

    /// <summary>
    /// Throws <see cref="MonoGameGlslRewriteException"/> if the rewritten body still
    /// contains a LOD / projected / gradient texture builtin. See Rule 6b for why
    /// these cannot be expressed in a single profile-agnostic GLSL payload.
    /// </summary>
    private static void ThrowIfUnsupportedSampling(string body)
    {
        foreach (var (glslToken, hlslIntrinsic) in UnsupportedSampling)
        {
            // Whole-identifier, call position (suffix followed by '(').
            if (Regex.IsMatch(body, $@"\b{Regex.Escape(glslToken)}\s*\("))
            {
                throw new MonoGameGlslRewriteException(
                    $"Unsupported texture sampling for the MonoGame/KNI GL target: '{glslToken}' " +
                    $"(from HLSL {hlslIntrinsic}). LOD/projected/gradient sampling has no single GLSL " +
                    $"form valid in both KNI Reach (WebGL1) and KNI HiDef (WebGL2), and KNI's runtime " +
                    $"ES-3.00 converter does not rewrite it — so it would silently fail to load under " +
                    $"HiDef. Use a plain tex2D / Texture2D.Sample, or precompute the LOD. " +
                    $"(Tracked for Phase 34.)");
            }
        }
    }

    /// <summary>
    /// Rewrites Slang's GLSL interface names into the DXC convention this rewriter
    /// expects. Slang (the browser HLSL→SPIR-V frontend) names interface variables
    /// after the source field / entry-point identifier, whereas DXC names them after
    /// the HLSL semantic; SPIR-V carries no semantic string, so the mapping is applied
    /// here by the PS-only SpriteBatch input contract (color:COLOR0 + texcoord:TEXCOORD0).
    /// Idempotent / no-op for DXC output (those identifier patterns never appear).
    /// </summary>
    private static string NormalizeSlangNaming(string glsl)
    {
        // (a) PS color output: Slang emits entryPointParam_<Entry>; DXC: out_var_SV_Target.
        glsl = Regex.Replace(glsl, @"\bentryPointParam_[A-Za-z0-9_]+\b", "out_var_SV_Target");

        // (b) Globals UBO: Slang types the block <Name>_default with instance e.g.
        // globalParams; DXC uses type_Globals { ... } _Globals. Rename a std140 block
        // whose type isn't already type_Globals, then point its member uses at _Globals.
        var ubo = Regex.Match(
            glsl,
            @"(layout\s*\([^)]*\bstd140\b[^)]*\)\s*uniform\s+)([A-Za-z_][A-Za-z0-9_]*)(\s*\{[^}]*\}\s*)([A-Za-z_][A-Za-z0-9_]*)(\s*;)",
            RegexOptions.Singleline);
        if (ubo.Success && ubo.Groups[2].Value != "type_Globals")
        {
            string instance = ubo.Groups[4].Value;
            glsl = glsl.Remove(ubo.Groups[2].Index, ubo.Groups[2].Length)
                       .Insert(ubo.Groups[2].Index, "type_Globals");
            glsl = Regex.Replace(glsl, $@"\b{Regex.Escape(instance)}\b", "_Globals");
        }

        // (c) PS inputs: Slang emits input_<Field>; map to in_var_<SEM> by convention.
        foreach (Match m in Regex.Matches(glsl, @"\bin\s+(float|vec2|vec3|vec4)\s+(input_[A-Za-z0-9_]+)\s*;"))
        {
            string type  = m.Groups[1].Value;
            string ident = m.Groups[2].Value;
            string sem   = SlangInputSemantic(ident, type);
            glsl = Regex.Replace(glsl, $@"\b{Regex.Escape(ident)}\b", "in_var_" + sem);
        }

        return glsl;
    }

    /// <summary>
    /// Maps a Slang input identifier (<c>input_&lt;Field&gt;</c>) to its HLSL semantic
    /// under the PS-only SpriteBatch contract: a color (vec4 / name says "color") is
    /// COLOR0, a texture coordinate (vec2 / name says tex/coord/uv) is TEXCOORD0.
    /// </summary>
    private static string SlangInputSemantic(string ident, string type)
    {
        const string prefix = "input_";
        string name  = ident.StartsWith(prefix) ? ident[prefix.Length..] : ident;
        string lower = name.ToLowerInvariant();

        if (lower.Contains("color")) return "COLOR0";
        if (lower.Contains("tex") || lower.Contains("coord") || lower.Contains("uv")) return "TEXCOORD0";

        // Fallback by type: SpriteBatch hands the PS a vec4 color + vec2 texcoord.
        return type == "vec4" ? "COLOR0" : "TEXCOORD0";
    }

    private static string SemanticToVaryingName(string identifier)
    {
        // identifier looks like "in_var_TEXCOORD0".
        const string prefix = "in_var_";
        var sem = identifier.StartsWith(prefix) ? identifier[prefix.Length..] : identifier;

        switch (sem)
        {
            case "COLOR0":
                return "vFrontColor";
            case "COLOR1":
                return "vBackColor";
        }

        if (sem.StartsWith("TEXCOORD"))
        {
            var n = sem["TEXCOORD".Length..];
            return $"vTexCoord{n}";
        }

        // Unknown semantic — pass through (won't occur in our corpus).
        return $"var_{sem}";
    }

    private static string SwizzleForType(string type) => type switch
    {
        "float" => ".x",
        "vec2" => ".xy",
        "vec3" => ".xyz",
        "vec4" => "",
        _ => "",
    };

    private static string ReplaceWord(string input, string word, string replacement)
    {
        var pattern = $@"\b{Regex.Escape(word)}\b";
        return Regex.Replace(input, pattern, replacement.Replace("$", "$$"));
    }

    /// <summary>
    /// Replaces uses of an input varying identifier with its legacy varying name,
    /// appending a width-truncating swizzle — except when the use is immediately
    /// followed by '.' (an existing swizzle), in which case only the rename happens.
    /// </summary>
    private static string ReplaceInputVaryingUses(string input, InputVarying varying)
    {
        var swizzle = SwizzleForType(varying.Type);
        var pattern = $@"\b{Regex.Escape(varying.Identifier)}\b";

        return Regex.Replace(input, pattern, match =>
        {
            int after = match.Index + match.Length;
            bool followedByDot = after < input.Length && input[after] == '.';
            return followedByDot ? varying.VaryingName : varying.VaryingName + swizzle;
        });
    }

    // roundEven / round identifiers to lower, longest first so "roundEven" is
    // matched before the "round" prefix.
    private static readonly string[] RoundFns = { "roundEven", "round" };

    /// <summary>
    /// Rewrites every <c>roundEven(<i>expr</i>)</c> / <c>round(<i>expr</i>)</c> call
    /// to <c>floor((<i>expr</i>) + 0.5)</c> (round-half-up), which is valid in all
    /// GLSL profiles — unlike the ES-3.00/GL-1.30-only <c>roundEven</c>/<c>round</c>
    /// builtins that WebGL1 (GLSL ES 1.00, KNI's Reach profile) rejects. This matches
    /// what mgfxc/MojoShader emits for HLSL <c>round</c>, so it preserves same-backend
    /// render parity. The argument is captured with a balanced-parenthesis scan so a
    /// nested call (e.g. <c>round(a * f(b))</c>) is lowered correctly.
    /// </summary>
    private static string LowerRoundToFloorHalfUp(string body)
    {
        foreach (var fn in RoundFns)
        {
            int searchFrom = 0;
            while (true)
            {
                int callStart = FindCallStart(body, fn, searchFrom);
                if (callStart < 0)
                {
                    break;
                }

                int openParen = callStart + fn.Length;
                int closeParen = FindMatchingParen(body, openParen);
                if (closeParen < 0)
                {
                    // Unbalanced (should not happen in valid GLSL) — stop lowering
                    // this fn rather than corrupt the source.
                    break;
                }

                string arg = body.Substring(openParen + 1, closeParen - openParen - 1);
                string replacement = $"floor(({arg}) + 0.5)";
                body = body.Substring(0, callStart) + replacement + body.Substring(closeParen + 1);
                searchFrom = callStart + replacement.Length;
            }
        }

        return body;
    }

    /// <summary>
    /// Finds the next whole-identifier occurrence of <paramref name="fn"/> in
    /// <paramref name="body"/> at or after <paramref name="from"/> that is immediately
    /// followed (ignoring whitespace) by '(', returning the identifier's start index,
    /// or -1. "Whole-identifier" rejects matches inside a longer identifier (e.g. the
    /// "round" inside "roundEven" or a user "myround").
    /// </summary>
    private static int FindCallStart(string body, string fn, int from)
    {
        int i = from;
        while ((i = body.IndexOf(fn, i, StringComparison.Ordinal)) >= 0)
        {
            bool boundaryBefore = i == 0 || !IsIdentChar(body[i - 1]);
            int afterId = i + fn.Length;
            // The identifier must not run into more identifier chars (e.g. "roundEven"
            // when fn == "round").
            bool boundaryAfter = afterId >= body.Length || !IsIdentChar(body[afterId]);

            // Skip whitespace between the identifier and the '('.
            int j = afterId;
            while (j < body.Length && (body[j] == ' ' || body[j] == '\t'))
            {
                j++;
            }
            bool isCall = j < body.Length && body[j] == '(';

            if (boundaryBefore && boundaryAfter && isCall)
            {
                return i;
            }

            i = afterId;
        }

        return -1;
    }

    /// <summary>
    /// Given the index of an opening '(' in <paramref name="body"/>, returns the index
    /// of its matching ')', or -1 if unbalanced.
    /// </summary>
    private static int FindMatchingParen(string body, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool IsIdentChar(char c) =>
        c == '_' || char.IsLetterOrDigit(c);
}
