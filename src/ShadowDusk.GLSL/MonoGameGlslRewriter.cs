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
/// One uniform-block member modelled into the shader's single
/// <c>{vs,ps}_uniforms_vec4[]</c> register space (Phase 43 F4/F5/F6). The pipeline
/// builds the per-shader .mgfx constant-buffer record DIRECTLY from this layout, so
/// the record's offsets are guaranteed to agree with the indices the emitted GLSL
/// reads — they come from the same allocation.
/// </summary>
/// <param name="Name">The HLSL variable name (== the effect parameter name).</param>
/// <param name="BaseRegister">First 16-byte register the member occupies.</param>
/// <param name="RegisterCount">
/// Registers occupied: 1 per <c>float/vec2/vec3/vec4</c>, 4 per <c>mat4</c>,
/// multiplied by the array element count for array members.
/// </param>
public sealed record MonoGameGlslUniform(
    string Name,
    int    BaseRegister,
    int    RegisterCount);

/// <summary>
/// Result of <see cref="MonoGameGlslRewriter.Rewrite"/>.
/// </summary>
/// <param name="Glsl">The rewritten legacy GLSL source.</param>
/// <param name="Samplers">Samplers in declaration order, renamed to <c>ps_s{k}</c> (pixel stage only).</param>
/// <param name="UniformRegisterCount">
/// 0 if there was no uniform block; otherwise the number of
/// <c>ps_uniforms_vec4[]</c>/<c>vs_uniforms_vec4[]</c> registers (one per member, a
/// <c>mat4</c> counting as four, an array counting once per element).
/// </param>
/// <param name="Attributes">
/// Vertex-input attributes in declaration order, renamed to <c>vs_v{k}</c> (vertex
/// stage only; empty for pixel shaders).
/// </param>
/// <param name="Uniforms">
/// The register layout of every uniform-block member folded into
/// <c>{vs,ps}_uniforms_vec4[]</c>, in allocation order across ALL blocks. Empty when
/// the shader has no uniform block.
/// </param>
public sealed record MonoGameGlslResult(
    string Glsl,
    IReadOnlyList<MonoGameGlslSampler> Samplers,
    int UniformRegisterCount,
    IReadOnlyList<MonoGameGlslAttribute> Attributes,
    IReadOnlyList<MonoGameGlslUniform> Uniforms)
{
    /// <summary>Back-compat constructor: pixel-stage results carry no attributes.</summary>
    public MonoGameGlslResult(
        string Glsl,
        IReadOnlyList<MonoGameGlslSampler> Samplers,
        int UniformRegisterCount)
        : this(Glsl, Samplers, UniformRegisterCount,
               Array.Empty<MonoGameGlslAttribute>(), Array.Empty<MonoGameGlslUniform>())
    {
    }

    /// <summary>Back-compat constructor: results without an explicit uniform layout.</summary>
    public MonoGameGlslResult(
        string Glsl,
        IReadOnlyList<MonoGameGlslSampler> Samplers,
        int UniformRegisterCount,
        IReadOnlyList<MonoGameGlslAttribute> Attributes)
        : this(Glsl, Samplers, UniformRegisterCount, Attributes, Array.Empty<MonoGameGlslUniform>())
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

    // layout(binding = N, std140) uniform <TypeName> — ANY uniform block DXC emits
    // (type_Globals for loose globals, type_<Name> for a named cbuffer). All blocks
    // of a stage are merged into the ONE {vs,ps}_uniforms_vec4[] register space
    // (Phase 43 F4/F5 — MojoShader's model: D3D9 has a single float-constant
    // register file per stage, so mgfxc's output never has more than one).
    private static readonly Regex UniformBlockHeader = new(
        @"^\s*layout\s*\(\s*binding\s*=\s*\d+\s*,\s*std140\s*\)\s*uniform\s+([A-Za-z_][A-Za-z0-9_]*)\s*$",
        RegexOptions.Compiled);

    // <type> <member>;  or  <type> <member>[N];  (Phase 43 F6: array members are
    // modelled — N consecutive element strides). Anything else inside a uniform
    // block (int/bool/mat3/struct/layout-qualified members) FAILS LOUDLY in
    // ThrowUnmodeledUniformMember instead of shipping GLSL that still references
    // the deleted block.
    private static readonly Regex UniformMember = new(
        @"^\s*(float|vec2|vec3|vec4|mat4)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:\[\s*([0-9]+)\s*\])?\s*;\s*$",
        RegexOptions.Compiled);

    // } <Instance>;  — the close of a uniform block, capturing the instance name the
    // body's member uses are qualified with (`_Globals` for loose globals, the
    // cbuffer's own name otherwise).
    private static readonly Regex UniformBlockClose = new(
        @"^\s*\}\s*([A-Za-z_][A-Za-z0-9_]*)\s*;\s*$",
        RegexOptions.Compiled);

    private sealed record InputVarying(string Identifier, string Type, string VaryingName);

    /// <summary>One parsed uniform block: its instance name and members in order.</summary>
    private sealed record UniformBlock(
        string Instance,
        List<(string Type, string Member, int Elements)> Members);

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

        // NOTE: the legacy Slang-normalization pre-pass was REMOVED here (Phase 43
        // F5/F11): the browser path runs the same faithful pinned DXC→WASM frontend
        // as desktop (see JsShaderBackends/DxcInterop), so no Slang-shaped GLSL can
        // reach this rewriter, and the pre-pass's accidental UBO-rename branch was
        // exactly what made a second cbuffer ship as raw invalid GLSL. Named cbuffer
        // blocks are now parsed directly (UniformBlockHeader matches any block).

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
        var uniformBlocks = new List<UniformBlock>();           // ALL std140 blocks, in order
        int uniformDeclInsertIndex = -1;                        // where the merged decl goes

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

            // Rule 7: uniform block header — ANY std140 block (type_Globals or a
            // named cbuffer's type_<Name>). All blocks merge into the single
            // {vs,ps}_uniforms_vec4[] register space (Phase 43 F4/F5); the combined
            // declaration is inserted at the FIRST block's position once every
            // block has been parsed (the total register count isn't known yet).
            if (UniformBlockHeader.IsMatch(line))
            {
                // Consume: header, optional '{', members..., '} <Instance>;'
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
                var members = new List<(string Type, string Member, int Elements)>();
                while (j < lines.Length && !lines[j].TrimStart().StartsWith("}"))
                {
                    if (lines[j].Trim().Length == 0)
                    {
                        j++;
                        continue;
                    }
                    var m = UniformMember.Match(lines[j]);
                    if (!m.Success)
                    {
                        // A member shape the MojoShader-dialect model doesn't cover.
                        // The OLD behaviour silently skipped it, leaving the body's
                        // `<Instance>.<member>` use referencing a deleted block —
                        // invalid GLSL with exit code 0 (Phase 43 F6). Fail loudly.
                        ThrowUnmodeledUniformMember(lines[j]);
                    }
                    int elements = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                    members.Add((m.Groups[1].Value, m.Groups[2].Value, elements));
                    j++;
                }
                // j points at the '} <Instance>;' line — capture the instance name
                // the body qualifies member uses with, then skip it.
                Match closeMatch = j < lines.Length ? UniformBlockClose.Match(lines[j]) : Match.Empty;
                if (!closeMatch.Success)
                {
                    throw new MonoGameGlslRewriteException(
                        "GLSL rewrite: uniform block has no parseable '} <instance>;' close — " +
                        "cannot determine the block's instance name.");
                }
                string instance = closeMatch.Groups[1].Value;
                j++;

                uniformBlocks.Add(new UniformBlock(instance, members));
                if (uniformDeclInsertIndex < 0)
                {
                    uniformDeclInsertIndex = output.Count;
                }
                i = j - 1; // loop i++ moves past consumed block
                continue;
            }

            // Rule 3: sampler declaration (sampler2D / samplerCube / sampler3D).
            //
            // Pixel stage only — and a VERTEX-stage sampler FAILS LOUDLY (Phase 43
            // F8). This is deliberate, not a missing feature: MonoGame 3.8.2's GL
            // runtime cannot bind a vertex texture at all. ShaderProgramCache.Link
            // calls ONLY pixelShader.ApplySamplerTextureUnits(program) (the vertex
            // shader's sampler records never get a texture unit assigned), and
            // GraphicsDevice.OpenGL.cs has no VertexTextures/VertexSamplerStates
            // apply path. So ANY emitted form — vs_s{k} contract or not — leaves
            // the VS sampler reading texture unit 0's incidental contents: silently
            // wrong output in the real runtime, the exact failure mode this
            // project's purpose forbids. Until/unless the runtime gap is solved,
            // the only honest output is a loud compile error (surfaces as SD0210).
            var samplerMatch = SamplerDecl.Match(line);
            if (samplerMatch.Success && isVertex)
            {
                throw new MonoGameGlslRewriteException(
                    "Vertex-stage texture sampling is not supported for the MonoGame OpenGL " +
                    "target: MonoGame 3.8.2's GL runtime never assigns texture units to " +
                    "VERTEX-shader samplers (ShaderProgramCache.Link applies only the pixel " +
                    "shader's sampler records, and there is no GL VertexTextures path), so any " +
                    "compiled output would silently sample the wrong texture at runtime. " +
                    "Move the texture fetch to the pixel stage (or pass the data via a uniform).");
            }
            if (samplerMatch.Success)
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

        // The merged declaration goes where the FIRST block sat. The register count
        // is NOT the member count: a mat4 occupies FOUR consecutive 16-byte
        // registers and an array occupies its element stride times its element
        // count — matching the .mgfx cbuffer record the pipeline derives from the
        // SAME layout, so the GLSL index always lands on the right bytes.
        int totalRegisters = RegisterCount(uniformBlocks);
        if (uniformDeclInsertIndex >= 0)
        {
            output.Insert(uniformDeclInsertIndex,
                $"uniform vec4 {regPrefix}_uniforms_vec4[{totalRegisters}];");
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

        // Uniform members: <Instance>.<member> -> {prefix}_uniforms_vec4[reg]<swizzle>
        // (array members: <Instance>.<member>[idx] -> the packed [base + idx] form).
        // The register OFFSET is the running register total across ALL blocks in
        // declaration order, so a mat4 (4 registers) or an array (stride × count)
        // correctly shifts every member after it — the exact same packing the
        // pipeline writes into the .mgfx cbuffer record (it consumes THIS layout),
        // so the GLSL index lands on the right bytes.
        var uniformLayout = new List<MonoGameGlslUniform>();
        int reg = 0;
        foreach (UniformBlock block in uniformBlocks)
        {
            foreach (var (type, member, elements) in block.Members)
            {
                int perElement = type == "mat4" ? 4 : 1;
                int registers  = perElement * Math.Max(1, elements);
                if (elements > 0)
                {
                    // Array member (Phase 43 F6): every use must be an indexed
                    // access — rewritten to the packed register form, literal
                    // indices folded. A whole-array use (no index) cannot be
                    // expressed against the packed array and fails loudly inside.
                    body = RewriteArrayMemberUses(body, block.Instance, member, type, reg, perElement, regPrefix);
                }
                else
                {
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
                    }
                    else
                    {
                        var swizzle = SwizzleForType(type);
                        replacement = $"{regPrefix}_uniforms_vec4[{reg}]{swizzle}";
                    }

                    // Match "<Instance>.<member>" with a word boundary after the member.
                    var pattern = $@"\b{Regex.Escape(block.Instance)}\.{Regex.Escape(member)}\b";
                    body = Regex.Replace(body, pattern, replacement.Replace("$", "$$"));
                }

                uniformLayout.Add(new MonoGameGlslUniform(member, reg, registers));
                reg += registers;
            }
        }

        // Leftover-instance guard: ANY surviving reference to a block instance means
        // a use shape the rewrites above didn't cover — the deleted block would be
        // referenced by the emitted GLSL (invalid; fails only at Effect-load time).
        // Fail loudly at compile time instead (Phase 43 F5/F6).
        foreach (UniformBlock block in uniformBlocks)
        {
            if (Regex.IsMatch(body, $@"\b{Regex.Escape(block.Instance)}\b"))
            {
                throw new MonoGameGlslRewriteException(
                    $"GLSL rewrite: a reference to uniform block instance '{block.Instance}' " +
                    $"survived the member rewrite — the use shape is not modelled by the " +
                    $"MojoShader-dialect lowering, and the emitted GLSL would reference a " +
                    $"deleted block. This is a ShadowDusk gap; please report the shader shape.");
            }
        }

        // Vertex stage: assemble + return now. No fragment-output / texture / round
        // passes — those are pixel-stage rules. The precision header for a VS uses
        // highp float (matching the mgfxc VS golden, which needs full precision for
        // the position transform) rather than the mediump the PS uses.
        if (isVertex)
        {
            // Phase 43 F3: inject mgfxc/MojoShader's runtime posFixup contract.
            // SPIRV-Cross's FlipVertexY is OFF (see SpirvCrossGlslTranspiler), so the
            // Y-flip is performed at draw time by MonoGame's GL runtime via the
            // `posFixup` uniform it sets on every program that declares one
            // (GraphicsDevice.OpenGL.cs ActivateShaderProgram: y=+1 backbuffer,
            // y=-1 render target, zw = the half-pixel offset when UseHalfPixelOffset).
            body = InjectPosFixup(body);

            var vsTrimmed = body.TrimStart('\n');
            var vsGlsl = VertexPrecisionHeader + "\n" + vsTrimmed;
            if (!vsGlsl.EndsWith("\n"))
            {
                vsGlsl += "\n";
            }
            return new MonoGameGlslResult(vsGlsl, Array.Empty<MonoGameGlslSampler>(), reg, attributes, uniformLayout);
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

        // Rule 6b: LOD / gradient / projected sampling (Phase 43 F7 — dimension-
        // specific legacy names + MojoShader's guarded extension header).
        //
        // Phase 34 left these in SPIRV-Cross's GENERIC spelling (`textureLod` /
        // `textureGrad` / `textureProj`) on the rationale that lenient desktop drivers
        // (NVIDIA) accept them in the legacy no-#version dialect. Mesa's strict GLSL
        // front-end does NOT ("no function with name 'textureLod'", llvmpipe Mesa
        // 22.3.6/25.2.8) — the generic forms only exist from GLSL 1.30 / ES 3.00, so
        // every Linux DesktopGL Effect load failed. The faithful form is MojoShader's
        // (profiles/mojoshader_profile_glsl.c, emit_GLSL_TEXLDL / emit_GLSL_TEXLDD):
        // dimension-specific `texture2DLod` / `textureCubeLod` / `texture3DLod` /
        // `texture2DGrad`, plus `prepend_glsl_texlod_extensions`'s guarded header
        // (ARB_shader_texture_lod, else EXT_gpu_shader4, else degrade to a plain
        // texture call — never a compile failure). For KNI HiDef/WebGL2 the header's
        // leading `#if __VERSION__ >= 300` branch maps the legacy names back to the
        // generic ES-3.00 builtins (MojoShader's own GLSLES3 preflight does exactly
        // this: `#define texture2DLod textureLod` …), so the ONE emitted artifact
        // still serves Reach AND HiDef (the Phase 33 promise).
        bool needsTexLodHeader = false;
        foreach (var sampler in samplers)
        {
            string samplerPattern = Regex.Escape(sampler.Name);

            // textureLod(ps_sK, …) -> texture{2D,Cube,3D}Lod(ps_sK, …).
            string lodBuiltin = sampler.Dimension switch
            {
                MonoGameSamplerDimension.TextureCube   => "textureCubeLod",
                MonoGameSamplerDimension.TextureVolume => "texture3DLod",
                _                                      => "texture2DLod",
            };
            var lodRegex = new Regex($@"\btextureLod\s*\(\s*{samplerPattern}\b");
            if (lodRegex.IsMatch(body))
            {
                body = lodRegex.Replace(body, $"{lodBuiltin}({sampler.Name}");
                needsTexLodHeader = true;
            }

            // textureGrad(ps_sK, …) -> texture2DGrad(ps_sK, …). Only the 2D form has
            // a legacy spelling any GLSL profile defines (ARB names the new fragment
            // built-ins texture2DGradARB etc. — the header maps it); MojoShader's own
            // cube/3D grad output (`textureCubeGrad`) is a name NO GLSL or extension
            // declares, so a cube/3D gradient sample fails loudly instead of shipping
            // GLSL that can never link.
            var gradRegex = new Regex($@"\btextureGrad\s*\(\s*{samplerPattern}\b");
            if (gradRegex.IsMatch(body))
            {
                if (sampler.Dimension != MonoGameSamplerDimension.Texture2D)
                {
                    throw new MonoGameGlslRewriteException(
                        $"Gradient sampling (SampleGrad/tex2Dgrad) on a " +
                        $"{(sampler.Dimension == MonoGameSamplerDimension.TextureCube ? "cube" : "3D")} " +
                        $"sampler has no legacy-GLSL spelling MonoGame's GL dialect can express " +
                        $"(only texture2DGrad exists via GL_ARB_shader_texture_lod / " +
                        $"GL_EXT_gpu_shader4). Use a 2D gradient sample, or an explicit-LOD " +
                        $"sample (SampleLevel), which supports all dimensions.");
                }
                body = gradRegex.Replace(body, $"texture2DGrad({sampler.Name}");
                needsTexLodHeader = true;
            }

            // textureProj(ps_sK, …) -> texture{2D,3D}Proj(ps_sK, …) (core GLSL 1.10;
            // no cube proj exists in any GLSL). The header's ES-3.00 branch maps the
            // legacy spelling back for KNI HiDef.
            var projRegex = new Regex($@"\btextureProj\s*\(\s*{samplerPattern}\b");
            if (projRegex.IsMatch(body))
            {
                if (sampler.Dimension == MonoGameSamplerDimension.TextureCube)
                {
                    throw new MonoGameGlslRewriteException(
                        "Projected sampling on a cube sampler is not expressible in GLSL " +
                        "(no textureCubeProj builtin exists in any profile).");
                }
                string projBuiltin = sampler.Dimension == MonoGameSamplerDimension.TextureVolume
                    ? "texture3DProj" : "texture2DProj";
                body = projRegex.Replace(body, $"{projBuiltin}({sampler.Name}");
                needsTexLodHeader = true;
            }
        }

        // Defensive: any remaining generic LOD/grad/proj call not bound to a modelled
        // sampler (should not occur — every sampler decl is modelled or guarded) falls
        // back to the 2D legacy form, mirroring the bare `texture(` fallback above.
        if (Regex.IsMatch(body, @"\btexture(Lod|Grad|Proj)\s*\("))
        {
            body = Regex.Replace(body, @"\btextureLod\s*\(",  "texture2DLod(");
            body = Regex.Replace(body, @"\btextureGrad\s*\(", "texture2DGrad(");
            body = Regex.Replace(body, @"\btextureProj\s*\(", "texture2DProj(");
            needsTexLodHeader = true;
        }

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
        // preserving a single blank line separation. The texlod extension header (when
        // needed) sits between the precision header and the #define block — the same
        // preflight position MojoShader gives prepend_glsl_texlod_extensions' output.
        var trimmedBody = body.TrimStart('\n');
        var finalGlsl = PrecisionHeader
            + (needsTexLodHeader ? TexLodExtensionHeader : "")
            + "\n" + defineBlock + trimmedBody;
        if (!finalGlsl.EndsWith("\n"))
        {
            finalGlsl += "\n";
        }

        return new MonoGameGlslResult(finalGlsl, samplers, reg, Array.Empty<MonoGameGlslAttribute>(), uniformLayout);
    }

    /// <summary>
    /// The number of 16-byte registers the uniform members of ALL blocks occupy: a
    /// <c>mat4</c> spans four, every other member one, an array its element stride
    /// times its element count. This is the <c>{prefix}_uniforms_vec4[]</c> array
    /// length, kept in lockstep with the .mgfx cbuffer packing.
    /// </summary>
    private static int RegisterCount(IReadOnlyList<UniformBlock> blocks)
    {
        int n = 0;
        foreach (UniformBlock block in blocks)
        {
            foreach (var (type, _, elements) in block.Members)
            {
                n += (type == "mat4" ? 4 : 1) * Math.Max(1, elements);
            }
        }
        return n;
    }

    /// <summary>
    /// Rewrites every indexed use of an ARRAY uniform-block member —
    /// <c><paramref name="instance"/>.<paramref name="member"/>[idx]</c> — into the
    /// packed register form (Phase 43 F6):
    /// <list type="bullet">
    ///   <item>vec types: <c>{prefix}_uniforms_vec4[base + (idx)]&lt;swizzle&gt;</c>
    ///   (element stride is one register — exactly how MonoGame's
    ///   <c>ConstantBuffer.SetParameter</c> advances 16 bytes per written row, and how
    ///   D3D9/MojoShader packs float-register arrays);</item>
    ///   <item><c>mat4</c>: stride four — reconstructed column-by-column as
    ///   <c>mat4(P[base+(idx)*4], …, P[base+(idx)*4+3])</c> (MonoGame writes a Matrix
    ///   element as 4 sequential registers, the proven non-array mat4 model).</item>
    /// </list>
    /// Literal indices are folded to a plain register number. A use WITHOUT an index
    /// (whole-array reference) cannot be expressed against the packed array and fails
    /// loudly.
    /// </summary>
    private static string RewriteArrayMemberUses(
        string body, string instance, string member, string type,
        int baseRegister, int perElement, string regPrefix)
    {
        string token = $"{instance}.{member}";
        var sb = new StringBuilder(body.Length);
        int pos = 0;
        while (true)
        {
            int idx = body.IndexOf(token, pos, StringComparison.Ordinal);
            if (idx < 0)
            {
                sb.Append(body, pos, body.Length - pos);
                break;
            }

            int afterToken = idx + token.Length;
            bool boundaryBefore = idx == 0 || !IsIdentChar(body[idx - 1]);
            bool boundaryAfter  = afterToken >= body.Length || !IsIdentChar(body[afterToken]);
            if (!boundaryBefore || !boundaryAfter)
            {
                sb.Append(body, pos, afterToken - pos);
                pos = afterToken;
                continue;
            }

            // Skip whitespace to the expected '['.
            int k = afterToken;
            while (k < body.Length && (body[k] == ' ' || body[k] == '\t'))
            {
                k++;
            }
            if (k >= body.Length || body[k] != '[')
            {
                throw new MonoGameGlslRewriteException(
                    $"GLSL rewrite: array uniform '{member}' is referenced without an index " +
                    $"(a whole-array use). The MojoShader-dialect lowering packs the array " +
                    $"into {regPrefix}_uniforms_vec4[] registers, so only indexed element " +
                    $"accesses can be rewritten. Index the array per element instead.");
            }

            int close = FindMatchingBracket(body, k);
            if (close < 0)
            {
                throw new MonoGameGlslRewriteException(
                    $"GLSL rewrite: unbalanced '[' in an indexed use of array uniform '{member}'.");
            }

            string indexExpr = body.Substring(k + 1, close - k - 1).Trim();
            string replacement = BuildArrayElementExpression(
                type, indexExpr, baseRegister, perElement, regPrefix);

            sb.Append(body, pos, idx - pos);
            sb.Append(replacement);
            pos = close + 1;
        }

        return sb.ToString();
    }

    /// <summary>
    /// The packed-register expression for one array element access. A literal index
    /// folds to a constant register; a dynamic index keeps the arithmetic in GLSL
    /// (valid in every profile — MojoShader emits the same relative-addressed form
    /// for D3D9 <c>a0</c> indexing).
    /// </summary>
    private static string BuildArrayElementExpression(
        string type, string indexExpr, int baseRegister, int perElement, string regPrefix)
    {
        string p = $"{regPrefix}_uniforms_vec4";
        bool literal = int.TryParse(indexExpr, out int literalIndex);

        if (type == "mat4")
        {
            if (literal)
            {
                int r = baseRegister + literalIndex * 4;
                return $"mat4({p}[{r}], {p}[{r + 1}], {p}[{r + 2}], {p}[{r + 3}])";
            }
            string b = $"{baseRegister} + ({indexExpr}) * 4";
            return $"mat4({p}[{b}], {p}[{b} + 1], {p}[{b} + 2], {p}[{b} + 3])";
        }

        string swizzle = SwizzleForType(type);
        return literal
            ? $"{p}[{baseRegister + literalIndex}]{swizzle}"
            : $"{p}[{baseRegister} + ({indexExpr})]{swizzle}";
    }

    /// <summary>
    /// Given the index of an opening '[' in <paramref name="body"/>, returns the index
    /// of its matching ']', or -1 if unbalanced.
    /// </summary>
    private static int FindMatchingBracket(string body, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
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

    // GLSL scalar/vector/matrix type keywords the uniform-block model does NOT
    // cover, used only to give the loud failure a precise diagnosis.
    private static readonly Regex UnmodeledMemberTypeProbe = new(
        @"^\s*(?:layout\s*\([^)]*\)\s*)?(int|uint|bool|ivec[234]|uvec[234]|bvec[234]|mat2(?:x[234])?|mat3(?:x[234])?|mat4x[23])\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Fails loudly (Phase 43 F6) for a uniform-block member line the model does not
    /// cover. Before Phase 43C such members were SILENTLY DROPPED: the block was
    /// deleted but the body still referenced <c>_Globals.&lt;member&gt;</c> — invalid
    /// GLSL that compiled with exit code 0 and failed only inside the consumer's
    /// game at Effect-load time.
    /// </summary>
    private static void ThrowUnmodeledUniformMember(string memberLine)
    {
        string trimmed = memberLine.Trim();
        Match probe = UnmodeledMemberTypeProbe.Match(memberLine);
        if (probe.Success)
        {
            string t = probe.Groups[1].Value;
            if (t is "int" or "uint" or "bool" || t.StartsWith("ivec") || t.StartsWith("uvec") || t.StartsWith("bvec"))
            {
                throw new MonoGameGlslRewriteException(
                    $"Unsupported uniform type in '{trimmed}': integer/boolean uniforms are not " +
                    $"modelled for the MonoGame OpenGL target (MojoShader places them in the " +
                    $"separate {{vs,ps}}_uniforms_ivec4/_bool register sets, which ShadowDusk " +
                    $"does not emit yet). Use a float-typed uniform and cast in the shader.");
            }
            throw new MonoGameGlslRewriteException(
                $"Unsupported uniform type in '{trimmed}': only float/float2/float3/float4 " +
                $"and square float4x4 matrices (plus arrays of those) are modelled for the " +
                $"MonoGame OpenGL target. Pad the matrix to float4x4 or split it into vectors.");
        }

        throw new MonoGameGlslRewriteException(
            $"Unsupported uniform-block member for the MonoGame OpenGL target: '{trimmed}'. " +
            $"The MojoShader-dialect lowering models float/vec2/vec3/vec4/mat4 members and " +
            $"arrays of those; this member would otherwise be silently dropped, leaving the " +
            $"emitted GLSL referencing a deleted uniform block.");
    }

    // The vertex stage uses highp float (the position transform needs full precision);
    // the mgfxc VS golden does exactly this. The pixel stage stays at mediump
    // (PrecisionHeader) to match mgfxc's PS output.
    private const string VertexPrecisionHeader =
        "#ifdef GL_ES\n" +
        "precision highp float;\n" +
        "precision mediump int;\n" +
        "#endif\n";

    // Phase 43 F7 — the guarded extension header for explicit-LOD / gradient /
    // projected sampling, prepended only when Rule 6b rewrote such a call. This is
    // MojoShader's prepend_glsl_texlod_extensions block (mojoshader_profile_glsl.c)
    // composed with its GLSLES3 preflight defines, so ONE artifact serves every
    // profile (the Phase 33 one-artifact-two-profiles promise):
    //
    //   • `#if __VERSION__ >= 300` — KNI's HiDef/WebGL2 converter prepends
    //     `#version 300 es`, making this branch active there (and ONLY there:
    //     versionless desktop GLSL is __VERSION__ 110, WebGL1 is 100). It maps the
    //     legacy names back to the generic core-ES-3.00 builtins, exactly
    //     MojoShader's own GLSLES3 profile header (`#define texture2DLod textureLod`,
    //     `#define texture2DGrad textureGrad`, `#define texture2DProj textureProj`…).
    //   • `GL_ARB_shader_texture_lod` — makes the unsuffixed texture*Lod names valid
    //     in fragment shaders and adds the Grad functions under ARB-suffixed names
    //     (hence `#define texture2DGrad texture2DGradARB`). Mesa supports this
    //     extension, which is what fixes the Linux/Mesa Effect-load failure.
    //   • `GL_EXT_gpu_shader4` — same effect, unsuffixed names.
    //   • `#else` — graceful degrade to a plain texture call (the mip the driver
    //     picks), NEVER a compile failure; extended past MojoShader's pixel-only
    //     texture2DLod fallback with cube/3D equivalents so no emitted name is ever
    //     left undefined.
    //
    // Deviation from MojoShader (deliberate): the extension tests use
    // `defined(GL_…)` instead of MojoShader's bare `#if GL_…` — GLSL ES 1.00 (§3.4,
    // WebGL1/Reach) makes an UNDEFINED identifier in #if/#elif a compile ERROR
    // (desktop GLSL defaults it to 0), so the bare form would turn the Reach
    // degrade path into a compile failure. `defined()` is legal and equivalent
    // everywhere (extension macros are defined to 1 when supported).
    private const string TexLodExtensionHeader =
        "#if __VERSION__ >= 300\n" +
        "#define texture2DLod textureLod\n" +
        "#define textureCubeLod textureLod\n" +
        "#define texture3DLod textureLod\n" +
        "#define texture2DGrad textureGrad\n" +
        "#define texture2DProj textureProj\n" +
        "#define texture3DProj textureProj\n" +
        "#elif defined(GL_ARB_shader_texture_lod)\n" +
        "#extension GL_ARB_shader_texture_lod : enable\n" +
        "#define texture2DGrad texture2DGradARB\n" +
        "#define texture2DProjGrad texture2DProjARB\n" +
        "#elif defined(GL_EXT_gpu_shader4)\n" +
        "#extension GL_EXT_gpu_shader4 : enable\n" +
        "#else\n" +
        "#define texture2DGrad(a,b,c,d) texture2D(a,b)\n" +
        "#define texture2DProjGrad(a,b,c,d) texture2DProj(a,b)\n" +
        "#define texture2DLod(a,b,c) texture2D(a,b)\n" +
        "#define textureCubeLod(a,b,c) textureCube(a,b)\n" +
        "#define texture3DLod(a,b,c) texture3D(a,b)\n" +
        "#endif\n";

    // The SPIRV-Cross depth-convention fixup line (FixupDepthConvention option), used
    // as the insertion anchor so the posFixup lines land in mgfxc's order (Y-flip,
    // half-pixel, THEN depth). NOTE the factor order: SPIRV-Cross spells it
    // `2.0 * gl_Position.z` where the mgfxc golden spells `gl_Position.z * 2.0` —
    // mathematically identical, kept as SPIRV-Cross emits it.
    private const string SpirvCrossDepthFixupLine = "gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;";

    // mgfxc/MojoShader's two posFixup lines, byte-for-byte the form in the OpenGL
    // golden VsTransformColorTexture.mgfx VS (and in MonoGame's own
    // GraphicsDevice.OpenGL.cs comment describing what it appends):
    //   gl_Position.y = gl_Position.y * posFixup.y;
    //   gl_Position.xy += posFixup.zw * gl_Position.ww;
    private const string PosFixupLine1 = "gl_Position.y = gl_Position.y * posFixup.y;";
    private const string PosFixupLine2 = "gl_Position.xy += posFixup.zw * gl_Position.ww;";

    /// <summary>
    /// Injects the mgfxc/MojoShader <c>posFixup</c> contract into a rewritten VERTEX
    /// body: declares <c>uniform vec4 posFixup;</c> (after the
    /// <c>vs_uniforms_vec4[]</c> declaration when present, matching the golden's
    /// declaration order) and appends the two fixup lines at the end of
    /// <c>main()</c> — before the SPIRV-Cross depth-convention line when present, so
    /// the line order matches the golden (Y-flip, half-pixel, depth).
    ///
    /// <para>MonoGame's GL runtime sets the uniform at draw time
    /// (<c>posFixup.y</c> = +1 backbuffer / -1 render target; <c>.zw</c> = the
    /// half-pixel offset when <c>UseHalfPixelOffset</c>) and skips programs that do
    /// not declare it — so a VS that never writes <c>gl_Position</c> is returned
    /// unchanged.</para>
    /// </summary>
    /// <exception cref="MonoGameGlslRewriteException">
    /// The body already contains a <c>posFixup</c> identifier (would be silently
    /// shadowed / double-applied) — fail loudly rather than emit ambiguous GLSL.
    /// </exception>
    private static string InjectPosFixup(string body)
    {
        // No position output => nothing for the runtime fixup to act on (and MonoGame
        // skips the upload when the uniform is absent — same contract).
        if (!Regex.IsMatch(body, @"\bgl_Position\b"))
        {
            return body;
        }

        if (Regex.IsMatch(body, @"\bposFixup\b"))
        {
            throw new MonoGameGlslRewriteException(
                "GLSL rewrite collision: source already contains identifier 'posFixup', " +
                "which clashes with the MojoShader position-fixup uniform. Cannot safely rewrite.");
        }

        var lines = body.Split('\n').ToList();

        // ---- Declaration: after `uniform vec4 vs_uniforms_vec4[N];` when present
        // (the golden's order), else before the first line of the body. ----
        int declAnchor = lines.FindIndex(l =>
            Regex.IsMatch(l, @"^\s*uniform\s+vec4\s+vs_uniforms_vec4\[\d+\]\s*;\s*$"));
        lines.Insert(declAnchor >= 0 ? declAnchor + 1 : 0, "uniform vec4 posFixup;");

        // ---- Fixup lines: immediately before the depth-convention line (mgfxc's
        // order: Y-flip, half-pixel, depth). SPIRV-Cross emits the depth line as the
        // last statement of main() when FixupDepthConvention is on; if it is absent
        // (e.g. a depth-range-neutral shader shape), fall back to the last `}` —
        // the close of main(), which SPIRV-Cross emits as the final function. ----
        int insertAt = lines.FindLastIndex(l => l.Trim() == SpirvCrossDepthFixupLine);
        string indent;
        if (insertAt >= 0)
        {
            indent = lines[insertAt][..(lines[insertAt].Length - lines[insertAt].TrimStart().Length)];
        }
        else
        {
            insertAt = lines.FindLastIndex(l => l.Trim() == "}");
            if (insertAt < 0)
            {
                throw new MonoGameGlslRewriteException(
                    "GLSL rewrite: vertex shader writes gl_Position but no insertion point " +
                    "for the posFixup lines was found (no depth-fixup line and no closing brace).");
            }
            indent = "    ";
        }

        lines.Insert(insertAt, indent + PosFixupLine1);
        lines.Insert(insertAt + 1, indent + PosFixupLine2);

        return string.Join("\n", lines);
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

                // FindCallStart allows whitespace between the identifier and '(' — skip
                // it here too, otherwise 'round (x)' would slice the argument off by the
                // whitespace width and emit corrupt GLSL.
                int openParen = callStart + fn.Length;
                while (openParen < body.Length && (body[openParen] == ' ' || body[openParen] == '\t'))
                {
                    openParen++;
                }

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
