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
/// Uniforms the mapping had to invent (<c>ShadowDusk_Color</c> for <c>COLOR0</c>,
/// <c>ShadowDusk_Resolution</c>, or a varying substituted per
/// <see cref="SkslConvertOptions.TreatVaryingsAsUniforms"/>). The consumer must
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
///   <item><c>COLOR0</c> (SpriteBatch's vertex color) converts <b>by default</b> to the
///   synthesized <c>float4</c> uniform <c>ShadowDusk_Color</c>: the consumer sets it to the
///   sprite's tint each draw (white when untinted). A documented semantic change
///   (interpolated → per-draw constant), warned about (<c>SD0614</c>) and surfaced in
///   <see cref="MappedSksl.SynthesizedUniforms"/>.</item>
///   <item>Any other interpolant is <b>rejected by name</b> (<c>SD0611</c>) unless the caller
///   explicitly lists it in <see cref="SkslConvertOptions.TreatVaryingsAsUniforms"/>, in which
///   case it becomes a uniform named <c>in_var_&lt;SEMANTIC&gt;</c>, with the same warning and
///   surfacing.</item>
///   <item>Sampling at computed coordinates (refused <c>SD0612</c> before issue #371) converts
///   to <c>child.eval((uv) * ShadowDusk_Resolution)</c>: HLSL coordinates are normalized, SkSL's
///   <c>.eval()</c> takes child-space pixels, and the same synthesized uniform carries the
///   child's pixel size (warned, <c>SD0614</c>). Only a two-argument sample of a bound
///   texture is modelled; a bias or extra argument, or an unknown sampler, stays
///   <c>SD0612</c>.</item>
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

    private static readonly Regex RoundEvenCall = new(@"\broundEven\s*\(", RegexOptions.Compiled);

    private const string RoundEvenHelpers =
        "float _sd_roundEven(float x)\n{\n" +
        "    float r = floor(x + 0.5);\n" +
        "    if (r - x == 0.5 && mod(r, 2.0) != 0.0)\n        r -= 1.0;\n" +
        "    return r;\n}\n" +
        "vec2 _sd_roundEven(vec2 x) { return vec2(_sd_roundEven(x.x), _sd_roundEven(x.y)); }\n" +
        "vec3 _sd_roundEven(vec3 x) { return vec3(_sd_roundEven(x.x), _sd_roundEven(x.y), _sd_roundEven(x.z)); }\n" +
        "vec4 _sd_roundEven(vec4 x) { return vec4(_sd_roundEven(x.x), _sd_roundEven(x.y), _sd_roundEven(x.z), _sd_roundEven(x.w)); }\n\n";

    private static readonly Regex TextureCall =new(@"\btexture\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// The synthesized size uniform's name: the pixel size of the element being drawn, which is
    /// also the size of the child textures it samples (the 1:1 post-process case). One value
    /// serves both the arithmetic-UV normalization and computed-coordinate sampling.
    /// </summary>
    internal const string ResolutionUniform = "ShadowDusk_Resolution";

    /// <summary>
    /// The synthesized uniform that stands in for <c>COLOR0</c>, SpriteBatch's vertex color
    /// (<c>float4</c>, straight RGBA). A runtime effect has no varyings, so the consumer sets it
    /// per draw to the sprite's tint: white when untinted, which reproduces the untinted math.
    /// </summary>
    internal const string ColorUniform = "ShadowDusk_Color";

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
        string? colorVar = null;
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

            // COLOR0 (a bare `: COLOR` input reaches here spelled "COLOR", index 0 implied) is
            // SpriteBatch's vertex color. Every .fx written for MonoGame/KNI reads it, so it
            // converts BY DEFAULT to one named, documented uniform instead of refusing. The
            // semantic change (interpolated -> per-draw constant) is the one
            // TreatVaryingsAsUniforms opts into, made the default for this one semantic
            // because every sprite shader has it (issue #368).
            if (semantic is "COLOR0" or "COLOR")
            {
                if (!type.Equals("vec4", StringComparison.Ordinal))
                {
                    return Fail(sourceName, "SD0611",
                        $"the pixel shader reads its vertex color ('{semantic}') as '{type}', but the " +
                        $"synthesized uniform '{ColorUniform}' is a float4 (SpriteBatch's RGBA tint). " +
                        "Declare the COLOR0 input as float4; refusing rather than guessing how to " +
                        "narrow the uniform.");
                }
                colorVar = "in_var_" + semantic;
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
                "SkSL port dropped its COLOR0 tint exactly this way; COLOR0 itself converts, to " +
                $"the uniform '{ColorUniform}'). If a per-draw constant is " +
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

        if (colorVar is not null)
        {
            text = VaryingIn.Replace(text, m =>
                m.Groups["semantic"].Value is "COLOR0" or "COLOR" ? $"uniform vec4 {ColorUniform};" : m.Value);
            text = Regex.Replace(text, $@"\b{Regex.Escape(colorVar)}\b", ColorUniform);
            synthesized.Add(ColorUniform);
            warnings.Add(new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0614",
                Message: $"the vertex color (COLOR0) was converted to the uniform '{ColorUniform}' " +
                         "(float4): SkSL has no vertex stage or varyings, so it is a per-draw " +
                         "constant, not an interpolated value. Your draw code must set it to the " +
                         "sprite's tint each draw (white when untinted), or the shader renders " +
                         "black.",
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

        // Computed coordinates: texture(child, expr) becomes child.eval((expr) * ShadowDusk_Resolution).
        // HLSL's tex2D takes NORMALIZED coordinates; .eval() takes child-space PIXELS, so the
        // computed UV is scaled by the child's pixel size. The consumer binds a child the size of
        // the element being drawn (the 1:1 post-process case, where the interpolated coordinate
        // is already in child space), so ShadowDusk_Resolution is that one size and Gum sets a
        // single value. Anything outside the one modelled shape (two arguments, first one a
        // known child) is refused by name, never guessed. Innermost call first: the last
        // `texture(` in the text never contains another.
        bool needsResolution = false;
        while (true)
        {
            Match? call = null;
            foreach (Match m in TextureCall.Matches(text))
                call = m;
            if (call is null)
                break;

            int open = call.Index + call.Length - 1;
            var args = new List<string>();
            int depth = 0, argStart = open + 1, close = -1;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    if (--depth == 0)
                    {
                        args.Add(text[argStart..i]);
                        close = i;
                        break;
                    }
                }
                else if (c == ',' && depth == 1)
                {
                    args.Add(text[argStart..i]);
                    argStart = i + 1;
                }
            }

            string callText = close < 0 ? text[call.Index..] : text[call.Index..(close + 1)];
            if (close < 0 || args.Count != 2 || !children.Contains(args[0].Trim(), StringComparer.Ordinal))
            {
                return Fail(sourceName, "SD0612",
                    $"the sampling call '{callText.Trim()}' is not a two-argument sample of a bound " +
                    "texture. SkSL's .eval() takes only a coordinate, so a sampling bias or any other " +
                    "extra argument, or a sampler that is not one of the shader's own textures, has no " +
                    "faithful mapping and is refused rather than guessed. Sample with tex2D/Sample at " +
                    "one coordinate.");
            }

            string childName = args[0].Trim();
            string uvExpr = args[1].Trim();
            text = text[..call.Index]
                 + $"{childName}.eval(({uvExpr}) * {ResolutionUniform})"
                 + text[(close + 1)..];
            needsResolution = true;
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
                needsResolution = true;
            }
        }

        if (needsResolution)
        {
            synthesized.Add(ResolutionUniform);
            warnings.Add(new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0614",
                Message: $"the shader uses its texture coordinate arithmetically or samples at computed " +
                         $"coordinates, so the uniform '{ResolutionUniform}' (float2, the pixel size of " +
                         "the element being drawn, which is also the size of the bound child textures) " +
                         "was synthesized: it normalizes SkSL's pixel-space coord and scales computed " +
                         "sampling coordinates back to child pixels. Your draw code must set it.",
                Severity: ShaderErrorSeverity.Warning));
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
                    sb.Append($"uniform {member};").Append('\n');
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

        // HLSL round() is ties-to-even, which DXC emits as GLSL roundEven; SkSL has no
        // roundEven. Emit exact helpers (not floor(x + 0.5), which differs on ties) so the
        // quantizing shaders that need it, like Pixelated, keep HLSL's result.
        if (RoundEvenCall.IsMatch(text))
        {
            text = RoundEvenCall.Replace(text, "_sd_roundEven(");
            text = text.Replace("half4 main(float2 coord)", RoundEvenHelpers + "half4 main(float2 coord)",
                StringComparison.Ordinal);
        }
        // The out variable becomes a local (plus the normalized UV, when synthesized); every
        // `return;` and the closing brace return it.
        string mainLocals = $"    vec4 {outName};";
        if (needsNormalizedUv)
            mainLocals = $"    vec2 _sd_uv = coord / {ResolutionUniform};\n" + mainLocals;
        if (needsResolution)
            text = $"uniform vec2 {ResolutionUniform};\n" + text;
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
