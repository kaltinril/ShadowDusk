#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.GLSL;

/// <summary>
/// The dimensionality of a sampler the rewriter modelled, mapped 1:1 onto
/// MonoGame's <c>SamplerType</c> enum byte (the value the .mgfx sampler record
/// carries). Verified against MonoGame's <c>Shader.cs</c> reader and an mgfxc
/// cube golden — see <c>PHASE34-INVESTIGATION.md</c> §3. Do NOT renumber.
/// </summary>
public enum MonoGameSamplerDimension : byte
{
    /// <summary><c>sampler2D</c> — MonoGame <c>SamplerType.Sampler2D</c>.</summary>
    Texture2D = 0,
    /// <summary><c>samplerCube</c> — MonoGame <c>SamplerType.SamplerCube</c>.</summary>
    TextureCube = 1,
    /// <summary><c>sampler3D</c> — MonoGame <c>SamplerType.SamplerVolume</c>.</summary>
    TextureVolume = 2,
}

/// <summary>
/// A single sampler discovered while rewriting SPIRV-Cross GLSL into the
/// MonoGame/MojoShader dialect. <see cref="Name"/> is always <c>ps_s{Slot}</c>.
/// <see cref="Dimension"/> is the sampler's dimensionality (2D / cube / 3D),
/// which the pipeline encodes into the .mgfx sampler-type byte.
/// </summary>
public sealed record MonoGameGlslSampler(
    int Slot,
    string Name,
    MonoGameSamplerDimension Dimension = MonoGameSamplerDimension.Texture2D);

/// <summary>
/// One vertex-input attribute discovered while rewriting a VERTEX shader's
/// SPIRV-Cross GLSL. The attribute is renamed to the MojoShader form
/// <c>vs_v{Slot}</c> (declaration order), and <see cref="Usage"/>/<see cref="Index"/>
/// carry the <c>VertexElementUsage</c>+semantic-index the pipeline writes into the
/// .mgfx attribute table so MonoGame's GL runtime binds the attribute to the right
/// vertex element. Empty for pixel shaders.
/// </summary>
/// <param name="Slot">Declaration order (0-based) — the <c>{N}</c> in <c>vs_v{N}</c>.</param>
/// <param name="Name">Always <c>vs_v{Slot}</c>.</param>
/// <param name="Usage">MonoGame <c>VertexElementUsage</c> byte (Position=0, Color=1, TextureCoordinate=2, Normal=3, …).</param>
/// <param name="Index">The semantic index (e.g. TEXCOORD1 → 1).</param>
public sealed record MonoGameGlslAttribute(
    int    Slot,
    string Name,
    byte   Usage,
    byte   Index);

/// <summary>
/// Result of <see cref="MonoGameGlslRewriter.Rewrite"/>.
/// </summary>
/// <param name="Glsl">The rewritten legacy GLSL source.</param>
/// <param name="Samplers">Samplers in declaration order, renamed to <c>ps_s{k}</c> (pixel stage only).</param>
/// <param name="UniformRegisterCount">
/// 0 if there was no uniform block; otherwise the number of
/// <c>ps_uniforms_vec4[]</c>/<c>vs_uniforms_vec4[]</c> registers (one per member, a
/// <c>mat4</c> counting as four).
/// </param>
/// <param name="Attributes">
/// Vertex-input attributes in declaration order, renamed to <c>vs_v{k}</c> (vertex
/// stage only; empty for pixel shaders).
/// </param>
public sealed record MonoGameGlslResult(
    string Glsl,
    IReadOnlyList<MonoGameGlslSampler> Samplers,
    int UniformRegisterCount,
    IReadOnlyList<MonoGameGlslAttribute> Attributes)
{
    /// <summary>Back-compat constructor: pixel-stage results carry no attributes.</summary>
    public MonoGameGlslResult(
        string Glsl,
        IReadOnlyList<MonoGameGlslSampler> Samplers,
        int UniformRegisterCount)
        : this(Glsl, Samplers, UniformRegisterCount, Array.Empty<MonoGameGlslAttribute>())
    {
    }
}

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

    // uniform sampler{2D|Cube|3D} <id>;  — captures the dimension keyword (group 1)
    // and the identifier (group 2). SPIRV-Cross emits the dimension-specific sampler
    // type for the decl (samplerCube / sampler3D) but the GENERIC texture() for the
    // call (verified, Phase 34) — so the rewriter reads the dimension HERE and uses it
    // to pick the matching texture builtin in Pass 2.
    private static readonly Regex SamplerDecl = new(
        @"^\s*uniform\s+sampler(2D|Cube|3D)\s+([A-Za-z_][A-Za-z0-9_]*)\s*;\s*$",
        RegexOptions.Compiled);

    // in <type> in_var_<SEM>;
    private static readonly Regex InputVaryingDecl = new(
        @"^\s*in\s+(float|vec2|vec3|vec4)\s+(in_var_[A-Za-z0-9_]+)\s*;\s*$",
        RegexOptions.Compiled);

    // out <type> out_var_<SEM>;  — a VERTEX shader's user output (becomes a legacy
    // `varying`). Excludes the SV_Target output (that is the pixel-shader colour, and
    // is handled by OutputDecl). The pixel stage never has user `out_var_*` outputs
    // other than SV_Target, so this is only consulted on the vertex stage.
    private static readonly Regex OutputVaryingDecl = new(
        @"^\s*out\s+(float|vec2|vec3|vec4)\s+(out_var_[A-Za-z0-9_]+)\s*;\s*$",
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

    /// <summary>
    /// Rewrites SPIRV-Cross GLSL into the MonoGame/MojoShader dialect for the given stage.
    /// </summary>
    /// <param name="glsl">The modern GLSL emitted by SPIRV-Cross.</param>
    /// <param name="stage">The shader stage being rewritten (vertex or pixel).</param>
    /// <returns>
    /// The rewritten GLSL together with the discovered samplers, uniform register count, and
    /// (for the vertex stage) vertex attributes.
    /// </returns>
    /// <exception cref="MonoGameGlslRewriteException">
    /// Thrown when the input GLSL cannot be rewritten faithfully into the dialect.
    /// </exception>
    public static MonoGameGlslResult Rewrite(string glsl, ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(glsl);

        bool isVertex = stage == ShaderStage.Vertex;
        // The MojoShader register-array prefix is the ONLY stage knob on the uniform
        // side: pixel free uniforms bind as ps_uniforms_vec4[], vertex as
        // vs_uniforms_vec4[] (MonoGame's GL runtime keys glUniform4fv on this name).
        string regPrefix = isVertex ? "vs" : "ps";

        // Normalize Slang-emitted interface names to the DXC convention the rest of
        // this rewriter is keyed to. No-op for DXC/FXC output. (Browser path uses
        // Slang as the HLSL→SPIR-V frontend; SPIRV-Cross then names interface vars
        // after Slang's field/entrypoint identifiers rather than HLSL semantics.)
        glsl = NormalizeSlangNaming(glsl);

        // Unsupported-sampler guard (Phase 33 → narrowed in Phase 34). The rewriter
        // now models sampler2D, samplerCube AND sampler3D — each renamed to ps_s{k} and
        // sampled with its matching builtin (texture2D / textureCube / texture3D). Only
        // sampler kinds it still doesn't model (sampler2DArray, sampler2DShadow, …) are
        // rejected loudly here, so they fail at compile time instead of being silently
        // rewritten to texture2D() — invalid GLSL that fails only at GL link time.
        ThrowIfUnsupportedSamplerType(glsl);

        // Normalize newlines to '\n' for processing.
        var lines = glsl.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        var samplers = new List<MonoGameGlslSampler>();
        var samplerRenames = new Dictionary<string, string>(); // original id -> ps_sK
        var inputVaryings = new List<InputVarying>();           // PS: in_var_* -> varying read
        var outputVaryings = new List<InputVarying>();          // VS: out_var_* -> varying write
        var attributes = new List<MonoGameGlslAttribute>();     // VS: in_var_* -> attribute vs_vK
        var attributeReads = new List<InputVarying>();          // VS: in_var_* read width/rename
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

                // The register count is NOT the member count: a mat4 occupies FOUR
                // consecutive 16-byte registers (matching the .mgfx cbuffer packing in
                // BuildConstantBufferInfoList and the std140 layout SPIRV-Cross assumed),
                // every other member occupies one. So the array length is the running
                // register total.
                output.Add($"uniform vec4 {regPrefix}_uniforms_vec4[{RegisterCount(uniformMembers)}];");
                i = j - 1; // loop i++ moves past consumed block
                continue;
            }

            // Rule 3: sampler declaration (sampler2D / samplerCube / sampler3D). Pixel
            // stage only — MonoGame's GL VS goldens carry no samplers, and a VS sampler
            // would need its own vs_s{k} contract that no corpus exercises.
            var samplerMatch = SamplerDecl.Match(line);
            if (samplerMatch.Success && !isVertex)
            {
                var kind = samplerMatch.Groups[1].Value;     // "2D" | "Cube" | "3D"
                var origId = samplerMatch.Groups[2].Value;
                int slot = samplers.Count;
                var newName = $"ps_s{slot}";
                var dimension = SamplerDimensionForKind(kind);
                samplers.Add(new MonoGameGlslSampler(slot, newName, dimension));
                samplerRenames[origId] = newName;
                // Keep the dimension-specific decl keyword. KNI's HiDef/WebGL2 converter
                // rewrites samplerCube/sampler3D usage cleanly; desktop GL and WebGL1
                // accept the legacy decls.
                output.Add($"uniform sampler{kind} {newName};");
                continue;
            }

            // Rule 4: input declaration.
            var inMatch = InputVaryingDecl.Match(line);
            if (inMatch.Success)
            {
                var type = inMatch.Groups[1].Value;
                var ident = inMatch.Groups[2].Value;
                if (isVertex)
                {
                    // VS input = a vertex ATTRIBUTE. MojoShader names attributes
                    // vs_v{k} (declaration order) and declares them vec4 regardless of
                    // the source width (matches the SpriteEffect golden). The semantic
                    // → VertexElementUsage+index mapping is captured for the .mgfx
                    // attribute table so MonoGame binds the right vertex element.
                    int slot = attributes.Count;
                    var attrName = $"vs_v{slot}";
                    var (usage, index) = SemanticToVertexUsage(ident);
                    attributes.Add(new MonoGameGlslAttribute(slot, attrName, usage, index));
                    // The attribute is DECLARED vec4 (mgfxc form) but a narrower source
                    // (float3 POSITION / float2 TEXCOORD) must read a truncating swizzle
                    // so a use like `vec4(in_var_POSITION0, 1.0)` stays well-typed.
                    attributeReads.Add(new InputVarying(ident, type, attrName));
                    output.Add($"attribute vec4 {attrName};");
                }
                else
                {
                    // PS input = a legacy varying the built-in/custom VS wrote.
                    var varyingName = SemanticToVaryingName(ident);
                    inputVaryings.Add(new InputVarying(ident, type, varyingName));
                    output.Add($"varying vec4 {varyingName};");
                }
                continue;
            }

            // Rule 4b (VS only): user output declaration -> legacy varying WRITE. The
            // varying name MUST match what the pixel shader reads (vFrontColor /
            // vTexCoord{n}) — MonoGame links VS→PS by varying NAME, not index.
            if (isVertex)
            {
                var outVaryMatch = OutputVaryingDecl.Match(line);
                if (outVaryMatch.Success)
                {
                    var type = outVaryMatch.Groups[1].Value;
                    var ident = outVaryMatch.Groups[2].Value;
                    var varyingName = SemanticToVaryingName(ident);
                    outputVaryings.Add(new InputVarying(ident, type, varyingName));
                    output.Add($"varying vec4 {varyingName};");
                    continue;
                }
            }

            // Rule 5: pixel-shader colour output declaration — drop it (a VS has no
            // SV_Target; gl_Position is a builtin and needs no decl).
            if (!isVertex && OutputDecl.IsMatch(line))
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

        // VS attributes: rename in_var_<SEM> -> vs_v{k}, appending a width-truncating
        // swizzle (the attribute is declared vec4, but a `float3 POSITION` etc. must
        // read .xyz). Trailing-'.' exception so an existing swizzle isn't doubled.
        foreach (var read in attributeReads)
        {
            body = ReplaceInputVaryingUses(body, read);
        }

        // PS input varyings: rename + swizzle, honoring the trailing-'.' exception.
        foreach (var varying in inputVaryings)
        {
            body = ReplaceInputVaryingUses(body, varying);
        }

        // VS output varyings: rename out_var_<SEM> -> the matching legacy varying.
        // Width handling: the varying is declared vec4 but the VS may write a narrower
        // type (vec2 TEXCOORD). A direct rename keeps the write valid because the body
        // already assigns the correct width to a possibly-narrower swizzle target; the
        // legacy varying is vec4 so any extra channels are simply unused by the PS.
        foreach (var varying in outputVaryings)
        {
            body = ReplaceOutputVaryingUses(body, varying);
        }

        // Uniform members: _Globals.<member> -> {prefix}_uniforms_vec4[reg]<swizzle>.
        // The register OFFSET is the running register total so a mat4 (4 registers)
        // correctly shifts every member after it — this is the exact same packing as
        // BuildConstantBufferInfoList, so the GLSL index lands on the right bytes.
        int reg = 0;
        for (int idx = 0; idx < uniformMembers.Count; idx++)
        {
            var (type, member) = uniformMembers[idx];
            string replacement;
            if (type == "mat4")
            {
                // A mat4 occupies registers reg..reg+3. SPIRV-Cross emits the matrix
                // column-major (GLSL native) and multiplies M * v; std140 stores each
                // matrix COLUMN at a 16-byte register (column0 @ reg, column1 @ reg+1,
                // …). GLSL mat4(c0,c1,c2,c3) takes COLUMNS, so reconstructing
                // mat4(reg, reg+1, reg+2, reg+3) reproduces the original matrix exactly.
                replacement =
                    $"mat4({regPrefix}_uniforms_vec4[{reg}], {regPrefix}_uniforms_vec4[{reg + 1}], " +
                    $"{regPrefix}_uniforms_vec4[{reg + 2}], {regPrefix}_uniforms_vec4[{reg + 3}])";
                reg += 4;
            }
            else
            {
                var swizzle = SwizzleForType(type);
                replacement = $"{regPrefix}_uniforms_vec4[{reg}]{swizzle}";
                reg += 1;
            }

            // Match "_Globals.<member>" with a word boundary after the member.
            var pattern = $@"_Globals\.{Regex.Escape(member)}\b";
            body = Regex.Replace(body, pattern, replacement.Replace("$", "$$"));
        }

        // Vertex stage: assemble + return now. No fragment-output / texture / round
        // passes — those are pixel-stage rules. The precision header for a VS uses
        // highp float (matching the mgfxc VS golden, which needs full precision for
        // the position transform) rather than the mediump the PS uses.
        if (isVertex)
        {
            var vsTrimmed = body.TrimStart('\n');
            var vsGlsl = VertexPrecisionHeader + "\n" + vsTrimmed;
            if (!vsGlsl.EndsWith("\n"))
            {
                vsGlsl += "\n";
            }
            return new MonoGameGlslResult(vsGlsl, Array.Empty<MonoGameGlslSampler>(), reg, attributes);
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

        // Rule 6: texture functions — per-sampler-dimension (Phase 34).
        //
        // SPIRV-Cross emits the GENERIC `texture(<sampler>, …)` for EVERY sampler
        // dimension (2D, cube, 3D alike). MonoGame's GL runtime (and KNI's WebGL1/Reach
        // profile) speaks the legacy dialect, which needs the DIMENSION-SPECIFIC builtin
        // — texture2D / textureCube / texture3D — matching each sampler's type. So the
        // rewrite is keyed to each modelled sampler (renamed to ps_s{k} above): rewrite
        // `texture(ps_s{k}, …)` → `<builtin>(ps_s{k}, …)` per its dimension.
        //
        // The `\btexture\s*\(` pattern matches ONLY the bare `texture(` form — it does
        // NOT match `textureLod(` / `textureGrad(` / `textureProj(` (the suffix sits
        // between `texture` and `(`), so those LOD/grad/proj calls are intentionally
        // left in their GENERIC ES-3.00 form (see Rule 6b).
        foreach (var sampler in samplers)
        {
            string builtin = TextureBuiltinForDimension(sampler.Dimension);
            // texture(ps_sK, ...) -> <builtin>(ps_sK, ...)  (whole-word sampler name).
            body = Regex.Replace(
                body,
                $@"\btexture\s*\(\s*{Regex.Escape(sampler.Name)}\b",
                $"{builtin}({sampler.Name}");
        }

        // Defensive: any remaining bare `texture(` not bound to a modelled sampler
        // (should not occur for the PS corpus) falls back to texture2D, preserving the
        // prior behaviour.
        body = Regex.Replace(body, @"\btexture\s*\(", "texture2D(");

        // Rule 6b: LOD / projected / gradient sampling (Phase 34 — generic form kept).
        //
        // The LOD/grad/proj family is left in SPIRV-Cross's GENERIC spelling
        // (`textureLod` / `textureGrad` / `textureProj`), NOT down-rewritten to the
        // legacy `texture2DLod` etc. Rationale (verified, PHASE34-INVESTIGATION.md §6):
        //   • The generic forms compile on desktop GL (legacy dialect) AND are core in
        //     GLSL ES 3.00, so KNI's HiDef/WebGL2 converter passes them through untouched
        //     (it only rewrites the texture2D/3D/Cube suffixes). The legacy
        //     `texture2DLod` is NOT an ES-3.00 builtin and KNI does NOT convert it → it
        //     would fail HiDef. So the generic form is the single-blob-correct choice.
        //   • On KNI Reach (WebGL1) explicit-LOD/gradient in a fragment shader is only
        //     available behind the optional GL_EXT_shader_texture_lod extension — a
        //     genuine platform wall, documented (not compile-time detectable; we emit
        //     ONE blob and cannot know the consumer's profile). This is the same honest-
        //     limitation pattern as 3D textures on Reach.
        // No compile-time guard fires here any longer: the generic LOD/grad/proj form
        // is valid output on the targets that support it; the Reach wall is documented.

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

        return new MonoGameGlslResult(finalGlsl, samplers, reg, Array.Empty<MonoGameGlslAttribute>());
    }

    /// <summary>
    /// The number of 16-byte registers the uniform members occupy: a <c>mat4</c> spans
    /// four, every other member one. This is the <c>{prefix}_uniforms_vec4[]</c> array
    /// length, kept in lockstep with the .mgfx cbuffer packing.
    /// </summary>
    private static int RegisterCount(IReadOnlyList<(string Type, string Member)> members)
    {
        int n = 0;
        foreach (var (type, _) in members)
        {
            n += type == "mat4" ? 4 : 1;
        }
        return n;
    }

    // The vertex stage uses highp float (the position transform needs full precision);
    // the mgfxc VS golden does exactly this. The pixel stage stays at mediump
    // (PrecisionHeader) to match mgfxc's PS output.
    private const string VertexPrecisionHeader =
        "#ifdef GL_ES\n" +
        "precision highp float;\n" +
        "precision mediump int;\n" +
        "#endif\n";

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

    /// <summary>
    /// Maps a SPIRV-Cross sampler-decl keyword suffix (<c>"2D"</c> / <c>"Cube"</c> /
    /// <c>"3D"</c>, captured by <see cref="SamplerDecl"/>) to the modelled dimension.
    /// </summary>
    private static MonoGameSamplerDimension SamplerDimensionForKind(string kind) => kind switch
    {
        "Cube" => MonoGameSamplerDimension.TextureCube,
        "3D"   => MonoGameSamplerDimension.TextureVolume,
        _      => MonoGameSamplerDimension.Texture2D,
    };

    /// <summary>
    /// The legacy-dialect texture builtin for a sampler dimension. mgfxc/MojoShader and
    /// MonoGame's GL runtime use the dimension-specific spelling; KNI's HiDef converter
    /// rewrites <c>texture2D/3D/Cube(</c> → <c>texture(</c> for ES 3.00. (Verified against
    /// the mgfxc cube golden, which emits <c>textureCube(ps_s1, …)</c>.)
    /// </summary>
    private static string TextureBuiltinForDimension(MonoGameSamplerDimension dim) => dim switch
    {
        MonoGameSamplerDimension.TextureCube   => "textureCube",
        MonoGameSamplerDimension.TextureVolume => "texture3D",
        _                                      => "texture2D",
    };

    // A `uniform sampler<KIND> <id>;` declaration whose KIND is one the rewriter does
    // NOT model. As of Phase 34 it models sampler2D, samplerCube and sampler3D, so the
    // negative lookahead lets those three through and catches the rest (sampler2DArray,
    // sampler2DShadow, samplerCubeArray, …).
    private static readonly Regex NonPlain2DSamplerDecl = new(
        @"^\s*uniform\s+(?:[a-z]+\s+)?sampler(?!2D\s|Cube\s|3D\s)([A-Za-z0-9]+)\s+[A-Za-z_][A-Za-z0-9_]*\s*;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Throws <see cref="MonoGameGlslRewriteException"/> if any sampler declaration uses
    /// a kind the MojoShader rewrite does not model (anything other than
    /// <c>sampler2D</c>/<c>samplerCube</c>/<c>sampler3D</c>). See the call site for why
    /// an unmodelled sampler would otherwise produce silently-broken GLSL.
    /// </summary>
    private static void ThrowIfUnsupportedSamplerType(string glsl)
    {
        Match m = NonPlain2DSamplerDecl.Match(glsl);
        if (m.Success)
        {
            string kind = "sampler" + m.Groups[1].Value;
            throw new MonoGameGlslRewriteException(
                $"Unsupported sampler type for the MonoGame/KNI GL target: '{kind}'. The MojoShader-" +
                $"dialect rewrite models 'sampler2D', 'samplerCube' and 'sampler3D'; a '{kind}' would " +
                $"be emitted as silently-broken GLSL (e.g. texture2D() on an unmodelled sampler) that " +
                $"fails at GL link time. Use a Texture2D/TextureCube/Texture3D, or extend the rewriter.");
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
        // identifier looks like "in_var_TEXCOORD0" (PS input) or "out_var_COLOR0"
        // (VS output). Strip either interface prefix to the bare HLSL semantic so a
        // VS output and the PS input it feeds resolve to the SAME varying name (the
        // basis of MonoGame's name-based VS→PS link).
        var sem = StripInterfacePrefix(identifier);

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

    private static string StripInterfacePrefix(string identifier)
    {
        const string inPrefix = "in_var_";
        const string outPrefix = "out_var_";
        if (identifier.StartsWith(inPrefix)) return identifier[inPrefix.Length..];
        if (identifier.StartsWith(outPrefix)) return identifier[outPrefix.Length..];
        return identifier;
    }

    /// <summary>
    /// Maps a vertex-input semantic (<c>in_var_&lt;SEM&gt;</c>) to MonoGame's
    /// <c>VertexElementUsage</c> byte + semantic index, as the .mgfx attribute table
    /// needs. Covers the SpriteBatch-compatible set this phase targets —
    /// POSITION / COLOR / TEXCOORD / NORMAL — and is verified against the mgfxc VS
    /// goldens (SpriteEffect: vs_v0=Position/0, vs_v1=Color/0, vs_v2=TexCoord/0;
    /// DualTexture: TEXCOORD1 → usage 2 index 1). The byte values are MonoGame's
    /// <c>VertexElementUsage</c> enum: Position=0, Color=1, TextureCoordinate=2,
    /// Normal=3.
    /// </summary>
    private static (byte Usage, byte Index) SemanticToVertexUsage(string identifier)
    {
        var sem = StripInterfacePrefix(identifier);

        if (sem.StartsWith("POSITION"))
            return (0, ParseTrailingIndex(sem, "POSITION"));
        if (sem.StartsWith("COLOR"))
            return (1, ParseTrailingIndex(sem, "COLOR"));
        if (sem.StartsWith("TEXCOORD"))
            return (2, ParseTrailingIndex(sem, "TEXCOORD"));
        if (sem.StartsWith("NORMAL"))
            return (3, ParseTrailingIndex(sem, "NORMAL"));

        // Unknown semantic: a real effect using an attribute the table doesn't model
        // would bind to the wrong vertex element (silent, not a link error). Fail
        // loudly so it's caught at compile time, consistent with the sampler guard.
        throw new MonoGameGlslRewriteException(
            $"Unsupported vertex-input semantic '{sem}' for the MonoGame GL target. The " +
            $"attribute table models POSITION / COLOR / TEXCOORD / NORMAL; extend " +
            $"SemanticToVertexUsage to support '{sem}'.");
    }

    private static byte ParseTrailingIndex(string sem, string baseName)
    {
        var tail = sem[baseName.Length..];
        return tail.Length == 0 ? (byte)0 : (byte)int.Parse(tail);
    }

    /// <summary>
    /// Replaces uses of a VS OUTPUT identifier (<c>out_var_&lt;SEM&gt;</c>) with its
    /// legacy varying name. The varying is DECLARED <c>vec4</c> but the source output
    /// may be narrower (a <c>vec2</c> TEXCOORD), so a width-matching swizzle is appended
    /// to the assignment target — <c>vTexCoord0.xy = …;</c> — matching the mgfxc golden
    /// (<c>vs_oT0.xy = vs_v2.xy;</c>). The trailing-'.' exception means a use that
    /// already carries an explicit swizzle keeps it (only the rename applies).
    /// </summary>
    private static string ReplaceOutputVaryingUses(string input, InputVarying varying)
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
