using System.Text.RegularExpressions;
using ShadowDusk.ShaderToy;

namespace ShadowDusk.SkSL;

/// <summary>
/// SkSL → HLSL <c>.fx</c> conversion. SkSL is GLSL-shaped, so this is a thin source-to-source
/// front end: it rewrites SkSL type spellings to GLSL, turns <c>half4 main(float2 coord)</c> into the
/// ShaderToy "returning" entry (flipping Y, since SkSL's origin is top-left and ShaderToy's is
/// bottom-left), then hands the result to <see cref="ShaderToyConverter"/>. Anything the ShaderToy
/// subset rejects is rejected here too, with the ShaderToy diagnostic (columns can shift on lines
/// where a type was respelled; line numbers are preserved).
/// </summary>
public static class SkSLConverter
{
    private const string UserEntry = "skslMain";

    // SkSL type spelling -> GLSL spelling. half/float share a GLSL type (no precision distinction).
    private static readonly Dictionary<string, string> Types = BuildTypes();

    private static readonly Regex Identifier = new(@"\b[A-Za-z_]\w*\b", RegexOptions.Compiled);

    private static readonly Regex EntryDecl = new(
        @"\b(?<ret>half4|float4|half3|float3)\s+main\s*\(\s*(?:const\s+)?(?:in\s+)?(?<ptype>float2|half2)\s+(?<pname>\w+)\s*\)",
        RegexOptions.Compiled);

    /// <summary>Convert an SkSL fragment shader to HLSL <c>.fx</c> text.</summary>
    public static ConvertResult Convert(string skslSource, ConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(skslSource);

        if (!EntryDecl.IsMatch(skslSource))
        {
            return Fail(
                "No 'half4 main(float2 coord)' entry point was found. Only SkSL fragment shaders " +
                "whose main takes a single float2 and returns a color are supported.",
                "main");
        }

        if (EntryDecl.Matches(skslSource).Count > 1)
        {
            return Fail("Multiple 'main' definitions found.", "main");
        }

        string source = EntryDecl.Replace(
            skslSource, m => $"{m.Groups["ret"].Value} {UserEntry}({m.Groups["ptype"].Value} {m.Groups["pname"].Value})");
        source = RewriteChildShaders(source);
        source = Identifier.Replace(source, m => Types.TryGetValue(m.Value, out string? glsl) ? glsl : m.Value);

        // SkSL coords are top-left origin; ShaderToy's fragCoord is bottom-left.
        source += $"\nvec4 mainImage(in vec2 fragCoord) {{ return vec4({UserEntry}(vec2(fragCoord.x, iResolution.y - fragCoord.y))); }}\n";

        return ShaderToyConverter.Convert(source, options ?? new ConvertOptions { EffectName = "SkSLEffect", TechniqueName = "SkSL" });
    }

    private static readonly Regex ChildDecl = new(
        @"\buniform\s+shader\s+(?<n>\w+)\s*;", RegexOptions.Compiled);

    /// <summary>
    /// <c>uniform shader img;</c> becomes a sampler plus a host-driven <c>imgSize</c> (pixels), and
    /// <c>img.eval(p)</c> becomes <c>texture(img, (p) / imgSize)</c>. SkSL's eval takes pixel
    /// coordinates; fx_2_0 cannot query a texture's size, so the size must be a uniform. Both use a
    /// top-left origin, so no flip is needed.
    /// </summary>
    private static string RewriteChildShaders(string source)
    {
        var children = new List<string>();
        source = ChildDecl.Replace(source, m =>
        {
            string n = m.Groups["n"].Value;
            children.Add(n);
            return $"uniform sampler2D {n}; uniform vec2 {n}Size;";
        });

        foreach (string n in children)
        {
            string call = n + ".eval(";
            int at;
            while ((at = source.IndexOf(call, StringComparison.Ordinal)) >= 0)
            {
                int open = at + call.Length;
                int depth = 1, i = open;
                while (i < source.Length && depth > 0)
                {
                    depth += source[i] == '(' ? 1 : source[i] == ')' ? -1 : 0;
                    i++;
                }

                if (depth != 0)
                {
                    break; // unbalanced: leave it for the parser to reject with a located error
                }

                string arg = source.Substring(open, i - 1 - open);
                source = source.Substring(0, at) + $"texture({n}, ({arg}) / {n}Size)" + source.Substring(i);
            }
        }

        return source;
    }

    private static ConvertResult Fail(string message, string construct) => new()
    {
        Success = false,
        Fx = null,
        Diagnostics = new[] { new ConvertDiagnostic(DiagnosticSeverity.Error, message, 0, 0, construct) },
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
