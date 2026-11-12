using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.ShaderToy;

namespace ShadowDusk.SkSL;

/// <summary>
/// Converts an SkSL runtime-effect shader (SkSL in) to HLSL <c>.fx</c> text (out). SkSL is
/// GLSL-shaped, so this is a thin source-to-source frontend: it rewrites SkSL spellings to GLSL,
/// turns <c>half4 main(float2 coord)</c> into the entry of <see cref="ShaderToyConverter"/>, and
/// lowers <c>uniform shader</c> children to textures. Not to be confused with
/// <c>ShadowDusk.Compiler.Sksl.SkslConverter</c>, which goes the other way (<c>.fx</c> to SkSL).
/// </summary>
/// <remarks>
/// <para><b>Host contract</b> (what a MonoGame/KNI consumer does to get Skia's image):</para>
/// <list type="bullet">
/// <item>Draw a fullscreen quad whose POSITION is already in NDC, and set <c>iResolution</c> to the
/// render-target size in pixels (<c>Vector3(width, height, 1)</c>). <c>main</c> receives the pixel
/// center with a top-left origin, as in Skia when the shader is drawn untransformed.</item>
/// <item>For each <c>uniform shader img;</c> child, bind the texture to <c>imgTexture</c> and set
/// <c>imgTexelSize</c> to <c>1 / (width, height)</c> of that texture. <c>img.eval(p)</c> samples at
/// <c>p * imgTexelSize</c>, so SkSL's pixel coordinates carry over. Use the sampler state Skia would
/// (e.g. <c>SamplerState.LinearClamp</c> for a clamped, linearly filtered image shader).</item>
/// <item><b>Alpha:</b> SkSL works in premultiplied alpha (<c>eval</c> returns premultiplied color and
/// <c>main</c> returns premultiplied color). The <c>.fx</c> has two techniques, so either texture
/// convention renders what Skia's <c>SrcOver</c> renders with no conversion by the host:
/// <see cref="PremultipliedTechniqueName"/> (first, so MonoGame's default) for premultiplied textures
/// drawn with <c>BlendState.AlphaBlend</c> (MonoGame's content-pipeline default), and
/// <see cref="StraightAlphaTechniqueName"/> for straight-alpha textures (<c>Texture2D.FromStream</c>,
/// Gum with <c>UsePremultipliedAlpha</c> off) drawn with <c>BlendState.NonPremultiplied</c>. The
/// straight technique premultiplies each child sample after filtering, as Skia does for a
/// straight-alpha image, and un-premultiplies the result. Both match Skia's color; with
/// <c>NonPremultiplied</c> the destination alpha follows that blend state's own equation
/// (<c>a*a + d*(1-a)</c>, where <c>SrcOver</c> gives <c>a + d*(1-a)</c>), which matters only if the
/// target's alpha is used later. A shader that returns <c>rgb &gt; a</c> is outside SkSL's contract
/// and is not guaranteed to match.</item>
/// </list>
/// <para>Unsupported SkSL (<c>sk_FragCoord</c>, <c>layout(color)</c>, color filter and blender entries
/// or children, <c>shader</c> parameters, the color-space built-ins) fails with an SkSL diagnostic
/// that says what to change. Diagnostics from the ShaderToy stage keep the SkSL line number; the
/// column can shift on a line where a type was respelled.</para>
/// </remarks>
public static class SkSLToFxConverter
{
    /// <summary>The effect name used when the caller does not set one.</summary>
    public const string DefaultEffectName = "SkSLEffect";

    /// <summary>The technique name used when the caller does not set one.</summary>
    public const string DefaultTechniqueName = "SkSL";

    /// <summary>
    /// The default technique: premultiplied textures in, premultiplied color out, drawn with
    /// <c>BlendState.AlphaBlend</c>. It is the first technique, so it is MonoGame's default
    /// <c>CurrentTechnique</c>. Equals <see cref="DefaultTechniqueName"/>.
    /// </summary>
    public const string PremultipliedTechniqueName = DefaultTechniqueName;

    /// <summary>
    /// The straight-alpha technique: straight-alpha textures in, straight color out, drawn with
    /// <c>BlendState.NonPremultiplied</c> (e.g. Gum with <c>UsePremultipliedAlpha</c> off). When the
    /// caller sets <see cref="ConvertOptions.TechniqueName"/>, it is that name plus
    /// <see cref="StraightAlphaTechniqueSuffix"/>.
    /// </summary>
    public const string StraightAlphaTechniqueName = DefaultTechniqueName + StraightAlphaTechniqueSuffix;

    /// <summary>Appended to the technique name to name the straight-alpha technique.</summary>
    public const string StraightAlphaTechniqueSuffix = "StraightAlpha";

    private const string TexelSizeSuffix = "TexelSize";
    private const string ChildColor = "sd_ChildColor";
    private const string StraightAlphaFlag = "sd_StraightAlpha";

    private const string ChildColorHelper =
        $" bool {StraightAlphaFlag}; vec4 {ChildColor}(vec4 c) {{ return {StraightAlphaFlag} ? vec4(c.rgb * c.a, c.a) : c; }}";

    private static readonly ConvertOptions ShaderToyDefaults = new();

    // SkSL type spelling -> GLSL spelling. half/float share a GLSL type (no precision distinction).
    private static readonly Dictionary<string, string> Types = BuildTypes();

    private static readonly Regex Identifier = new(@"\b[A-Za-z_]\w*\b", RegexOptions.Compiled);

    private static readonly Regex MainCall = new(@"\bmain\s*\(", RegexOptions.Compiled);

    private static readonly Regex EntryDecl = new(
        @"\b(?:half4|float4|vec4)\s+main\s*\(\s*(?:const\s+)?(?:in\s+)?(?:float2|half2|vec2)\s+(?<pname>\w+)\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex ChildDecl = new(@"\buniform\s+shader\s+(?<n>\w+)\s*;", RegexOptions.Compiled);

    private static readonly Regex AnyEval = new(@"\b(?<n>\w+)\s*\.\s*eval\s*\(", RegexOptions.Compiled);

    private static readonly Regex NonSquareMatrix = new(@"^(?:half|float)([2-4])x([2-4])$", RegexOptions.Compiled);

    private static readonly HashSet<string> ColorVectors = new(StringComparer.Ordinal) { "half4", "float4", "vec4" };

    // Identifiers the ShaderToy stage gives a meaning of its own; SkSL source must not use them.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "mainImage", "glsl_mod", "openfl_TextureCoordv", "openfl_TextureSize",
    };

    /// <summary>Convert an SkSL shader to HLSL <c>.fx</c> text.</summary>
    /// <param name="skslSource">The SkSL source (a runtime-effect shader, <c>half4 main(float2)</c>).</param>
    /// <param name="options">
    /// Optional overrides. Fields left at their defaults keep the SkSL defaults
    /// (<see cref="DefaultEffectName"/>, <see cref="DefaultTechniqueName"/>).
    /// <see cref="ConvertOptions.CommonSource"/> is not supported.
    /// </param>
    public static ConvertResult Convert(string skslSource, ConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(skslSource);

        options ??= ShaderToyDefaults;
        if (options.CommonSource is not null)
        {
            return Fail(new[] { Error("CommonSource is a ShaderToy option; SkSL has no Common tab.", 0, 0, "CommonSource") });
        }

        options = options with
        {
            EffectName = options.EffectName == ShaderToyDefaults.EffectName ? DefaultEffectName : options.EffectName,
            TechniqueName = options.TechniqueName == ShaderToyDefaults.TechniqueName ? DefaultTechniqueName : options.TechniqueName,
            SkSLEntry = true,
        };

        // Comments are blanked in place (same length, newlines kept), so every scan below sees only
        // code and reports line/column values valid for the original source.
        string source = ShaderToyConverter.BlankComments(skslSource);

        var diagnostics = new List<ConvertDiagnostic>();
        List<string> children = ChildDecl.Matches(source).Select(m => m.Groups["n"].Value).ToList();
        CheckEntry(source, diagnostics);
        ScanIdentifiers(source, children, diagnostics);
        if (diagnostics.Count == 0)
        {
            source = RewriteEvals(source, children, diagnostics);
        }

        if (diagnostics.Count > 0)
        {
            return Fail(diagnostics);
        }

        // Each child becomes a sampler plus its host-set texel size. The child-sample helper follows the
        // last declaration (same line, so later line numbers hold): the straight-alpha technique sets its
        // flag to premultiply each sample, as Skia does when it samples a straight-alpha image.
        int lastChild = children.Count;
        source = ChildDecl.Replace(source, m =>
            $"uniform sampler2D {m.Groups["n"].Value}; uniform vec2 {m.Groups["n"].Value}{TexelSizeSuffix};" +
            (--lastChild == 0 ? ChildColorHelper : string.Empty));
        source = EntryDecl.Replace(source, m => $"vec4 mainImage(in vec2 {m.Groups["pname"].Value})", 1);
        source = Identifier.Replace(source, m => Types.TryGetValue(m.Value, out string? glsl) ? glsl : m.Value);

        return ShaderToyConverter.Convert(source, options);
    }

    /// <summary>Require exactly one <c>half4 main(float2)</c>; name the SkSL shape of anything else.</summary>
    private static void CheckEntry(string source, List<ConvertDiagnostic> diagnostics)
    {
        MatchCollection mains = MainCall.Matches(source);
        if (mains.Count == 0)
        {
            diagnostics.Add(Error(
                "No 'half4 main(float2 coord)' entry point was found. Only SkSL shaders (not color filters " +
                "or blenders) are supported.", 0, 0, "main"));
            return;
        }

        if (mains.Count > 1)
        {
            Locate(source, mains[1].Index, out int l, out int c);
            diagnostics.Add(Error("Multiple 'main' definitions found.", l, c, "main"));
            return;
        }

        if (EntryDecl.IsMatch(source))
        {
            return;
        }

        Locate(source, mains[0].Index, out int line, out int col);
        string[] parameters = ParameterTypes(source, mains[0].Index + mains[0].Length);
        if (parameters.Length == 1 && ColorVectors.Contains(parameters[0]))
        {
            diagnostics.Add(Error(
                "This is an SkSL color filter ('half4 main(half4 color)'); only shaders ('half4 main(float2 " +
                "coord)') are supported. Apply the filter's math to the color inside a shader instead.",
                line, col, "main"));
        }
        else if (parameters.Length == 2 && ColorVectors.Contains(parameters[0]) && ColorVectors.Contains(parameters[1]))
        {
            diagnostics.Add(Error(
                "This is an SkSL blender ('half4 main(half4 src, half4 dst)'); only shaders ('half4 main(float2 " +
                "coord)') are supported. A blender has no MonoGame equivalent: use a BlendState, or a shader " +
                "that samples the destination as a child.",
                line, col, "main"));
        }
        else
        {
            diagnostics.Add(Error(
                "An SkSL shader's entry must be 'half4 main(float2 coord)' (or float4/vec4 and half2/vec2): " +
                "it takes one coordinate and returns a premultiplied color.",
                line, col, "main"));
        }
    }

    /// <summary>The type word of each comma-separated parameter after <c>main(</c> (qualifiers skipped).</summary>
    private static string[] ParameterTypes(string source, int open)
    {
        int close = source.IndexOf(')', open);
        string list = close < 0 ? source[open..] : source[open..close];
        return list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(w => w is not ("const" or "in" or "out" or "inout")) ?? string.Empty)
            .ToArray();
    }

    /// <summary>Reject identifiers this frontend does not support or reserves, once per name, at the first use.</summary>
    private static void ScanIdentifiers(string source, List<string> children, List<ConvertDiagnostic> diagnostics)
    {
        var childDeclShaderKeywords = new HashSet<int>(
            ChildDecl.Matches(source).Select(m => source.IndexOf("shader", m.Index, StringComparison.Ordinal)));
        var texelSizes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string child in children)
        {
            texelSizes[child + TexelSizeSuffix] = child;
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Identifier.Matches(source))
        {
            if (m.Value == "shader" && childDeclShaderKeywords.Contains(m.Index))
            {
                continue;
            }

            string? message = Unsupported(m.Value, texelSizes);
            if (message is not null && reported.Add(m.Value))
            {
                Locate(source, m.Index, out int line, out int col);
                diagnostics.Add(Error(message, line, col, m.Value));
            }
        }

        foreach (Match m in AnyEval.Matches(source))
        {
            string name = m.Groups["n"].Value;
            if (!children.Contains(name) && reported.Add(name + ".eval"))
            {
                Locate(source, m.Index, out int line, out int col);
                diagnostics.Add(Error(
                    $"'{name}.eval' does not name a 'uniform shader' child; only globals declared " +
                    "'uniform shader name;' can be evaluated.",
                    line, col, name + ".eval"));
            }
        }
    }

    private static string? Unsupported(string name, Dictionary<string, string> texelSizes)
    {
        switch (name)
        {
            case "sk_FragCoord":
                return "sk_FragCoord is not supported yet; use the coordinate passed to main (equal to the " +
                       "pixel coordinate when the shader is drawn untransformed).";
            case "layout":
                return "layout(color) uniforms are not supported yet; declare a plain half4/float4 uniform " +
                       "and set it to the color already in the destination's color space (for an untagged " +
                       "surface, the sRGB values).";
            case "unpremul":
                return "unpremul() is not supported yet; divide instead: half4(c.rgb / max(c.a, 0.0001), c.a).";
            case "toLinearSrgb":
            case "fromLinearSrgb":
                return $"{name}() is not supported: the converted shader has no working color space. Do the " +
                       "conversion on the host or write the transfer function out.";
            case "colorFilter":
            case "blender":
                return $"'{name}' children are not supported yet; only 'uniform shader' children are.";
            case "shader":
                return "'shader' is only supported as a global 'uniform shader name;' evaluated with " +
                       "name.eval(coord). To pass a child to a function, call eval at the call site and " +
                       "pass the color.";
        }

        if (name.StartsWith("sk_", StringComparison.Ordinal))
        {
            return $"'{name}' is not an SkSL built-in this converter supports.";
        }

        if (name.StartsWith("sd_", StringComparison.Ordinal))
        {
            return $"'{name}' is reserved: the 'sd_' prefix belongs to the SkSL-to-.fx conversion. Rename this identifier.";
        }

        if (name.StartsWith("gl_", StringComparison.Ordinal))
        {
            return $"'{name}' is a GLSL built-in, not SkSL; use the coordinate passed to main.";
        }

        if (texelSizes.TryGetValue(name, out string? child))
        {
            return $"'{name}' is reserved: the converter declares it for child shader '{child}' (the host " +
                   "sets it to 1 / the child's size in pixels). Rename this identifier.";
        }

        if (UniformInfo.IsUniform(name) || ReservedNames.Contains(name))
        {
            return $"'{name}' is reserved by the SkSL-to-.fx conversion (the host drives it); rename this identifier.";
        }

        Match nonSquare = NonSquareMatrix.Match(name);
        if (nonSquare.Success && nonSquare.Groups[1].Value != nonSquare.Groups[2].Value)
        {
            return $"Non-square matrix types ('{name}') are not supported yet.";
        }

        return null;
    }

    /// <summary>
    /// <c>img.eval(p)</c> becomes <c>sd_ChildColor(texture(img, (p) * imgTexelSize))</c>. Matches are rewritten right to
    /// left, so each match still sits at its original index (diagnostics stay located) and a nested eval
    /// inside an argument is already rewritten when its enclosing call is copied.
    /// </summary>
    private static string RewriteEvals(string source, List<string> children, List<ConvertDiagnostic> diagnostics)
    {
        if (children.Count == 0)
        {
            return source;
        }

        var call = new Regex(@"\b(?<n>" + string.Join("|", children.Select(Regex.Escape)) + @")\s*\.\s*eval\s*\(");
        var sb = new StringBuilder(source);
        foreach (Match m in call.Matches(source).Reverse())
        {
            int open = m.Index + m.Length;
            int depth = 1, i = open;
            while (i < sb.Length && depth > 0)
            {
                depth += sb[i] == '(' ? 1 : sb[i] == ')' ? -1 : 0;
                i++;
            }

            string n = m.Groups["n"].Value;
            if (depth != 0)
            {
                Locate(source, m.Index, out int line, out int col);
                diagnostics.Add(Error($"Unbalanced parentheses in '{n}.eval('.", line, col, n + ".eval"));
                return source;
            }

            string arg = sb.ToString(open, i - 1 - open).Trim();
            sb.Remove(m.Index, i - m.Index);
            sb.Insert(m.Index, $"{ChildColor}(texture({n}, ({arg}) * {n}{TexelSizeSuffix}))");
        }

        return sb.ToString();
    }

    private static void Locate(string text, int index, out int line, out int col)
    {
        line = 1;
        col = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                col = 1;
            }
            else if (text[i] != '\r')
            {
                col++;
            }
        }
    }

    private static ConvertDiagnostic Error(string message, int line, int col, string construct) =>
        new(DiagnosticSeverity.Error, message, line, col, construct);

    private static ConvertResult Fail(IReadOnlyList<ConvertDiagnostic> diagnostics) => new()
    {
        Success = false,
        Fx = null,
        Diagnostics = diagnostics,
        UsedUniforms = Array.Empty<string>(),
    };

    private static Dictionary<string, string> BuildTypes()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["half"] = "float",
            ["short"] = "int",
            ["ushort"] = "uint",
        };

        foreach (string scalar in new[] { "half", "float" })
        {
            for (int n = 2; n <= 4; n++)
            {
                d[$"{scalar}{n}"] = $"vec{n}";
                d[$"{scalar}{n}x{n}"] = $"mat{n}";
            }
        }

        for (int n = 2; n <= 4; n++)
        {
            d[$"int{n}"] = $"ivec{n}";
            d[$"short{n}"] = $"ivec{n}";
            d[$"uint{n}"] = $"uvec{n}";
            d[$"ushort{n}"] = $"uvec{n}";
            d[$"bool{n}"] = $"bvec{n}";
        }

        return d;
    }
}
