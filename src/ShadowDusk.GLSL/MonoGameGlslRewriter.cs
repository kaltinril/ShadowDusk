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
        var positionOutputIds = new List<string>();             // VS: out_var_POSITION{0} -> gl_Position
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
                    var ident = outVaryMatch.Groups[2].Value;

                    // A VS output carrying the legacy D3D9 POSITION/POSITION0 semantic IS the
                    // clip-space position (mgfxc/MojoShader map it to gl_Position). This is the
                    // form the stock MonoGame GL effect template emits — `#define SV_POSITION
                    // POSITION` makes `: SV_POSITION` compile as `: POSITION`. ShadowDusk's
                    // frontend is DXC (Shader Model 6), where ONLY `: SV_Position` is the builtin
                    // position (SPIRV-Cross then emits gl_Position directly, never reaching here);
                    // a `: POSITION` output is just a user varying. Emitting it as such would
                    // leave gl_Position UNWRITTEN — silently-broken geometry. So map the
                    // position-semantic output to gl_Position instead: drop its varying decl and
                    // rewrite its uses to gl_Position in Pass 2 (posFixup then applies as usual).
                    if (IsPositionSemantic(ident))
                    {
                        positionOutputIds.Add(ident);
                        continue;
                    }

                    var type = outVaryMatch.Groups[1].Value;
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

        // VS position output (legacy POSITION/POSITION0 semantic) -> gl_Position. The body's
        // `out_var_POSITION{0} = …` write becomes `gl_Position = …`, so InjectPosFixup (below)
        // finds gl_Position and appends the runtime Y-flip/depth fixup, exactly as for a true
        // SV_Position shader.
        foreach (var id in positionOutputIds)
        {
            body = ReplaceWord(body, id, "gl_Position");
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
                        // A mat4 occupies registers reg..reg+3, reconstructed TRANSPOSED
                        // (registers read as ROWS) — see BuildUploadedMat4 for why issue #70
                        // requires this. The naive mat4(reg, reg+1, reg+2, reg+3) (registers
                        // as COLUMNS) renders geometry transposed/garbled in the real runtime.
                        replacement = BuildUploadedMat4(
                            $"{regPrefix}_uniforms_vec4[{reg}]",
                            $"{regPrefix}_uniforms_vec4[{reg + 1}]",
                            $"{regPrefix}_uniforms_vec4[{reg + 2}]",
                            $"{regPrefix}_uniforms_vec4[{reg + 3}]");
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

        // Vertex stage: assemble + return now. No fragment-output / texture passes —
        // those are pixel-stage rules — but the stage-agnostic body lowerings DO run
        // here (below). The precision header for a VS uses highp float (matching the
        // mgfxc VS golden, which needs full precision for the position transform)
        // rather than the mediump the PS uses.
        if (isVertex)
        {
            // Issue #137: the stage-agnostic body lowerings must run for the vertex
            // stage too. A VS using round() otherwise ships roundEven() — absent from
            // GLSL ES 1.00 (WebGL1 / KNI Reach) and rejected by Mesa's strict
            // versionless-1.10 front end — and a VS with an (inlined) early-return
            // helper ships the raw do{…}while(false) Appendix A forbids: both are
            // silent Effect-load failures with compile exit 0. Rule 9a (the #136
            // unwrap, which turns loop breaks into early `return;`) stays PIXEL-only:
            // InjectPosFixup appends the posFixup lines at end-of-main, and an early
            // return would skip them (the Y-flip / half-pixel contract). The Rule 9b
            // for-loop form has a single fall-through exit, so the fixup always runs.
            // (No derivative ops exist in the vertex stage, so skipping 9a loses no
            // #136 coverage here.)
            body = LowerRoundToFloorHalfUp(body);       // Rule 8  (issue #137)
            body = LowerOneShotDoWhileToForLoop(body);  // Rule 9b (issues #107/#137)
            body = LowerPowSquareToMultiply(body);      // Rule 10 (issue #127)
            body = FoldReciprocalOfQuotient(body);      // Rule 11 (issue #127)
            body = LowerEmptyIncrementForLoop(body);    // Rule 12 (issue #138, shape 2)
            body = LowerBoundedHeaderlessForLoop(body); // Rule 13 (issue #138, shape 1)
            body = LowerTruncToSignFloorAbs(body);      // Rule 15 (Apos.Shapes #34)

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
            ThrowIfUnrewrittenStageIo(vsGlsl);
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

        // Rule 9: eliminate SPIRV-Cross's one-shot `do { … } while(false);` loops (its
        // structured-early-return idiom). SPIRV-Cross renders an early `return` — the
        // entry point's own, or a nested `if` that returns inside an inlined helper — as
        // a single-iteration loop so the `return` can become a `break`. Two problems:
        // GLSL ES 1.00 (WebGL1 / KNI Reach) only *guarantees* the restricted `for`-loop
        // forms of Appendix A, so a do-while FAILS TO LOAD in WebGL (issue #107); and on
        // ANGLE's D3D11 backend (WebGL in every Windows browser) ANY loop with a
        // divergent exit — a conditional `break` OR a conditional `discard` — silently
        // zeroes every gradient op (dFdx/dFdy, and implicit-LOD mip selection) in the
        // loop body (issue #136), so wrapping main's body in a loop kills
        // derivative-based AA with no compile or link error.
        //
        // Rule 9a (#136): when the one-shot loop is a direct child of main's body and is
        // followed only by simple statements (the return-value-phi output writes),
        // UNWRAP it — keep the body as a plain brace block and turn each loop-level
        // `break` into those tail statements + `return;`. Straight-line `main` with
        // conditional early returns is valid in every GLSL profile incl. ESSL 1.00,
        // keeps derivatives alive on ANGLE (its loop restriction no longer applies),
        // and is the exact shape mgfxc/MojoShader emits — strictly more faithful.
        body = UnwrapMainOneShotDoWhile(body);
        // Rule 9b (#107): any one-shot loop 9a could not prove safe to unwrap (nested
        // inside another construct, loop-level `continue`, non-simple tail) falls back
        // to the canonical Appendix-A-allowed `for (int _i = 0; _i < 1; _i++) { B }` —
        // semantically identical to the do-while, so pixels are unchanged, and loads in
        // WebGL1. A genuine multi-iteration do-while — `while(<not false>)` — is left
        // untouched.
        body = LowerOneShotDoWhileToForLoop(body);

        // Rule 10: strength-reduce pow(x, 2.0) to a multiply (issue #127). GLSL defines
        // pow(x, y) only for x > 0 — a negative base is undefined (drivers lowering to
        // exp2(y*log2(x)) return NaN) — while fxc constant-folds pow(x, 2) into a
        // multiply, so HLSL that squares a possibly-negative value via pow() (e.g.
        // Apos.Shapes' LinearGradient squaring normalized-direction components) is
        // well-defined through mgfxc but driver-dependent through a native GLSL pow.
        // The multiply restores the reference compiler's semantics and is exact
        // (correctly-rounded) where pow was merely approximate.
        body = LowerPowSquareToMultiply(body);

        // Rule 11: fold the reciprocal-of-quotient 1.0 / (a / b) to (b / a) — one
        // correctly-rounded division instead of two (issue #127). SPIRV-Cross preserves
        // the HLSL shape `1.0 / (aaSize / length(...))` literally where fxc folds it,
        // leaving an extra rounding step on ShadowDusk's GL path at every such site.
        body = FoldReciprocalOfQuotient(body);

        // Rule 12 (issue #138, shape 2): hoist a for-loop's body-advanced index
        // (`<index>++; continue;` or `<index> += k; continue;` as the body's last two
        // statements) into the header's empty increment clause. GLSL ES 1.00 Appendix A
        // requires the increment in the header and forbids any other write to the index
        // — this shape fails to load on WebGL1/KNI Reach (SD0402) and independently
        // makes `arr[base + index]` a non-constant-index-expression there too. Only
        // rewritten when the index has no other write and no other `continue` exists
        // anywhere else in the body — the same safety bar SD0402's own message asks
        // for — so an unprovable shape is left as a documented warning, not guessed at.
        body = LowerEmptyIncrementForLoop(body);

        // Rule 13 (issue #138, shape 1): give a header-less `for (;;)` a real,
        // Appendix-A-legal bound when the runtime "trip count" variable it guards
        // against is provably a compile-time constant (a literal, or a ternary between
        // two literals) — SPIRV-Cross has just renamed it into a runtime-looking
        // temporary, the shader's true ceiling is still knowable. Rewritten only when
        // that ceiling can be proven and the surrounding shape matches exactly what
        // SPIRV-Cross emits for this idiom; anything else is left for SD0402 to keep
        // warning about.
        body = LowerBoundedHeaderlessForLoop(body);

        // Rule 15 (Apos.Shapes #34): lower trunc() to a legacy-GLSL-valid expression.
        // SPIRV-Cross has no GLSL operator for HLSL's truncating `%`/fmod (OpFRem —
        // sign follows the dividend; GLSL's own mod() is floored and sign-follows-
        // divisor, so it can't be reused), so it inlines the remainder by hand as
        // `a - b * trunc(a / b)`. trunc() is a GLSL 1.30 / GLSL ES 3.00 builtin —
        // absent from the versionless legacy dialect this rewriter targets (Rule 1
        // strips the #version line) — so it loads on lenient desktop drivers but
        // fails as an undeclared identifier on strict GLSL ES 1.00 front ends
        // (ANGLE on macOS DesktopGL). Lowered here, not via a header, since (unlike
        // roundEven/round in Rule 8) there is no builtin name to fall back to.
        body = LowerTruncToSignFloorAbs(body);

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
        // The derivatives extension header (issue #139) goes FIRST, before the
        // precision header — the position mgfxc gives it.
        var trimmedBody = body.TrimStart('\n');
        bool needsDerivativesHeader = DerivativeBuiltinUse.IsMatch(body);
        var finalGlsl = (needsDerivativesHeader ? StandardDerivativesExtensionHeader : "")
            + PrecisionHeader
            + (needsTexLodHeader ? TexLodExtensionHeader : "")
            + "\n" + defineBlock + trimmedBody;
        if (!finalGlsl.EndsWith("\n"))
        {
            finalGlsl += "\n";
        }

        ThrowIfUnrewrittenStageIo(finalGlsl);
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
    /// Reconstructs a <c>float4x4</c> uniform as a GLSL <c>mat4</c> from its four
    /// consecutive <c>{vs,ps}_uniforms_vec4[]</c> registers, <b>transposed</b>: the four
    /// register expressions are taken as the matrix's ROWS (the <c>mat4(col0,col1,col2,col3)</c>
    /// constructor takes COLUMNS, so the rows are spread component-wise across the columns).
    ///
    /// <para><b>Why transposed (issue #70).</b> MonoGame/KNI's
    /// <c>EffectParameter.SetValue(Matrix)</c> uploads each matrix as its COLUMNS — register
    /// <c>k</c> = column <c>k</c> of the authored matrix — which is exactly the layout mgfxc's
    /// GLSL golden reads with <c>result[j] = dot(v, register[j])</c> for HLSL <c>mul(v, M)</c>
    /// (i.e. <c>v * mat4(reg0..reg3)</c>). SPIRV-Cross, however, emits the multiply with the
    /// operands swapped relative to the HLSL (HLSL <c>mul(v, M)</c> → GLSL <c>M * v</c>;
    /// <c>mul(M, v)</c> → <c>v * M</c>), because the row/column-major decoration it carries on
    /// the matrix — which the dialect rewrite then strips when it flattens the UBO into the
    /// flat register array — is what would otherwise keep the result upright. A naive
    /// <c>mat4(reg0..reg3)</c> (registers as COLUMNS) therefore computes <c>M·v</c>, the
    /// TRANSPOSE of the intended <c>v·M</c>, and renders geometry visibly garbled (issue #70's
    /// "exploded cube"). Reconstructing the transpose cancels SPIRV-Cross's operand swap:
    /// <c>Mᵀ * v == v * M == mul(v, M)</c> for the vector-first form, and <c>v * Mᵀ == M * v
    /// == mul(M, v)</c> for the matrix-first form — correct for every mul order, matching the
    /// mgfxc golden behaviourally. <c>transpose()</c> is deliberately NOT used: it does not
    /// exist in GLSL ES 1.00 (KNI Reach / WebGL1) nor versionless desktop GLSL 1.10, so the
    /// transpose is open-coded with swizzles, valid in every profile the one artifact serves.</para>
    /// </summary>
    private static string BuildUploadedMat4(string r0, string r1, string r2, string r3)
        => $"mat4(vec4({r0}.x, {r1}.x, {r2}.x, {r3}.x), " +
           $"vec4({r0}.y, {r1}.y, {r2}.y, {r3}.y), " +
           $"vec4({r0}.z, {r1}.z, {r2}.z, {r3}.z), " +
           $"vec4({r0}.w, {r1}.w, {r2}.w, {r3}.w))";

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
            // Transposed reconstruction (registers as ROWS) — see BuildUploadedMat4
            // for why issue #70 requires this for the matrix to render upright.
            if (literal)
            {
                int r = baseRegister + literalIndex * 4;
                return BuildUploadedMat4($"{p}[{r}]", $"{p}[{r + 1}]", $"{p}[{r + 2}]", $"{p}[{r + 3}]");
            }
            string b = $"{baseRegister} + ({indexExpr}) * 4";
            return BuildUploadedMat4($"{p}[{b}]", $"{p}[{b} + 1]", $"{p}[{b} + 2]", $"{p}[{b} + 3]");
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

    // mgfxc parity (issue #139): MonoGame 3.8.2 mgfxc (ShaderData.mojo.cs) prepends
    // this as the FIRST line of the fragment GLSL whenever the MojoShader output
    // contains dFdx/dFdy. In ESSL 1.00 the derivative built-ins exist only under this
    // extension, so a strict GLES2 compiler (native Android/iOS GL, some Mesa ES
    // paths) rejects a derivative-using fragment shader that lacks the header, at
    // Effect-load time, with compile exit 0 on our side. Our scan also includes
    // fwidth: SPIRV-Cross emits fwidth() directly, which mgfxc's two-token dFdx/dFdy
    // scan never had to handle. Where derivatives are core (ES 3.00 / desktop 1.30+)
    // an enable of this known extension is at most a warning, so the ONE emitted
    // artifact still serves Reach, HiDef, and desktop (the Phase 33 promise).
    private const string StandardDerivativesExtensionHeader =
        "#extension GL_OES_standard_derivatives : enable\n";

    // Matches a use of any derivative builtin in the rewritten fragment body.
    private static readonly Regex DerivativeBuiltinUse = new(
        @"\b(dFdx|dFdy|fwidth)\s*\(",
        RegexOptions.Compiled);

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

        // Slot-0 builtin is MRT-aware (matches mgfxc's goldens, verified):
        //   * SINGLE output  -> slot 0 is gl_FragColor (every single-target golden:
        //     Sepia/Dissolve/AlphaTestEffect/... emit `#define ps_oC0 gl_FragColor`).
        //   * TRUE MRT (2+)   -> slot 0 is gl_FragData[0], like every other slot (the
        //     mgfxc DeferredSprite GL golden emits `#define ps_oC0 gl_FragData[0]` AND
        //     `#define ps_oC1 gl_FragData[1]`). This is render-CORRECTNESS, not cosmetic:
        //     in legacy GLSL with multiple render targets bound, writing gl_FragColor
        //     broadcasts to ALL color attachments and corrupts the other target(s);
        //     gl_FragData[0] writes only attachment 0.
        bool isMrt = slots.Count >= 2;
        var outputs = new List<FragmentOutput>(slots.Count);
        foreach (int slot in slots)
        {
            string builtin = (slot == 0 && !isMrt) ? "gl_FragColor" : $"gl_FragData[{slot}]";
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
    /// True if <paramref name="identifier"/> (e.g. <c>out_var_POSITION</c> /
    /// <c>out_var_POSITION0</c>) carries the D3D9 clip-position semantic. POSITION ≡ POSITION0
    /// is the single VS output position in Shader Model 3; higher indices are not the position.
    /// </summary>
    private static bool IsPositionSemantic(string identifier)
    {
        // HLSL semantics are case-insensitive and DXC mirrors the author's spelling
        // (": Position" => out_var_Position), so normalize before matching — the same
        // treatment OutputDecl already gives SV_Target. Without it a mixed-case
        // position semantic silently became a varying instead of gl_Position and the
        // geometry rendered from garbage (bug-hunt 2026-07-27 M3).
        var sem = StripInterfacePrefix(identifier).ToUpperInvariant();
        return sem is "POSITION" or "POSITION0";
    }

    private static string SemanticToVaryingName(string identifier)
    {
        // identifier looks like "in_var_TEXCOORD0" (PS input) or "out_var_COLOR0"
        // (VS output). Strip either interface prefix to the bare HLSL semantic so a
        // VS output and the PS input it feeds resolve to the SAME varying name (the
        // basis of MonoGame's name-based VS→PS link). Uppercased because HLSL
        // semantics are case-insensitive and DXC mirrors the author's spelling —
        // ": TexCoord0" (VS) and ": TEXCOORD0" (PS) must produce the SAME varying or
        // the GL link fails at Effect load (bug-hunt 2026-07-27 M3).
        var sem = StripInterfacePrefix(identifier).ToUpperInvariant();

        switch (sem)
        {
            // An omitted HLSL semantic index IS index 0: ": COLOR" and ": COLOR0" are the
            // same semantic, and fxc/mgfxc treat them identically. DXC mirrors the author's
            // spelling verbatim, so the bare form arrives as `in_var_COLOR`. Collapsing it
            // here is the same normalization IsPositionSemantic (POSITION ≡ POSITION0),
            // ParseTrailingIndex, and the SV_Target rewrite already perform — without it a
            // pixel-only pass declaring `: COLOR` emits `var_COLOR`, which MonoGame's
            // built-in SpriteEffect VS (writing vFrontColor) can never link against.
            case "COLOR":
            case "COLOR0":
                return "vFrontColor";
            case "COLOR1":
                return "vBackColor";
        }

        if (sem.StartsWith("TEXCOORD"))
        {
            var n = sem["TEXCOORD".Length..];
            if (n.Length == 0)
            {
                n = "0";    // ": TEXCOORD" is TEXCOORD0
            }
            return $"vTexCoord{n}";
        }

        // Unknown semantic — pass through (won't occur in our corpus).
        return $"var_{sem}";
    }

    /// <summary>
    /// Final leak guard (bug-hunt 2026-07-27 M13): the stage-I/O declaration regexes
    /// model unqualified <c>float/vec2/vec3/vec4</c> shapes only, so a declaration they
    /// don't match (a <c>flat</c> interpolant, an int/uint/mat varying, a non-vec4
    /// SV_Target) used to fall through with its modern <c>in</c>/<c>out</c> syntax and
    /// <c>in_var_</c>/<c>out_var_</c> uses intact — GLSL that versionless front ends
    /// reject only at Effect-load time, silently. Any survivor is a rewrite gap: fail
    /// the compile loudly instead, like the uniform-block leftover guard.
    /// </summary>
    private static void ThrowIfUnrewrittenStageIo(string finalGlsl)
    {
        Match survivor = Regex.Match(finalGlsl, @"\b(?:in_var_|out_var_)\w+");
        if (survivor.Success)
        {
            throw new MonoGameGlslRewriteException(
                $"GLSL rewrite: stage interface identifier '{survivor.Value}' survived the " +
                "I/O rewrite — its declaration shape (qualifier or type) is not modelled by " +
                "the MojoShader-dialect lowering, and the emitted GLSL would fail to load " +
                "on the GL runtime. This is a ShadowDusk gap; please report the shader shape.");
        }
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
        // Uppercased: HLSL semantics are case-insensitive and DXC mirrors the author's
        // spelling, so ": position" must map like ": POSITION" (bug-hunt 2026-07-27 M3).
        var sem = StripInterfacePrefix(identifier).ToUpperInvariant();

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
        if (tail.Length == 0)
            return 0;
        // TryParse: a non-numeric tail (e.g. POSITIONT, which shares POSITION's prefix)
        // must surface as the loud unsupported-semantic rewrite error below, not as a raw
        // FormatException that bypasses the pipeline's MonoGameGlslRewriteException catch
        // and escapes the library as an unhandled throw (bug-hunt 2026-07-27 N4).
        // byte.TryParse also rejects indices above 255, which the record cannot carry.
        if (byte.TryParse(tail, out byte index))
            return index;
        throw new MonoGameGlslRewriteException(
            $"Unsupported vertex-input semantic '{sem}' for the MonoGame GL target: " +
            $"'{tail}' is not a supported numeric semantic index.");
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
                // Resume INSIDE the replacement, not past it (issue #140): the argument
                // was copied verbatim into floor((arg) + 0.5), so a same-named call
                // nested in the argument — round(round(x) * 0.5) — must still be
                // visited. The replacement starts with "floor((", which can never
                // re-match "round"/"roundEven", so each pass still eliminates one call
                // and the scan terminates. (Rule 10 already resumes inside its args.)
                searchFrom = callStart;
            }
        }

        return body;
    }

    /// <summary>
    /// Rewrites every <c>trunc(<i>expr</i>)</c> call to <c>sign((<i>expr</i>)) *
    /// floor(abs((<i>expr</i>)))</c> — truncate-toward-zero built from
    /// <c>sign</c>/<c>floor</c>/<c>abs</c>, all available since GLSL ES 1.00 / GLSL
    /// 1.10, unlike <c>trunc</c> itself (GLSL ES 3.00 / GL 1.30+). SPIRV-Cross emits
    /// bare <c>trunc()</c> calls when lowering HLSL's truncating <c>%</c>/<c>fmod</c>
    /// (there is no GLSL builtin with fmod's sign-follows-dividend semantics — GLSL's
    /// own <c>mod()</c> is floored, sign-follows-divisor). Component-wise, so it holds
    /// for <c>float</c>/<c>vec2</c>/<c>vec3</c>/<c>vec4</c> arguments alike. The
    /// argument is captured with a balanced-parenthesis scan so a nested call is
    /// lowered correctly, and the scan resumes inside the replacement so a nested
    /// same-named call is still visited (mirrors <see cref="LowerRoundToFloorHalfUp"/>).
    /// </summary>
    private static string LowerTruncToSignFloorAbs(string body)
    {
        int searchFrom = 0;
        while (true)
        {
            int callStart = FindCallStart(body, "trunc", searchFrom);
            if (callStart < 0)
            {
                break;
            }

            int openParen = callStart + "trunc".Length;
            while (openParen < body.Length && (body[openParen] == ' ' || body[openParen] == '\t'))
            {
                openParen++;
            }

            int closeParen = FindMatchingParen(body, openParen);
            if (closeParen < 0)
            {
                break;
            }

            string arg = body.Substring(openParen + 1, closeParen - openParen - 1);
            // The whole product is parenthesized: `trunc(x)` was a primary expression, and
            // splicing a bare `a * b` into its place re-associates wherever the surrounding
            // operator binds at least as tightly. `1.0 / trunc(x)` would become
            // `(1.0 / sign(x)) * floor(abs(x))` — valid GLSL, silently wrong pixels — and
            // `trunc(v).x` would attach the swizzle to only the second factor. (The main
            // producer, SPIRV-Cross's `a - b * trunc(a / b)` fmod expansion, is unaffected
            // either way; this is the latent case reachable from user HLSL.)
            string replacement = $"(sign(({arg})) * floor(abs(({arg}))))";
            body = body.Substring(0, callStart) + replacement + body.Substring(closeParen + 1);
            searchFrom = callStart;
        }

        return body;
    }

    /// <summary>
    /// Issue #136 (Rule 9a): unwrap one-shot <c>do { … } while(false);</c> loops —
    /// SPIRV-Cross's wrapper for early returns (the entry point's own, and each inlined
    /// helper's) — into plain brace blocks with real early exits. Each <c>break</c>
    /// whose nearest enclosing loop/switch is the one-shot loop becomes the statements
    /// control would run after the loop (the return-value-phi output writes, flattened
    /// through any enclosing plain blocks and through a trailing <c>{ … return; }</c>
    /// block a previous unwrap produced) plus <c>return;</c>; the fall-through path
    /// runs the in-place tail unchanged. Unwraps iterate outside-in: the entry wrapper
    /// first, then helper wrappers inside the plain block it leaves behind. Any loop
    /// the strict preconditions cannot prove safe (inside an if/else/loop/switch body,
    /// a loop-level <c>continue</c>, an unparseable tail, a tail whose duplication
    /// would move a gradient op or implicit-LOD sample into divergent flow) is left for
    /// the Rule-9b for-loop lowering — never corrupted. See the Rule 9 comment at the
    /// call site for why the loop must go entirely on ANGLE D3D11 (a conditional break
    /// alone poisons gradient ops).
    /// </summary>
    private static string UnwrapMainOneShotDoWhile(string body)
    {
        // Each successful unwrap removes one `do` from main's direct children, so this
        // terminates; a pass that unwraps nothing ends the loop.
        while (TryUnwrapOneMainOneShot(body, out string rewritten))
        {
            body = rewritten;
        }
        return body;
    }

    private static bool TryUnwrapOneMainOneShot(string body, out string rewritten)
    {
        rewritten = body;

        if (!TryFindMainBody(body, out int mainOpen, out int mainClose))
        {
            return false;
        }

        // At main's top level, falling off the end returns implicitly, so the exit
        // context carried into the scan is empty-and-valid.
        return TryUnwrapInBlock(body, mainOpen, mainClose, exitStatements: "", exitValid: true, out rewritten);
    }

    /// <summary>
    /// Scan the block <c>(open, close)</c> — <c>main</c>'s body, or a PLAIN nested block
    /// reached recursively — for an unwrappable one-shot do-while among its direct
    /// children. Plain blocks (not the body of an if/else/loop/switch) are transparent:
    /// an earlier 9a unwrap turns the entry wrapper into exactly such a block, and the
    /// one-shot wrapper of an inlined helper's early return then sits inside it — the
    /// issue-#136 poisoning shape would otherwise survive there via the 9b fallback.
    /// Construct bodies (a <c>{</c> preceded by <c>)</c>, <c>else</c>, or <c>do</c>) are
    /// skipped wholesale: control leaving a conditional's body is not statically "the
    /// rest of the scope", so nothing inside is provable.
    /// <paramref name="exitStatements"/> is the flattened statement text that executes
    /// (before an implicit <c>return;</c>) when control falls off THIS block's end;
    /// <paramref name="exitValid"/> is false when that context could not be proven.
    /// </summary>
    private static bool TryUnwrapInBlock(
        string body, int open, int close, string exitStatements, bool exitValid, out string rewritten)
    {
        rewritten = body;
        int p = open + 1;
        bool nextBraceIsConstructBody = false; // set after ')' or `else`
        while (p < close)
        {
            p = SkipWsAndComments(body, p);
            if (p < 0 || p >= close)
            {
                break;
            }
            char c = body[p];
            if (c == '{')
            {
                int bClose = FindMatchingBrace(body, p);
                if (bClose < 0)
                {
                    return false;
                }
                if (!nextBraceIsConstructBody)
                {
                    // PLAIN block. Its exit context = the trailing region between the
                    // block and this scope's close: either it ends in a `{ … return; }`
                    // block (terminating — the parent context is irrelevant) or it is
                    // all simple statements followed by the parent's own exit.
                    string childExit = "";
                    bool childValid = false;
                    if (TryParseExitTail(body, bClose + 1, close, out string simple, out bool terminating, out string retStmts))
                    {
                        if (terminating)
                        {
                            childExit = JoinStatements(simple, retStmts);
                            childValid = true;
                        }
                        else if (exitValid)
                        {
                            childExit = JoinStatements(simple, exitStatements);
                            childValid = true;
                        }
                    }
                    if (TryUnwrapInBlock(body, p, bClose, childExit, childValid, out rewritten))
                    {
                        return true;
                    }
                }
                nextBraceIsConstructBody = false;
                p = bClose + 1;
                continue;
            }
            if (c == ')')
            {
                nextBraceIsConstructBody = true;
                p++;
                continue;
            }
            if (IsIdentChar(c))
            {
                int wordEnd = p;
                while (wordEnd < body.Length && IsIdentChar(body[wordEnd]))
                {
                    wordEnd++;
                }
                if (wordEnd - p == 2 && body[p] == 'd' && body[p + 1] == 'o')
                {
                    int braceIdx = SkipWsAndComments(body, wordEnd);
                    if (braceIdx >= 0 && braceIdx < close && body[braceIdx] == '{')
                    {
                        int closeBrace = FindMatchingBrace(body, braceIdx);
                        if (closeBrace < 0)
                        {
                            return false;
                        }
                        if (TryMatchWhileFalseTrailer(body, closeBrace + 1, out int trailerEnd) &&
                            TryUnwrapAt(body, p, braceIdx, closeBrace, trailerEnd, close,
                                        exitStatements, exitValid, out rewritten))
                        {
                            return true;
                        }
                        // Genuine do-while, or preconditions failed — skip the whole
                        // construct (its trailer tokens are inert to this scan).
                        nextBraceIsConstructBody = false;
                        p = closeBrace + 1;
                        continue;
                    }
                }
                nextBraceIsConstructBody =
                    wordEnd - p == 4 && string.CompareOrdinal(body, p, "else", 0, 4) == 0;
                p = wordEnd;
                continue;
            }
            nextBraceIsConstructBody = false;
            p++;
        }
        return false;
    }

    private static string JoinStatements(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

    /// <summary>
    /// Statements whose textual duplication into a conditional (divergent) branch
    /// changes their semantics: gradient ops and implicit-LOD texture samples are
    /// undefined in non-uniform control flow (GLSL §8.13.1 / ESSL §8.14.1) — in the
    /// original do-while they executed AFTER the loop, convergently, and fxc/mgfxc
    /// evaluate the same post-merge code convergently too. A tail containing one must
    /// keep the 9b for-loop form, which leaves it outside the loop. (The explicit-LOD
    /// spellings — texture2DLod/texture2DGrad/… — are safe and intentionally absent.)
    /// </summary>
    private static readonly Regex DivergenceSensitiveOp = new(
        @"\b(dFdx|dFdy|fwidth|texture|texture2D|texture3D|textureCube|texture2DProj|texture3DProj)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Locate <c>main</c>'s body braces: the <c>main</c> keyword, its parameter
    /// parens, then the opening <c>{</c> and its match.</summary>
    private static bool TryFindMainBody(string body, out int open, out int close)
    {
        open = -1;
        close = -1;
        int i = 0;
        while ((i = body.IndexOf("main", i, StringComparison.Ordinal)) >= 0)
        {
            if (MatchKeyword(body, i, "main"))
            {
                int j = SkipWsAndComments(body, i + 4);
                if (j >= 0 && j < body.Length && body[j] == '(')
                {
                    int closeParen = FindMatchingParen(body, j);
                    if (closeParen > 0)
                    {
                        int k = SkipWsAndComments(body, closeParen + 1);
                        if (k >= 0 && k < body.Length && body[k] == '{')
                        {
                            close = FindMatchingBrace(body, k);
                            if (close < 0)
                            {
                                return false;
                            }
                            open = k;
                            return true;
                        }
                    }
                }
            }
            i += 4;
        }
        return false;
    }

    private static bool TryUnwrapAt(
        string body, int doIdx, int braceIdx, int closeBrace, int trailerEnd, int scopeClose,
        string exitStatements, bool exitValid, out string rewritten)
    {
        rewritten = body;

        // The tail — everything between the loop and its scope's closing brace — is
        // what a loop-level break jumps to: simple `;`-terminated statements (the phi
        // output writes), optionally ending in ONE `{ … return; }` block (the shape a
        // previous 9a unwrap leaves behind). Terminating tails are complete in
        // themselves; a fall-through tail continues into the enclosing scope's exit
        // context. Anything else means "rest of execution" isn't statically this text
        // — bail to 9b.
        if (!TryParseExitTail(body, trailerEnd, scopeClose, out string simple, out bool terminating, out string retStmts))
        {
            return false;
        }
        string tail;
        if (terminating)
        {
            tail = JoinStatements(simple, retStmts);
        }
        else if (exitValid)
        {
            tail = JoinStatements(simple, exitStatements);
        }
        else
        {
            return false;
        }

        // Duplicating a gradient op / implicit-LOD sample into a conditional branch
        // would move it from convergent into divergent control flow — undefined, and a
        // divergence from both the previous output and fxc/mgfxc. Keep the 9b form.
        if (DivergenceSensitiveOp.IsMatch(tail))
        {
            return false;
        }

        // Find every break whose nearest enclosing loop/switch is THIS loop; bail on a
        // loop-level `continue` (identical exit semantics in a one-shot loop, but the
        // for-loop fallback preserves it without needing a rewrite here).
        if (!TryCollectLoopLevelBreaks(body, braceIdx, closeBrace, out List<(int Start, int End)> breaks))
        {
            return false;
        }

        string breakReplacement = tail.Length == 0 ? "return;" : "{ " + tail + " return; }";

        var sb = new StringBuilder(body.Length + breaks.Count * (breakReplacement.Length + 8));
        sb.Append(body, 0, doIdx);                     // drop the `do` keyword
        int prev = braceIdx;                           // keep the braces: a plain block
        foreach ((int start, int end) in breaks)
        {
            sb.Append(body, prev, start - prev);
            sb.Append(breakReplacement);
            prev = end;
        }
        sb.Append(body, prev, closeBrace + 1 - prev);  // through the closing '}'
        sb.Append(body, trailerEnd, body.Length - trailerEnd); // drop `while(false);`
        rewritten = sb.ToString();
        return true;
    }

    /// <summary>
    /// Parse <c>[from, to)</c> as an exit tail: zero or more simple <c>;</c>-terminated
    /// statements (no braces, no control-flow/jump keywords), optionally ENDING with a
    /// single <c>{ &lt;simple statements&gt; return; }</c> block — the exact shape a
    /// previous 9a unwrap emits for an outer break — with nothing after it.
    /// <paramref name="simple"/> receives the leading statements (whitespace-collapsed),
    /// <paramref name="terminating"/> is true when the trailing return-block was
    /// present, and <paramref name="retStmts"/> its inner statements (without the
    /// <c>return;</c>). Returns false on any shape outside this grammar.
    /// </summary>
    private static bool TryParseExitTail(
        string body, int from, int to, out string simple, out bool terminating, out string retStmts)
    {
        simple = "";
        terminating = false;
        retStmts = "";

        if (!TryScanSimpleStatements(body, from, to, allowTrailingReturn: false,
                out string leading, out int stopAt))
        {
            return false;
        }
        if (stopAt >= to)
        {
            simple = leading;
            return true; // all-simple tail; falls through into the enclosing context
        }
        if (body[stopAt] != '{')
        {
            return false;
        }
        int bClose = FindMatchingBrace(body, stopAt);
        if (bClose < 0 || bClose >= to)
        {
            return false;
        }
        // The block must be `{ <simple> return; }` and the LAST thing in the region.
        if (!TryScanSimpleStatements(body, stopAt + 1, bClose, allowTrailingReturn: true,
                out string inner, out int innerStop) ||
            innerStop < bClose) // stopped early on something other than the close
        {
            return false;
        }
        int after = SkipWsAndComments(body, bClose + 1);
        if (after >= 0 && after < to)
        {
            return false; // something follows the return-block — not statically the exit
        }
        simple = leading;
        terminating = true;
        retStmts = inner;
        return true;
    }

    /// <summary>
    /// Scan <c>[from, to)</c> as whitespace-collapsed simple statements, stopping at the
    /// first <c>{</c> (reported via <paramref name="stopAt"/>; <c>stopAt >= to</c> means
    /// the whole region was consumed). Control-flow/jump keywords fail the scan — except
    /// that with <paramref name="allowTrailingReturn"/> a final <c>return ;</c> is
    /// accepted and consumed (used for the <c>{ … return; }</c> block's contents, which
    /// are captured WITHOUT the return). A region ending mid-statement (last significant
    /// char not <c>;</c>) fails.
    /// </summary>
    private static bool TryScanSimpleStatements(
        string body, int from, int to, bool allowTrailingReturn, out string text, out int stopAt)
    {
        text = "";
        stopAt = to;
        int i = from;
        char lastSignificant = ';';
        bool sawReturn = false;
        var sb = new StringBuilder(Math.Max(0, to - from));
        bool pendingSpace = false;
        while (i < to)
        {
            int next = SkipWsAndComments(body, i);
            if (next < 0)
            {
                return false;
            }
            if (next >= to)
            {
                break;
            }
            if (next > i)
            {
                pendingSpace = sb.Length > 0;
            }
            i = next;
            char c = body[i];
            if (sawReturn && c != ';')
            {
                return false; // only `;` may follow the trailing return keyword
            }
            if (c == '{')
            {
                if (sawReturn || lastSignificant != ';')
                {
                    return false;
                }
                stopAt = i;
                text = sb.ToString();
                return true;
            }
            if (c == '}')
            {
                return false;
            }
            if (IsIdentChar(c))
            {
                int wordEnd = i;
                while (wordEnd < to && IsIdentChar(body[wordEnd]))
                {
                    wordEnd++;
                }
                string word = body.Substring(i, wordEnd - i);
                switch (word)
                {
                    case "return" when allowTrailingReturn:
                        sawReturn = true;
                        i = wordEnd;
                        continue; // not captured — the caller re-adds its own return
                    case "if" or "else" or "for" or "while" or "do" or "switch"
                        or "return" or "discard" or "break" or "continue":
                        return false;
                }
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(word);
                lastSignificant = body[wordEnd - 1];
                i = wordEnd;
                continue;
            }
            if (c == ';' && sawReturn)
            {
                // Consume the trailing `return ;` without capturing it; nothing but
                // whitespace/comments may remain.
                int after = SkipWsAndComments(body, i + 1);
                if (after >= 0 && after < to)
                {
                    return false;
                }
                text = sb.ToString();
                stopAt = to;
                return true;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
            lastSignificant = c;
            i++;
        }
        if (sawReturn || lastSignificant != ';')
        {
            return false; // `return` without its `;`, or region ended mid-statement
        }
        text = sb.ToString();
        stopAt = to;
        return true;
    }

    /// <summary>
    /// Scan a one-shot loop's body <c>(open, close)</c> for <c>break;</c> statements
    /// whose nearest enclosing loop/switch is that loop. Nested <c>for</c>/<c>while</c>/
    /// <c>do</c>/<c>switch</c> constructs are skipped wholesale (their breaks are
    /// theirs); <c>if</c>/<c>else</c> bodies and plain blocks are walked through (breaks
    /// there ARE loop-level). Returns false — caller falls back to the for-loop
    /// lowering — on a loop-level <c>continue</c> or any construct shape it cannot
    /// prove (unbraced nested-loop body, missing trailer, unbalanced delimiters).
    /// </summary>
    private static bool TryCollectLoopLevelBreaks(
        string body, int open, int close, out List<(int Start, int End)> breaks)
    {
        breaks = new List<(int, int)>();
        int i = open + 1;
        while (i < close)
        {
            int next = SkipWsAndComments(body, i);
            if (next < 0)
            {
                return false;
            }
            if (next >= close)
            {
                break;
            }
            i = next;
            char c = body[i];
            if (!IsIdentChar(c))
            {
                i++;
                continue;
            }
            int wordEnd = i;
            while (wordEnd < body.Length && IsIdentChar(body[wordEnd]))
            {
                wordEnd++;
            }
            string word = body.Substring(i, wordEnd - i);
            switch (word)
            {
                case "for":
                case "while":
                case "switch":
                {
                    int paren = SkipWsAndComments(body, wordEnd);
                    if (paren < 0 || paren >= close || body[paren] != '(')
                    {
                        return false;
                    }
                    int parenClose = FindMatchingParen(body, paren);
                    if (parenClose < 0)
                    {
                        return false;
                    }
                    int bodyBrace = SkipWsAndComments(body, parenClose + 1);
                    if (bodyBrace < 0 || bodyBrace >= close || body[bodyBrace] != '{')
                    {
                        return false; // unbraced nested body — cannot prove; bail
                    }
                    int bodyClose = FindMatchingBrace(body, bodyBrace);
                    if (bodyClose < 0)
                    {
                        return false;
                    }
                    i = bodyClose + 1;
                    continue;
                }
                case "do":
                {
                    int bodyBrace = SkipWsAndComments(body, wordEnd);
                    if (bodyBrace < 0 || bodyBrace >= close || body[bodyBrace] != '{')
                    {
                        return false;
                    }
                    int bodyClose = FindMatchingBrace(body, bodyBrace);
                    if (bodyClose < 0)
                    {
                        return false;
                    }
                    int w = SkipWsAndComments(body, bodyClose + 1);
                    if (w < 0 || !MatchKeyword(body, w, "while"))
                    {
                        return false;
                    }
                    int paren = SkipWsAndComments(body, w + 5);
                    if (paren < 0 || body[paren] != '(')
                    {
                        return false;
                    }
                    int parenClose = FindMatchingParen(body, paren);
                    if (parenClose < 0)
                    {
                        return false;
                    }
                    int semi = SkipWsAndComments(body, parenClose + 1);
                    if (semi < 0 || semi >= body.Length || body[semi] != ';')
                    {
                        return false;
                    }
                    i = semi + 1;
                    continue;
                }
                case "break":
                {
                    int semi = SkipWsAndComments(body, wordEnd);
                    if (semi < 0 || semi >= close || body[semi] != ';')
                    {
                        return false;
                    }
                    breaks.Add((i, semi + 1));
                    i = semi + 1;
                    continue;
                }
                case "continue":
                    return false; // loop-level continue — fall back to for-loop lowering
                default:
                    i = wordEnd;
                    continue;
            }
        }
        return true;
    }

    /// <summary>
    /// Issue #107 (Rule 9b): lower SPIRV-Cross's one-shot <c>do { … } while(false);</c> loops (its
    /// structured-early-return idiom) to the WebGL1-safe <c>for (int _i = 0; _i &lt; 1; _i++) { … }</c>
    /// form. Semantically identical (exactly one iteration; <c>break</c>/<c>continue</c>/
    /// fall-through all exit as before, so pixels are unchanged), but uses the GLSL ES 1.00
    /// Appendix-A loop form WebGL1 / KNI Reach requires — so the effect loads in WebGL instead
    /// of failing on the do-while. A genuine multi-iteration <c>do { } while(&lt;not false&gt;)</c>
    /// is left untouched. Comment-aware (skips <c>//</c> and <c>/* */</c> while scanning).
    /// Since issue #136 this is the FALLBACK: <see cref="UnwrapMainOneShotDoWhile"/> (Rule 9a)
    /// unwraps the entry-point wrapper first; only loops it could not prove safe reach here.
    /// </summary>
    private static string LowerOneShotDoWhileToForLoop(string body)
    {
        int counter = 0;
        int searchFrom = 0;
        while (true)
        {
            int doIdx = FindDoBlock(body, searchFrom, out int braceIdx);
            if (doIdx < 0)
                break;

            int closeBrace = FindMatchingBrace(body, braceIdx);
            if (closeBrace < 0)
                break; // unbalanced (should not happen in valid GLSL) — stop, do not corrupt

            if (!TryMatchWhileFalseTrailer(body, closeBrace + 1, out int trailerEnd))
            {
                // `do { } while(<not false>)` — a real loop. Leave it; resume past this `do`.
                searchFrom = braceIdx;
                continue;
            }

            string loopVar = $"_spvonce_{counter++}";
            string forHeader = $"for (int {loopVar} = 0; {loopVar} < 1; {loopVar}++) ";
            string bodyBlock = body.Substring(braceIdx, closeBrace - braceIdx + 1); // "{ … }"

            body = body.Substring(0, doIdx) + forHeader + bodyBlock + body.Substring(trailerEnd);
            // Resume just after the inserted header so a nested one-shot do-while inside this
            // body is lowered too (each becomes its own for-loop with a unique index var).
            searchFrom = doIdx + forHeader.Length;
        }

        return body;
    }

    /// <summary>
    /// Find the next <c>do</c> keyword (word-bounded, not inside a comment) immediately
    /// followed (after whitespace/comments) by <c>{</c>. Returns the <c>do</c> index and, via
    /// <paramref name="braceIdx"/>, that opening brace's index; -1 when none remains.
    /// </summary>
    private static int FindDoBlock(string s, int from, out int braceIdx)
    {
        braceIdx = -1;
        int i = from;
        while (i < s.Length)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                i = s.IndexOf('\n', i);
                if (i < 0) return -1;
                continue;
            }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int blockEnd = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (blockEnd < 0) return -1;
                i = blockEnd + 2;
                continue;
            }
            if (s[i] == 'd' && i + 1 < s.Length && s[i + 1] == 'o' &&
                (i == 0 || !IsIdentChar(s[i - 1])) &&
                (i + 2 >= s.Length || !IsIdentChar(s[i + 2])))
            {
                int j = SkipWsAndComments(s, i + 2);
                if (j >= 0 && j < s.Length && s[j] == '{')
                {
                    braceIdx = j;
                    return i;
                }
                i += 2;
                continue;
            }
            i++;
        }
        return -1;
    }

    /// <summary>Skip whitespace and <c>//</c> / <c>/* */</c> comments; returns the next
    /// significant index, or -1 if an unterminated comment runs to end-of-string.</summary>
    private static int SkipWsAndComments(string s, int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                i = s.IndexOf('\n', i);
                if (i < 0) return -1;
                continue;
            }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int blockEnd = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (blockEnd < 0) return -1;
                i = blockEnd + 2;
                continue;
            }
            return i;
        }
        return i;
    }

    /// <summary>Brace-match from an opening <c>{</c> to its closing <c>}</c>, skipping
    /// comments; -1 if unbalanced.</summary>
    private static int FindMatchingBrace(string s, int open)
    {
        int depth = 0;
        int i = open;
        while (i < s.Length)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                i = s.IndexOf('\n', i);
                if (i < 0) return -1;
                continue;
            }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int blockEnd = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (blockEnd < 0) return -1;
                i = blockEnd + 2;
                continue;
            }
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
            i++;
        }
        return -1;
    }

    /// <summary>Match a <c>while ( false ) ;</c> trailer at <paramref name="from"/> (ws/comment
    /// tolerant between tokens). On success, <paramref name="end"/> is set just past the ';'.</summary>
    private static bool TryMatchWhileFalseTrailer(string s, int from, out int end)
    {
        end = -1;
        int i = SkipWsAndComments(s, from);
        if (i < 0 || !MatchKeyword(s, i, "while")) return false;
        i = SkipWsAndComments(s, i + 5);
        if (i < 0 || i >= s.Length || s[i] != '(') return false;
        i = SkipWsAndComments(s, i + 1);
        if (i < 0 || !MatchKeyword(s, i, "false")) return false;
        i = SkipWsAndComments(s, i + 5);
        if (i < 0 || i >= s.Length || s[i] != ')') return false;
        i = SkipWsAndComments(s, i + 1);
        if (i < 0 || i >= s.Length || s[i] != ';') return false;
        end = i + 1;
        return true;
    }

    /// <summary>True if <paramref name="word"/> appears at <paramref name="i"/> with identifier
    /// boundaries on both sides (ordinal, case-sensitive — GLSL keywords are case-sensitive).</summary>
    private static bool MatchKeyword(string s, int i, string word)
    {
        if (i + word.Length > s.Length) return false;
        if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
        if (i > 0 && IsIdentChar(s[i - 1])) return false;
        int after = i + word.Length;
        if (after < s.Length && IsIdentChar(s[after])) return false;
        return true;
    }

    /// <summary>
    /// Issue #127: rewrites <c>pow(<i>x</i>, 2.0)</c> to <c>((x) * (x))</c>. GLSL
    /// (desktop 1.10 and ES 1.00 alike) leaves <c>pow</c> undefined for a negative
    /// base, while fxc constant-folds <c>pow(x, 2)</c> into a multiply — so the
    /// multiply is both the well-defined form and the reference compiler's semantics.
    /// Only a SIMPLE base operand (optionally signed identifier / swizzle / numeric
    /// literal — the only shape SPIRV-Cross emits here, its temps being SSA-named) is
    /// rewritten, so the textual duplication can never re-evaluate a call or
    /// side-effecting expression; a complex base is left as the original pow().
    /// </summary>
    private static string LowerPowSquareToMultiply(string body)
    {
        int searchFrom = 0;
        while (true)
        {
            int callStart = FindCallStart(body, "pow", searchFrom);
            if (callStart < 0)
            {
                break;
            }

            int openParen = callStart + "pow".Length;
            while (openParen < body.Length && (body[openParen] == ' ' || body[openParen] == '\t'))
            {
                openParen++;
            }

            int closeParen = FindMatchingParen(body, openParen);
            if (closeParen < 0)
            {
                break; // unbalanced (should not happen in valid GLSL) — stop, do not corrupt
            }

            searchFrom = openParen; // default: resume inside the args (covers nested pow)

            string args = body.Substring(openParen + 1, closeParen - openParen - 1);
            int comma = FindTopLevelComma(args);
            if (comma < 0)
            {
                continue;
            }

            string baseArg  = args.Substring(0, comma).Trim();
            string exponent = args.Substring(comma + 1).Trim();
            if (!IsLiteralTwo(exponent) || !IsSimpleOperand(baseArg))
            {
                continue;
            }

            string replacement = $"(({baseArg}) * ({baseArg}))";
            body = body.Substring(0, callStart) + replacement + body.Substring(closeParen + 1);
            searchFrom = callStart + replacement.Length;
        }

        return body;
    }

    /// <summary>The index of the first depth-0 ',' in <paramref name="expr"/>, or -1.</summary>
    private static int FindTopLevelComma(string expr)
    {
        int depth = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(' || c == '[') depth++;
            else if (c == ')' || c == ']') depth--;
            else if (c == ',' && depth == 0) return i;
        }
        return -1;
    }

    /// <summary>True for a float literal that is exactly two: <c>2.0</c>, <c>2.00</c>, …</summary>
    private static bool IsLiteralTwo(string expr)
    {
        if (expr.Length < 3 || expr[0] != '2' || expr[1] != '.')
        {
            return false;
        }
        for (int i = 2; i < expr.Length; i++)
        {
            if (expr[i] != '0')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="expr"/> is a single pure operand — an optionally
    /// signed identifier (with at most one <c>.member</c>/swizzle suffix) or numeric
    /// literal — i.e. an expression that is trivially safe to duplicate textually.
    /// </summary>
    private static bool IsSimpleOperand(string expr)
    {
        int i = 0;
        if (i < expr.Length && (expr[i] == '-' || expr[i] == '+'))
        {
            i++;
        }
        if (i >= expr.Length)
        {
            return false;
        }

        if (char.IsDigit(expr[i]))
        {
            bool seenDot = false;
            for (; i < expr.Length; i++)
            {
                if (expr[i] == '.' && !seenDot) { seenDot = true; continue; }
                if (!char.IsDigit(expr[i])) return false;
            }
            return true;
        }

        if (expr[i] != '_' && !char.IsLetter(expr[i]))
        {
            return false;
        }
        while (i < expr.Length && IsIdentChar(expr[i]))
        {
            i++;
        }
        if (i == expr.Length)
        {
            return true;
        }
        if (expr[i] != '.')
        {
            return false;
        }
        i++;
        int suffix = 0;
        while (i < expr.Length && IsIdentChar(expr[i]))
        {
            i++;
            suffix++;
        }
        return i == expr.Length && suffix >= 1;
    }

    /// <summary>
    /// Issue #127: folds <c>1.0 / (<i>a</i> / <i>b</i>)</c> to <c>((b) / (a))</c> —
    /// one correctly-rounded division instead of two. Value-equivalent across the
    /// zero/infinity edge cases (<c>1/(a/0) = +0 = 0/a</c>; <c>1/(0/b) = +inf = b/0</c>;
    /// IEEE signs agree in every quadrant). Applied only when the <c>1.0</c> begins its
    /// term (so the replacement keeps the surrounding precedence/evaluation order
    /// intact) and the parenthesized group's top-level operator is provably that single
    /// division; any ambiguity leaves the site untouched.
    /// </summary>
    private static string FoldReciprocalOfQuotient(string body)
    {
        int searchFrom = 0;
        while (true)
        {
            int idx = body.IndexOf("1.0", searchFrom, StringComparison.Ordinal);
            if (idx < 0)
            {
                break;
            }
            searchFrom = idx + 3;

            // Literal boundary: not the tail/head of a longer number or identifier
            // (21.0, 1.05, x1.0, 1.0e5 …).
            if (idx > 0 && (IsIdentChar(body[idx - 1]) || body[idx - 1] == '.'))
            {
                continue;
            }
            int afterLit = idx + 3;
            if (afterLit < body.Length &&
                (char.IsDigit(body[afterLit]) || body[afterLit] == '.' ||
                 body[afterLit] == 'e' || body[afterLit] == 'E'))
            {
                continue;
            }

            // The 1.0 must BEGIN its term — preceded (ignoring whitespace) by an
            // assignment/opener/separator, never by an operand or a '*' '/' '%' whose
            // right operand it would be.
            int p = idx - 1;
            while (p >= 0 && (body[p] == ' ' || body[p] == '\t'))
            {
                p--;
            }
            if (p >= 0)
            {
                char c = body[p];
                bool termStart = c is '=' or '(' or ',' or '?' or ':' or '{' or ';' or '\n' or '\r' or '+' or '-';
                if (!termStart)
                {
                    continue;
                }
            }

            // Expect '/' then a parenthesized group.
            int q = afterLit;
            while (q < body.Length && (body[q] == ' ' || body[q] == '\t'))
            {
                q++;
            }
            if (q >= body.Length || body[q] != '/')
            {
                continue;
            }
            q++;
            while (q < body.Length && (body[q] == ' ' || body[q] == '\t'))
            {
                q++;
            }
            if (q >= body.Length || body[q] != '(')
            {
                continue;
            }

            int close = FindMatchingParen(body, q);
            if (close < 0)
            {
                continue;
            }

            string inner = body.Substring(q + 1, close - q - 1);
            if (!TrySplitTopLevelDivision(inner, out string numerator, out string denominator))
            {
                continue;
            }

            string replacement = $"({Parenthesize(denominator)} / {Parenthesize(numerator)})";
            body = body.Substring(0, idx) + replacement + body.Substring(close + 1);
            searchFrom = idx + replacement.Length;
        }

        return body;
    }

    /// <summary>Wraps <paramref name="expr"/> in parentheses unless it already is one
    /// fully parenthesized group (avoids stacking redundant parens).</summary>
    private static string Parenthesize(string expr) =>
        expr.Length >= 2 && expr[0] == '(' && FindMatchingParen(expr, 0) == expr.Length - 1
            ? expr
            : $"({expr})";

    /// <summary>
    /// Splits <paramref name="expr"/> at the division that is provably the root of its
    /// expression tree: the LAST depth-0 multiplicative operator must be a <c>/</c>
    /// (left-associativity makes the last one the root), and no depth-0 operator of
    /// lower precedence — additive, relational, logical, ternary, comma — may exist.
    /// A depth-0 <c>+</c>/<c>-</c> is tolerated only when clearly unary (preceded by
    /// nothing or another operator); anything ambiguous (including a scientific-notation
    /// exponent sign) rejects the split, which merely leaves the site unoptimized.
    /// </summary>
    private static bool TrySplitTopLevelDivision(string expr, out string left, out string right)
    {
        left = string.Empty;
        right = string.Empty;

        int depth = 0;
        int rootDiv = -1;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(' || c == '[')
            {
                depth++;
                continue;
            }
            if (c == ')' || c == ']')
            {
                depth--;
                continue;
            }
            if (depth != 0)
            {
                continue;
            }

            switch (c)
            {
                case ',':
                case '?':
                case ':':
                case '<':
                case '>':
                case '&':
                case '|':
                case '!':
                case '^':
                case '=':
                    return false;
                case '+':
                case '-':
                {
                    int p = i - 1;
                    while (p >= 0 && (expr[p] == ' ' || expr[p] == '\t'))
                    {
                        p--;
                    }
                    bool unary = p < 0 || expr[p] is '(' or ',' or '*' or '/' or '%' or '+' or '-';
                    if (!unary)
                    {
                        return false;
                    }
                    break;
                }
                case '*':
                case '%':
                    rootDiv = -1;
                    break;
                case '/':
                    rootDiv = i;
                    break;
            }
        }

        if (depth != 0 || rootDiv < 0)
        {
            return false;
        }

        left = expr.Substring(0, rootDiv).Trim();
        right = expr.Substring(rootDiv + 1).Trim();
        return left.Length > 0 && right.Length > 0;
    }

    /// <summary>
    /// Issue #138 (Rule 12), shape 2: SPIRV-Cross emits some constant-bounded loops as
    /// <c>for (int _40 = 0; _40 &lt; 15; ) { …; _40++; continue; }</c> — a declared init
    /// clause, a normal condition, but an EMPTY increment clause, with the index instead
    /// advanced by the body's last two statements. GLSL ES 1.00 Appendix A requires the
    /// increment to live in the for-header and forbids any other write to the index, so
    /// this shape fails to load on WebGL1/KNI Reach (SD0402) — and independently makes
    /// any <c>arr[base + _40]</c> access a non-constant-index-expression there too.
    ///
    /// Hoisting the trailing <c>_40++;</c> (or <c>_40 += k;</c>) into the header and
    /// deleting the two trailing statements is semantically exact — same iteration
    /// count, same body, the increment just moves where it textually lives — PROVIDED
    /// the index has no other write and no other <c>continue</c> exists anywhere else
    /// in the body (either could change behavior if the increment moved). Only that
    /// provably-safe shape is rewritten; anything else is left for SD0402 to keep
    /// warning about, not guessed at.
    /// </summary>
    private static string LowerEmptyIncrementForLoop(string body)
    {
        int searchFrom = 0;
        while (true)
        {
            int forIdx = FindForKeyword(body, searchFrom);
            if (forIdx < 0)
            {
                break;
            }

            int openParen = SkipWsAndComments(body, forIdx + 3);
            if (openParen < 0 || openParen >= body.Length || body[openParen] != '(')
            {
                searchFrom = forIdx + 3;
                continue;
            }

            int closeParen = FindMatchingParen(body, openParen);
            if (closeParen < 0)
            {
                break; // unbalanced (should not happen in valid GLSL) — stop, do not corrupt
            }

            searchFrom = openParen + 1; // default resume point

            (string init, string cond, string incr, bool wellFormed) =
                SplitForHeaderThreeParts(body, openParen, closeParen);
            if (!wellFormed || init.Trim().Length == 0 || incr.Trim().Length != 0)
            {
                continue; // only the declared-init, empty-increment shape
            }

            string? indexVar = ExtractDeclaredIndexVar(init);
            if (indexVar is null)
            {
                continue;
            }

            int braceIdx = SkipWsAndComments(body, closeParen + 1);
            if (braceIdx < 0 || braceIdx >= body.Length || body[braceIdx] != '{')
            {
                continue;
            }

            int bodyClose = FindMatchingBrace(body, braceIdx);
            if (bodyClose < 0)
            {
                break; // unbalanced — stop, do not corrupt
            }

            string innerBody = body.Substring(braceIdx + 1, bodyClose - braceIdx - 1);
            if (!TryHoistTrailingIncrement(innerBody, indexVar, out string incrExpr, out string newInnerBody))
            {
                continue;
            }

            string newFor = $"for ({init};{cond}; {incrExpr})";
            body = body.Substring(0, forIdx) + newFor + " {" + newInnerBody + "}" + body.Substring(bodyClose + 1);
            searchFrom = forIdx + newFor.Length;
        }

        return body;
    }

    /// <summary>Find the next <c>for</c> keyword (word-bounded, not inside a comment).</summary>
    private static int FindForKeyword(string s, int from)
    {
        int i = from;
        while (i < s.Length)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                i = s.IndexOf('\n', i);
                if (i < 0)
                {
                    return -1;
                }
                continue;
            }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int blockEnd = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (blockEnd < 0)
                {
                    return -1;
                }
                i = blockEnd + 2;
                continue;
            }
            if (MatchKeyword(s, i, "for"))
            {
                return i;
            }
            i++;
        }

        return -1;
    }

    /// <summary>Splits a <c>for(...)</c> header at TOP-LEVEL semicolons (paren-depth 0)
    /// into its init/condition/increment clauses.</summary>
    private static (string Init, string Cond, string Incr, bool WellFormed) SplitForHeaderThreeParts(
        string s, int open, int close)
    {
        int depth = 0;
        int firstSemi = -1, secondSemi = -1;
        for (int i = open + 1; i < close; i++)
        {
            char c = s[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ';' && depth == 0)
            {
                if (firstSemi < 0)
                {
                    firstSemi = i;
                }
                else if (secondSemi < 0)
                {
                    secondSemi = i;
                }
                else
                {
                    return (string.Empty, string.Empty, string.Empty, false);
                }
            }
        }

        if (firstSemi < 0 || secondSemi < 0)
        {
            return (string.Empty, string.Empty, string.Empty, false);
        }

        string init = s[(open + 1)..firstSemi];
        string cond = s[(firstSemi + 1)..secondSemi];
        string incr = s[(secondSemi + 1)..close];
        return (init, cond, incr, true);
    }

    /// <summary>
    /// Extracts the declared loop variable's name from a for-header init clause of the
    /// exact SPIRV-Cross shape <c>int _40 = 0</c> (a single plain-<c>int</c> declaration
    /// with an initializer). Null for anything else (no <c>int</c> keyword, no simple
    /// identifier, no plain <c>=</c>) — left unrewritten rather than guessed at.
    /// </summary>
    private static string? ExtractDeclaredIndexVar(string init)
    {
        string trimmed = init.Trim();
        if (!MatchKeyword(trimmed, 0, "int"))
        {
            return null;
        }

        int i = SkipWsAndComments(trimmed, 3);
        if (i < 0)
        {
            return null;
        }

        int nameStart = i;
        while (i < trimmed.Length && IsIdentChar(trimmed[i]))
        {
            i++;
        }
        if (i == nameStart)
        {
            return null;
        }
        string name = trimmed[nameStart..i];

        int eq = SkipWsAndComments(trimmed, i);
        if (eq < 0 || eq >= trimmed.Length || trimmed[eq] != '=' ||
            (eq + 1 < trimmed.Length && trimmed[eq + 1] == '='))
        {
            return null; // no plain '=' initializer (a bare "==" is never valid here anyway)
        }

        return name;
    }

    /// <summary>
    /// If <paramref name="innerBody"/>'s LAST two statements are exactly
    /// <c>&lt;indexVar&gt;++;</c>/<c>&lt;indexVar&gt;--;</c>/<c>&lt;indexVar&gt; OP= expr;</c>
    /// followed by <c>continue;</c>, AND <paramref name="indexVar"/> is written nowhere
    /// else in the body AND no other <c>continue</c> exists elsewhere, returns the
    /// increment expression to hoist into the for-header plus the body with that
    /// trailing pair removed. Otherwise returns false and leaves the loop untouched —
    /// the conservative, provably-safe bar this rewrite requires.
    /// </summary>
    private static bool TryHoistTrailingIncrement(
        string innerBody, string indexVar, out string incrExpr, out string newInnerBody)
    {
        incrExpr = string.Empty;
        newInnerBody = innerBody;

        var tail = TrailingIncrementContinue(indexVar).Match(innerBody);
        if (!tail.Success)
        {
            return false;
        }

        string bodyWithoutTail = innerBody[..tail.Index];

        // Safety bar: no OTHER write to indexVar, and no OTHER `continue`, anywhere in
        // what remains of the body once the matched tail is set aside.
        if (WritesVariable(bodyWithoutTail, indexVar) || ContainsWordToken(bodyWithoutTail, "continue"))
        {
            return false;
        }

        incrExpr = tail.Groups["incr"].Value.Trim();
        newInnerBody = bodyWithoutTail;
        return true;
    }

    /// <summary>Matches <c>&lt;indexVar&gt;(++|--|OP= expr); continue;</c> anchored at the
    /// END of the (trimmed) body — SPIRV-Cross's only emitted forms for this idiom.</summary>
    private static Regex TrailingIncrementContinue(string indexVar) => new(
        @"(?<incr>\b" + Regex.Escape(indexVar) + @"\s*(?:\+\+|--|[-+*/]=\s*[^;]+?))\s*;\s*continue\s*;\s*$",
        RegexOptions.Singleline);

    /// <summary>True if <paramref name="body"/> writes <paramref name="variable"/> anywhere
    /// (assignment, any compound assignment including <c>%=</c>/shift/bitwise forms, or
    /// prefix/postfix <c>++</c>/<c>--</c>) — a plain read, or the identifier appearing as
    /// a substring of a longer name, does not count.</summary>
    private static bool WritesVariable(string body, string variable) =>
        Regex.IsMatch(body,
            @"(?:\+\+|--)\s*" + Regex.Escape(variable) + @"\b|\b" + Regex.Escape(variable) +
            @"\s*(?:\+\+|--|(?:<<|>>)=|[-+*/%&|^]?=(?!=))");

    /// <summary>True if the whole word <paramref name="word"/> occurs anywhere in
    /// <paramref name="body"/> (identifier-boundary aware).</summary>
    private static bool ContainsWordToken(string body, string word) =>
        Regex.IsMatch(body, @"\b" + Regex.Escape(word) + @"\b");

    /// <summary>
    /// Issue #138 (Rule 13), shape 1 — Apos.Shapes' Newton-iteration style: SPIRV-Cross
    /// emits a header-less <c>for (;;)</c> whose entire body is one
    /// <c>if (idx &lt; boundVar) { …; idx++; continue; } else { …; break; }</c>, with
    /// <c>idx</c> declared as its own statement immediately above the loop and
    /// <c>boundVar</c> itself set, immediately above THAT, from a compile-time-constant
    /// expression (a bare literal, or a ternary between two literals) — the shader's
    /// real iteration ceiling hasn't actually been lost, it's just been renamed into a
    /// runtime-looking SSA temporary by the time this text is emitted.
    ///
    /// Rewriting to <c>for (int idx = 0; idx &lt;= provenMax; idx++) { if (idx &lt;
    /// boundVar) {…} else {…} }</c> is EXACT, not an approximation: GLSL ES 1.00
    /// Appendix A requires the header's bound to be a literal (a provably-bounded
    /// variable doesn't satisfy the syntax), but <c>provenMax</c> IS the shader's true
    /// maximum, so the loop still runs exactly as many iterations as before — the
    /// runtime check against the real (possibly smaller) <c>boundVar</c> survives
    /// unchanged inside the body. The cap is <c>&lt;= provenMax</c> (not <c>&lt;</c>)
    /// so the header-less loop's <c>else</c> branch — which the original runs at
    /// <c>idx == boundVar</c>, and boundVar can equal provenMax — stays reachable
    /// (issue #160); an inclusive inner comparison (<c>&lt;=</c>) rejects first at
    /// <c>boundVar + 1</c>, so its cap is <c>provenMax + 1</c>. Only an ascending
    /// step-1 walk (<c>idx++</c> / <c>idx += 1</c>) with a literal init the cap
    /// admits, an ascending (<c>&lt;</c> / <c>&lt;=</c>) comparison, and no
    /// post-loop read of the index is rewritten — anything else (descending walks,
    /// wider steps, an index used after the loop) is left for SD0402 to keep
    /// warning about, because an unprovable rewrite here is how issue #160 shipped.
    /// </summary>
    private static string LowerBoundedHeaderlessForLoop(string body)
    {
        int searchFrom = 0;
        while (true)
        {
            int forIdx = FindForKeyword(body, searchFrom);
            if (forIdx < 0)
            {
                break;
            }

            int openParen = SkipWsAndComments(body, forIdx + 3);
            if (openParen < 0 || openParen >= body.Length || body[openParen] != '(')
            {
                searchFrom = forIdx + 3;
                continue;
            }

            int closeParen = FindMatchingParen(body, openParen);
            if (closeParen < 0)
            {
                break; // unbalanced — stop, do not corrupt
            }

            searchFrom = openParen + 1; // default resume point

            (string init, string cond, string incr, bool wellFormed) =
                SplitForHeaderThreeParts(body, openParen, closeParen);
            if (!wellFormed || init.Trim().Length != 0 || cond.Trim().Length != 0 || incr.Trim().Length != 0)
            {
                continue; // only the fully header-less "for (;;)" shape (Rule 12 handles the other one)
            }

            if (!TryFindImmediatelyPrecedingIntDecl(body, forIdx, out string idxVar, out string idxInit, out int idxDeclStart))
            {
                continue;
            }

            int braceIdx = SkipWsAndComments(body, closeParen + 1);
            if (braceIdx < 0 || braceIdx >= body.Length || body[braceIdx] != '{')
            {
                continue;
            }

            int bodyClose = FindMatchingBrace(body, braceIdx);
            if (bodyClose < 0)
            {
                break; // unbalanced — stop, do not corrupt
            }

            string innerBody = body.Substring(braceIdx + 1, bodyClose - braceIdx - 1);
            if (!TryParseSoleIfElse(innerBody, out string condIdx, out string cmpOp, out string boundVar,
                    out string trueBranch, out string falseBranch) ||
                condIdx != idxVar)
            {
                continue;
            }

            if (!TryFindPrecedingDecl(body, idxDeclStart, boundVar, out string boundExpr) ||
                !TryResolveLiteralMax(boundExpr, out int provenMax))
            {
                continue; // no compile-time-provable ceiling — SD0402 keeps warning, honestly
            }

            // provenMax comes from the bound's INITIALIZER, so it is only a real ceiling
            // while the bound never changes. SPIRV-Cross emits loop-carried values as
            // `int _b = <literal>;` plus a reassignment in the body — the very shape this
            // rule reads for the index itself — and a bound that grows past provenMax
            // would let the synthesized header exit before the terminal `else` finalizer
            // ever runs, leaving its output undefined. That is issue #160's exact failure
            // mode, so decline to the honest SD0402 warning instead of rewriting wrong.
            if (WritesVariable(trueBranch, boundVar) || WritesVariable(falseBranch, boundVar))
            {
                continue;
            }

            if (!TryHoistTrailingIncrement(trueBranch, idxVar, out string incrExpr, out string newTrueBranch))
            {
                continue;
            }

            // The false branch must end in `break;` with nothing else risky before it
            // (no other break/continue, no write to the index) — the same conservative
            // bar as everywhere else in this rewriter: an unrecognized shape is left
            // untouched rather than guessed at.
            int lastBreak = falseBranch.LastIndexOf("break", StringComparison.Ordinal);
            if (lastBreak < 0)
            {
                continue;
            }
            string falseTail = falseBranch[(lastBreak + 5)..].TrimStart();
            if (falseTail.Length == 0 || falseTail[0] != ';')
            {
                continue; // not a bare `break;` — an unexpected shape
            }
            string falseBeforeBreak = falseBranch[..lastBreak];
            if (WritesVariable(falseBeforeBreak, idxVar) ||
                ContainsWordToken(falseBeforeBreak, "break") ||
                ContainsWordToken(falseBeforeBreak, "continue"))
            {
                continue;
            }

            // The header cap below is exact ONLY for an ascending step-1 walk: the else
            // fires at the first index the inner comparison rejects, and the header must
            // still admit that index. Every other combination the patterns can match
            // either skips the terminal else (a step > 1 can jump past the cap; `>`/`>=`
            // walks descend) or needs a bigger cap (`<=` rejects first at boundVar + 1).
            // Anything unprovable declines to SD0402's honest warning — emitting a loop
            // that drops its finalizer is exactly how issue #160 happened.
            if (!Regex.IsMatch(incrExpr, @"^" + Regex.Escape(idxVar) + @"\s*(?:\+\+|\+=\s*1)$"))
            {
                continue; // not an ascending step-1 walk
            }

            int headerMax;
            if (cmpOp == "<")
            {
                headerMax = provenMax; // else fires at idx == boundVar, and boundVar <= provenMax
            }
            else if (cmpOp == "<=" && provenMax < int.MaxValue)
            {
                headerMax = provenMax + 1; // else fires at idx == boundVar + 1 <= provenMax + 1
            }
            else
            {
                continue; // descending comparison — the ascending cap would invert the walk
            }

            // The header must admit the first iteration: the original always enters its
            // body once (running the else immediately when the comparison rejects), so a
            // cap below the init would erase an iteration the original performed — the
            // bare-negative-literal and init-past-bound shapes.
            if (!int.TryParse(idxInit, out int idxInitValue) || idxInitValue > headerMax)
            {
                continue;
            }

            // Moving the declaration into the for-header ends its scope at the loop's
            // closing brace; any later read of the index would become undeclared.
            if (Regex.IsMatch(body[(bodyClose + 1)..], @"\b" + Regex.Escape(idxVar) + @"\b"))
            {
                continue;
            }

            string newFor = $"for (int {idxVar} = {idxInit}; {idxVar} <= {headerMax}; {incrExpr})";
            string newInner = $"if ({idxVar} {cmpOp} {boundVar}) {{{newTrueBranch}}} else {{{falseBranch}}}";
            body = body[..idxDeclStart] + newFor + " {" + newInner + "}" + body[(bodyClose + 1)..];
            searchFrom = idxDeclStart + newFor.Length;
        }

        return body;
    }

    /// <summary>
    /// If the statement immediately preceding <paramref name="beforeIndex"/> (skipping
    /// whitespace/comments) is a plain <c>int &lt;name&gt; = &lt;expr&gt;;</c> declaration,
    /// returns its variable name, initializer expression, and the statement's start index.
    /// </summary>
    private static bool TryFindImmediatelyPrecedingIntDecl(
        string body, int beforeIndex, out string varName, out string initExpr, out int declStart)
    {
        varName = string.Empty;
        initExpr = string.Empty;
        declStart = -1;

        if (!TryFindPrecedingStatement(body, beforeIndex, out int start, out int semiIdx))
        {
            return false;
        }

        var m = IntDeclPattern.Match(body[start..semiIdx].Trim());
        if (!m.Success)
        {
            return false;
        }

        varName = m.Groups[1].Value;
        initExpr = m.Groups[2].Value.Trim();
        declStart = start;
        return true;
    }

    /// <summary>
    /// Walks backward through the SAME straight-line block (never crossing a <c>{</c>/
    /// <c>}</c> scope boundary) from <paramref name="beforeIndex"/>, statement by
    /// statement, looking for a plain <c>int &lt;variable&gt; = &lt;expr&gt;;</c>
    /// declaration of the exact name requested. Returns its initializer expression.
    /// </summary>
    private static bool TryFindPrecedingDecl(string body, int beforeIndex, string variable, out string expr)
    {
        expr = string.Empty;
        var declRegex = new Regex(@"^int\s+" + Regex.Escape(variable) + @"\s*=\s*(.+)$", RegexOptions.Singleline);

        int pos = beforeIndex;
        while (TryFindPrecedingStatement(body, pos, out int start, out int semiIdx))
        {
            string stmt = body[start..semiIdx].Trim();
            var m = declRegex.Match(stmt);
            if (m.Success)
            {
                expr = m.Groups[1].Value.Trim();
                return true;
            }

            // A statement that WRITES the variable stands between us and its declaration,
            // so the declaration's initializer is no longer the variable's value at the
            // loop. Walking past it would resolve a stale literal as the proven ceiling
            // (`int _b = 4; _b = int(runtime); …`). Decline instead.
            if (WritesVariable(stmt, variable))
            {
                return false;
            }

            if (start == 0 || body[start - 1] != ';')
            {
                return false; // crossed a scope boundary ('{'/'}') or reached the start — stop
            }

            pos = start;
        }

        return false;
    }

    /// <summary>
    /// Finds the statement immediately ending before <paramref name="beforeIndex"/>
    /// (skipping trailing whitespace/comments) — i.e. one bounded by a <c>;</c>
    /// terminator, itself preceded by the start of the block (<c>{</c>/<c>}</c>/start-
    /// of-string) or an earlier <c>;</c>. Returns its trimmed text span as
    /// <paramref name="start"/>/<paramref name="semiEnd"/> (the index of its own
    /// terminating <c>;</c>); false if <paramref name="beforeIndex"/> isn't directly
    /// preceded by a <c>;</c>-terminated statement at all.
    /// </summary>
    private static bool TryFindPrecedingStatement(string body, int beforeIndex, out int start, out int semiEnd)
    {
        start = -1;
        semiEnd = -1;

        int end = beforeIndex;
        while (end > 0 && char.IsWhiteSpace(body[end - 1]))
        {
            end--;
        }
        if (end == 0 || body[end - 1] != ';')
        {
            return false;
        }

        semiEnd = end - 1;
        int s = semiEnd;
        while (s > 0 && body[s - 1] != ';' && body[s - 1] != '{' && body[s - 1] != '}')
        {
            s--;
        }

        start = s;
        return true;
    }

    private static readonly Regex IntDeclPattern =
        new(@"^int\s+([A-Za-z_]\w*)\s*=\s*(.+)$", RegexOptions.Singleline);

    /// <summary>
    /// Parses <paramref name="innerBody"/> as EXACTLY one statement,
    /// <c>if (idx CMP boundVar) { trueBranch } else { falseBranch }</c> — nothing before
    /// the <c>if</c>, nothing after the <c>else</c> block but whitespace. <c>idx</c> and
    /// <c>boundVar</c> must each be a simple identifier (SPIRV-Cross's SSA temporaries
    /// always are); anything more complex in the condition declines the match.
    /// </summary>
    private static bool TryParseSoleIfElse(
        string innerBody, out string idxVar, out string cmpOp, out string boundVar,
        out string trueBranch, out string falseBranch)
    {
        idxVar = cmpOp = boundVar = trueBranch = falseBranch = string.Empty;

        int i = SkipWsAndComments(innerBody, 0);
        if (i < 0 || i >= innerBody.Length || !MatchKeyword(innerBody, i, "if"))
        {
            return false;
        }

        int paren = SkipWsAndComments(innerBody, i + 2);
        if (paren < 0 || paren >= innerBody.Length || innerBody[paren] != '(')
        {
            return false;
        }

        int parenClose = FindMatchingParen(innerBody, paren);
        if (parenClose < 0)
        {
            return false;
        }

        var m = IfConditionPattern.Match(innerBody[(paren + 1)..parenClose].Trim());
        if (!m.Success)
        {
            return false;
        }

        int braceOpen = SkipWsAndComments(innerBody, parenClose + 1);
        if (braceOpen < 0 || braceOpen >= innerBody.Length || innerBody[braceOpen] != '{')
        {
            return false;
        }

        int braceClose = FindMatchingBrace(innerBody, braceOpen);
        if (braceClose < 0)
        {
            return false;
        }

        int elseIdx = SkipWsAndComments(innerBody, braceClose + 1);
        if (elseIdx < 0 || !MatchKeyword(innerBody, elseIdx, "else"))
        {
            return false;
        }

        int elseBraceOpen = SkipWsAndComments(innerBody, elseIdx + 4);
        if (elseBraceOpen < 0 || elseBraceOpen >= innerBody.Length || innerBody[elseBraceOpen] != '{')
        {
            return false;
        }

        int elseBraceClose = FindMatchingBrace(innerBody, elseBraceOpen);
        if (elseBraceClose < 0)
        {
            return false;
        }

        int tail = SkipWsAndComments(innerBody, elseBraceClose + 1);
        if (tail < innerBody.Length)
        {
            return false; // something besides whitespace follows the else-block
        }

        idxVar = m.Groups[1].Value;
        cmpOp = m.Groups[2].Value;
        boundVar = m.Groups[3].Value;
        trueBranch = innerBody[(braceOpen + 1)..braceClose];
        falseBranch = innerBody[(elseBraceOpen + 1)..elseBraceClose];
        return true;
    }

    private static readonly Regex IfConditionPattern =
        new(@"^([A-Za-z_]\w*)\s*(<=|>=|<|>)\s*([A-Za-z_]\w*)$");

    /// <summary>
    /// Resolves the provable maximum of a for-header bound expression: a bare integer
    /// literal, or a ternary between exactly two integer literals (<c>cond ? a : b</c>)
    /// — the only two shapes SPIRV-Cross emits for a compile-time-constant trip count.
    /// Anything else (a uniform, a computed expression, a nested ternary) declines —
    /// there is no safe constant to derive.
    /// </summary>
    private static bool TryResolveLiteralMax(string expr, out int max)
    {
        max = 0;
        expr = expr.Trim();

        if (int.TryParse(expr, out int lit))
        {
            max = lit;
            return true;
        }

        var m = TernaryLiteralPattern.Match(expr);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int a) && int.TryParse(m.Groups[2].Value, out int b))
        {
            max = Math.Max(a, b);
            return true;
        }

        return false;
    }

    private static readonly Regex TernaryLiteralPattern =
        new(@"^[A-Za-z_]\w*\s*\?\s*(-?\d+)\s*:\s*(-?\d+)\s*$");

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
