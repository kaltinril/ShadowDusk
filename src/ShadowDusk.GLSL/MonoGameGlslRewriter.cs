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

    // out vec4 out_var_SV_Target<N?>;
    private static readonly Regex OutputDecl = new(
        @"^\s*out\s+vec4\s+(out_var_SV_Target[0-9]*)\s*;\s*$",
        RegexOptions.Compiled);

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

        // Rule 5: output uses.
        // out_var_SV_Target<N> -> gl_FragData[<N>]; out_var_SV_Target -> gl_FragColor.
        body = Regex.Replace(body, @"\bout_var_SV_Target([0-9]+)\b", "gl_FragData[$1]");
        body = Regex.Replace(body, @"\bout_var_SV_Target\b", "gl_FragColor");

        // Rule 6: texture functions.
        body = Regex.Replace(body, @"\btextureLod\s*\(", "texture2DLod(");
        body = Regex.Replace(body, @"\btextureProj\s*\(", "texture2DProj(");
        body = Regex.Replace(body, @"\btexture\s*\(", "texture2D(");

        // ---- Assemble final output: precision header + body. ----
        // Trim leading blank lines from the body so the header sits at the top,
        // preserving a single blank line separation.
        var trimmedBody = body.TrimStart('\n');
        var finalGlsl = PrecisionHeader + "\n" + trimmedBody;
        if (!finalGlsl.EndsWith("\n"))
        {
            finalGlsl += "\n";
        }

        return new MonoGameGlslResult(finalGlsl, samplers, uniformMembers.Count);
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
}
