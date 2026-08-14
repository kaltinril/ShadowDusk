#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler.Sksl;

/// <summary>The mapped SkSL plus everything the consumer must know to run it.</summary>
/// <param name="SkslText">The SkSL runtime-effect source.</param>
/// <param name="Warnings">Non-fatal findings (synthesized uniforms, substituted varyings).</param>
/// <param name="ChildShaders">
/// The <c>uniform shader</c> children, in declaration order — one per (texture, sampler) pair
/// the HLSL sampled, named after the HLSL texture. The consumer binds each with
/// <c>SKRuntimeEffect.Uniforms</c>/children before creating the paint.
/// </param>
/// <param name="SynthesizedUniforms">
/// Uniforms the mapping had to invent (e.g. <c>ShadowDusk_Resolution</c>, or a varying
/// substituted per <see cref="SkslConvertOptions.TreatVaryingsAsUniforms"/>). The consumer must
/// set every one of these each frame or the effect renders wrong — which is why each also
/// carries a warning.
/// </param>
public sealed record MappedSksl(
    string SkslText,
    IReadOnlyList<ShaderError> Warnings,
    IReadOnlyList<string> ChildShaders,
    IReadOnlyList<string> SynthesizedUniforms);

/// <summary>
/// Maps SPIRV-Cross's modern GLSL (the §2.3 pipeline seam, BEFORE the MonoGame rewriter) to an
/// SkSL runtime effect. This is the "convention mapper" Phase 62 §2.4 said must not be
/// hand-waved, built on the owner-accepted evidence decision (2026-08-13): the emitted SkSL is
/// judged by rendered-image fidelity, and anything that cannot be mapped **faithfully** is
/// rejected loudly — never emitted compiles-but-renders-wrong.
///
/// <para><b>The conversion contract, shaped by Gum's own hand-port</b> (Phase 62 §2.6: their
/// SkSL silently drops the <c>* input.Color</c> its <c>.fx</c> applies — precisely what an
/// automated tool must never do):</para>
/// <list type="bullet">
///   <item><c>TEXCOORD0</c> is the one interpolant SkSL can supply, via <c>coord</c>. Sampling
///   at exactly the interpolated UV maps to <c>child.eval(coord)</c> (the 1:1 post-process
///   case). Using the UV <i>arithmetically</i> maps to <c>coord / ShadowDusk_Resolution</c>
///   with a synthesized uniform the consumer must set (warned, never silent).</item>
///   <item>Any other interpolant (<c>COLOR0</c>, …) is <b>rejected by name</b> (<c>SD0611</c>)
///   unless the caller explicitly lists it in
///   <see cref="SkslConvertOptions.TreatVaryingsAsUniforms"/>, in which case it becomes a
///   uniform — a documented semantic change (interpolated → per-draw constant), opted into,
///   warned about, and surfaced in <see cref="MappedSksl.SynthesizedUniforms"/>.</item>
///   <item>Sampling at computed coordinates is rejected (<c>SD0612</c>): SkSL's
///   <c>.eval()</c> takes child-space pixel coordinates and a child's bounds are unknowable
///   from inside the effect, so any guess could silently sample the wrong texel.</item>
///   <item>Constructs with no SkSL meaning — <c>gl_*</c> builtins, derivatives, LOD/offset
///   sampling — are rejected by name (<c>SD0613</c>).</item>
/// </list>
/// </summary>
internal static class SkslGlslMapper
{
    private static readonly Regex VersionOrExtension = new(
        @"^\s*#(version|ifdef|extension|endif)\b.*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CombinedSampler = new(
        @"^\s*uniform\s+sampler2D\s+(?<name>\w+)\s*;\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex VaryingIn = new(
        @"^\s*in\s+(?<type>\w+)\s+in_var_(?<semantic>\w+)\s*;\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex FragmentOut = new(
        @"^\s*(layout\s*\([^)]*\)\s*)?out\s+vec4\s+(?<name>\w+)\s*;\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex UniformBlock = new(
        @"^\s*(layout\s*\([^)]*\)\s*)?uniform\s+(?<block>\w+)\s*\{(?<members>[^}]*)\}\s*(?<instance>\w*)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Constructs with no SkSL runtime-effect meaning. Derivatives and LOD sampling genuinely
    // have no equivalent; gl_* builtins reference pipeline state a runtime effect does not have.
    private static readonly Regex Unmappable = new(
        @"\b(gl_\w+|dFdx|dFdy|fwidth|textureLod|textureProj|textureGrad|textureOffset|texelFetch)\b",
        RegexOptions.Compiled);

    /// <summary>The synthesized viewport-size uniform's name.</summary>
    internal const string ResolutionUniform = "ShadowDusk_Resolution";

    /// <summary>
    /// Maps one pixel shader's SPIRV-Cross GLSL to SkSL.
    /// </summary>
    /// <param name="glsl">The raw SPIRV-Cross GLSL (never the MonoGame-rewritten dialect).</param>
    /// <param name="textureNamesInDeclarationOrder">
    /// The HLSL texture name behind each combined sampler, in SPIR-V declaration order (from
    /// <c>SpirvCombinedSamplerPairs</c> — the same extraction the GL sampler table trusts).
    /// </param>
    /// <param name="treatVaryingsAsUniforms">Semantics the caller opted into substituting.</param>
    /// <param name="sourceName">For diagnostics.</param>
    public static Result<MappedSksl, ShaderError[]> Map(
        string glsl,
        IReadOnlyList<string> textureNamesInDeclarationOrder,
        IReadOnlySet<string> treatVaryingsAsUniforms,
        string sourceName)
    {
        var warnings = new List<ShaderError>();
        var children = new List<string>();
        var synthesized = new List<string>();

        // 0. Constructs with no faithful mapping — checked first, on the whole text, so nothing
        //    below can partially transform a shader that is going to be rejected anyway.
        Match unmappable = Unmappable.Match(glsl);
        if (unmappable.Success)
        {
            return Fail(sourceName, "SD0613",
                $"'{unmappable.Value}' has no SkSL runtime-effect equivalent (no pipeline builtins, " +
                "no derivatives, no LOD/offset sampling in a runtime effect). The shader cannot be " +
                "converted faithfully, so it is refused rather than approximated.");
        }

        string text = VersionOrExtension.Replace(glsl, "");

        // 1. Combined samplers -> child shaders, named after the HLSL textures.
        var samplerRenames = new List<(string From, string To)>();
        int samplerIndex = 0;
        text = CombinedSampler.Replace(text, m =>
        {
            string hlslName = samplerIndex < textureNamesInDeclarationOrder.Count
                ? textureNamesInDeclarationOrder[samplerIndex]
                : m.Groups["name"].Value;
            samplerIndex++;
            children.Add(hlslName);
            samplerRenames.Add((m.Groups["name"].Value, hlslName));
            return $"uniform shader {hlslName};";
        });
        foreach ((string from, string to) in samplerRenames)
            text = Regex.Replace(text, $@"\b{Regex.Escape(from)}\b", to);

        // 2. Varyings. TEXCOORD0 is representable; everything else is the Gum lesson.
        string? uvVar = null;
        var uniformSubstitutions = new List<(string Var, string Type, string Semantic)>();
        foreach (Match varying in VaryingIn.Matches(text))
        {
            string semantic = varying.Groups["semantic"].Value;
            string type = varying.Groups["type"].Value;

            if (semantic.Equals("TEXCOORD0", StringComparison.Ordinal))
            {
                uvVar = "in_var_" + semantic;
                continue;
            }

            if (treatVaryingsAsUniforms.Contains(semantic))
            {
                uniformSubstitutions.Add(("in_var_" + semantic, type, semantic));
                continue;
            }

            return Fail(sourceName, "SD0611",
                $"the pixel shader reads the interpolant '{semantic}', and an SkSL runtime effect " +
                "has no varyings at all — a pixel shader gets the coordinate plus uniforms and " +
                "nothing else. Refusing rather than silently dropping it (Gum's own hand-written " +
                "SkSL port dropped its COLOR0 tint exactly this way). If a per-draw constant is " +
                $"acceptable for '{semantic}', opt in with TreatVaryingsAsUniforms and set the " +
                "uniform from your draw code.");
        }

        foreach ((string var, string type, string semantic) in uniformSubstitutions)
        {
            text = VaryingIn.Replace(text, m =>
                m.Groups["semantic"].Value == semantic ? $"uniform {type} {var};" : m.Value);
            synthesized.Add(var);
            warnings.Add(new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0614",
                Message: $"the interpolant '{semantic}' was substituted with the uniform '{var}' " +
                         "(TreatVaryingsAsUniforms): it is now a per-draw constant, not an " +
                         "interpolated value, and your draw code must set it.",
                Severity: ShaderErrorSeverity.Warning));
        }

        // 3. Sampling. child.eval(coord) for the 1:1 case; computed coordinates are refused.
        if (uvVar is not null)
        {
            foreach (string child in children)
            {
                text = Regex.Replace(text,
                    $@"\btexture\s*\(\s*{Regex.Escape(child)}\s*,\s*{Regex.Escape(uvVar)}\s*\)",
                    $"{child}.eval(coord)");
            }
        }
        Match computedSample = Regex.Match(text, @"\btexture\s*\(\s*(?<child>\w+)\s*,");
        if (computedSample.Success)
        {
            return Fail(sourceName, "SD0612",
                $"'{computedSample.Groups["child"].Value}' is sampled at computed coordinates. " +
                "SkSL's .eval() takes CHILD-SPACE PIXEL coordinates, and a runtime effect cannot " +
                "know a child's bounds, so converted computed-UV sampling could silently read the " +
                "wrong texels — refused rather than guessed. Sample at the interpolated TEXCOORD0, " +
                "or restructure the effect so the coordinate math happens in your draw code.");
        }

        // 4. Any remaining arithmetic use of the UV becomes normalized coord — which needs the
        //    output bounds, which a runtime effect cannot know: synthesize the uniform, loudly.
        bool needsNormalizedUv = false;
        if (uvVar is not null)
        {
            text = VaryingIn.Replace(text, m =>
                m.Groups["semantic"].Value == "TEXCOORD0" ? "" : m.Value);

            if (Regex.IsMatch(text, $@"\b{Regex.Escape(uvVar)}\b"))
            {
                text = Regex.Replace(text, $@"\b{Regex.Escape(uvVar)}\b", "_sd_uv");
                needsNormalizedUv = true;
                synthesized.Add(ResolutionUniform);
                warnings.Add(new ShaderError(
                    File: sourceName, Line: 0, Column: 0, Code: "SD0614",
                    Message: $"the shader uses its texture coordinate arithmetically, so the uniform " +
                             $"'{ResolutionUniform}' (float2, the output size in pixels) was synthesized " +
                             "to normalize SkSL's pixel-space coord. Your draw code must set it.",
                    Severity: ShaderErrorSeverity.Warning));
            }
        }

        // 5. Uniform blocks -> loose SkSL uniforms (SkSL has no UBOs). Two shapes appear:
        //    an anonymous block (cbuffer members referenced unqualified — nothing to fix at use
        //    sites) and an INSTANCED block (`uniform type_Globals { ... } _Globals;`, which is
        //    how DXC's $Globals cbuffer for loose HLSL globals comes through), whose qualified
        //    `_Globals.X` accesses must lose the qualifier along with the wrapper.
        var instanceQualifiers = new List<string>();
        text = UniformBlock.Replace(text, m =>
        {
            string instance = m.Groups["instance"].Value;
            if (instance.Length > 0)
                instanceQualifiers.Add(instance);

            var sb = new StringBuilder();
            foreach (string line in m.Groups["members"].Value.Split('\n'))
            {
                string member = line.Trim().TrimEnd(';');
                if (member.Length > 0)
                    sb.AppendLine($"uniform {member};");
            }
            return sb.ToString();
        });
        foreach (string instance in instanceQualifiers)
            text = Regex.Replace(text, $@"\b{Regex.Escape(instance)}\s*\.\s*", "");

        // 6. Entry point: void main() writing an out var -> half4 main(float2 coord) returning.
        Match outVar = FragmentOut.Match(text);
        if (!outVar.Success)
        {
            return Fail(sourceName, "SD0613",
                "no single vec4 fragment output found in the transpiled GLSL — an SkSL runtime " +
                "effect returns exactly one color. (MRT pixel shaders cannot be converted.)");
        }
        string outName = outVar.Groups["name"].Value;
        text = FragmentOut.Replace(text, "");

        text = text.Replace("void main()", "half4 main(float2 coord)", StringComparison.Ordinal);
        // The out variable becomes a local (plus the normalized UV, when synthesized); every
        // `return;` and the closing brace return it.
        string mainLocals = $"    vec4 {outName};";
        if (needsNormalizedUv)
        {
            mainLocals = $"    vec2 _sd_uv = coord / {ResolutionUniform};\n" + mainLocals;
            text = $"uniform vec2 {ResolutionUniform};\n" + text;
        }
        text = Regex.Replace(text,
            @"half4 main\(float2 coord\)\s*\{",
            $"half4 main(float2 coord)\n{{\n{mainLocals}");
        text = Regex.Replace(text, @"\breturn\s*;", $"return half4({outName});");
        int closing = text.LastIndexOf('}');
        text = text[..closing] + $"    return half4({outName});\n}}" + text[(closing + 1)..];

        string sksl = "// Generated by ShadowDusk's SkSL converter (HLSL -> DXC -> SPIRV-Cross -> SkSL).\n"
                    + "// Feed to SKRuntimeEffect.CreateShader; bind each `uniform shader` child and every\n"
                    + "// synthesized uniform from your draw code.\n"
                    + CollapseBlankLines(text.Trim()) + "\n";

        return Result<MappedSksl, ShaderError[]>.Ok(
            new MappedSksl(sksl, warnings, children, synthesized));
    }

    private static Result<MappedSksl, ShaderError[]> Fail(string file, string code, string message) =>
        Result<MappedSksl, ShaderError[]>.Fail(
            [new ShaderError(File: file, Line: 0, Column: 0, Code: code, Message: message)]);

    private static string CollapseBlankLines(string text) =>
        Regex.Replace(text, @"(\r?\n){3,}", "\n\n");
}
