#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.Slang;

/// <summary>
/// Issue #230: respells slangc's DX10-style texture objects in the DX9 effect syntax the FNA
/// (<c>fx_2_0</c>) target needs, changing nothing else.
/// </summary>
/// <remarks>
/// <para>slangc's <c>-target hlsl</c> always emits texture objects:
/// <c>Texture2D&lt;float4&gt; T; SamplerState S; ... T.Sample(S, uv)</c>. Under the FNA target
/// that text compiled (vkd3d accepts it at <c>ps_3_0</c>, combining the pair into one sampler
/// named <c>S+T</c> whose CTAB type is the TEXTURE type) and the resulting effect CRASHED real
/// FNA on the first draw (<c>NotImplementedException: Unhandled sampler state!
/// MOJOSHADER_SAMP_UNKNOWN1</c>, measured on all 12 textured shaders of the 21-shader corpus),
/// because the parameter table then holds a texture where MojoShader expects a sampler.
/// Microsoft's own <c>fxc /T fx_2_0</c> refuses the same text outright ("This sampler is used
/// with a DX10-style texture intrinsic").</para>
/// <para>The DX9 spelling of the same program is mechanical: the texture becomes an effect
/// <c>texture2D</c> of the same name, the sampler a <c>sampler2D</c> (same name, same register
/// if the author wrote one) bound to it through <c>sampler_state { Texture = &lt;T&gt;; }</c>,
/// and each <c>T.Sample(S, uv)</c> a <c>tex2D(S, uv)</c>, which is the same SM3 <c>texld</c>.
/// That is exactly what a hand-written FNA <c>.fx</c> says, so the parameter table FNA sees
/// (texture <c>T</c>, sampler <c>S</c> with a Texture state) matches what <c>fxc</c> builds
/// from it. Every <c>texture2D</c> declaration is placed ahead of every sampler, because slangc
/// can emit a sampler first (one hoisted out of a struct or <c>ParameterBlock</c>) and
/// <c>fxc</c> rejects a <c>sampler_state</c> that names a texture declared later.</para>
/// <para>Anything texture-shaped this does not model is rejected as <c>SD0627</c> by name, at
/// the Slang source line slangc's <c>#line</c> directives give, never passed through: a texture
/// or sampler passed as a function parameter, a subscript load (<c>T[uint2(...)]</c>),
/// <c>SampleLevel</c>/<c>SampleGrad</c>/<c>Load</c>/<c>Gather</c>, a non-2D texture, one sampler
/// shared by two textures, a <c>Sample</c> call with an offset argument, a sampler no
/// <c>T.Sample(S, uv)</c> uses directly, or a non-zero register space.</para>
/// </remarks>
internal static class SlangFx2TextureRespeller
{
    /// <summary>The diagnostic code for a texture construct this respelling does not model.</summary>
    public const string UnsupportedCode = "SD0627";

    /// <summary>What a respelling attempt produced: the DX9 text, or the unmodeled construct
    /// (a noun phrase for the <c>SD0627</c> message) and its Slang source line (0 when the
    /// emission carries no <c>#line</c> back to the source).</summary>
    public sealed record Result(string? Text, string? Unsupported, int SourceLine);

    private static readonly Regex TextureDecl = new(
        """^[ \t]*Texture2D\b(?:\s*<[^>;{}]*>)?\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\([^)]*\))?\s*;[ \t]*(?=\r?$)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SamplerDecl = new(
        """^[ \t]*SamplerState\b\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*(?<reg>s\d+)\s*(?:,\s*space(?<space>\d+)\s*)?\))?\s*;[ \t]*(?=\r?$)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex RespelledSampler = new(
        """^[ \t]*sampler2D\s""", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SampleCall = new(
        """\b(?<tex>[A-Za-z_]\w*)\s*\.\s*Sample\s*\(""",
        RegexOptions.Compiled);

    private static readonly Regex Identifier = new("""^[A-Za-z_]\w*$""", RegexOptions.Compiled);

    // Every type pattern below is a whole-token match of a REAL resource type
    // (SlangcResourceTypes): a user type that merely starts with "Texture" or "SamplerState"
    // ('struct TextureRegion', 'SamplerStateInfo') is not a resource and must not match.

    // A texture/sampler-typed parameter in a function signature: '(' or ',' then the type.
    private static readonly Regex ResourceParameter = new(
        $$"""[(,]\s*(?:(?:in|uniform|const)\s+)*(?<type>{{SlangcResourceTypes.Texture}}|{{SlangcResourceTypes.Sampler}})(?:\s*<[^>;{}()]*>)?\s+(?<name>[A-Za-z_]\w*)\s*(?=[,)])""",
        RegexOptions.Compiled);

    // Any texture declaration's name, for the texture-array and subscript-load checks.
    private static readonly Regex AnyTextureDecl = new(
        $$"""{{SlangcResourceTypes.Texture}}(?:\s*<[^>;{}]*>)?\s+(?<name>[A-Za-z_]\w*)(?<array>\s*\[)?""",
        RegexOptions.Compiled);

    // The text before a name ends in a texture type: the name is being declared, not used.
    private static readonly Regex TextureTypeAtEnd = new(
        $$"""{{SlangcResourceTypes.Texture}}(?:\s*<[^>;{}]*>)?\s*$""",
        RegexOptions.Compiled);

    // Texture-object spellings left behind after the respelling = a shape it does not model.
    // Calls are checked first, so the message names the call rather than its now-unused sampler.
    private static readonly Regex LeftoverCall = new(
        """\.\s*(?<what>Sample\w*|Load|Gather\w*|GetDimensions|CalculateLevelOfDetail\w*)\s*\(""",
        RegexOptions.Compiled);

    private static readonly Regex LeftoverType = new(
        $$"""(?<what>{{SlangcResourceTypes.Texture}}|{{SlangcResourceTypes.Sampler}})""",
        RegexOptions.Compiled);

    private static readonly Regex LineDirective = new(
        """^[ \t]*#line[ \t]+(?<n>\d+)(?:[ \t]+"(?<file>[^"]*)")?""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Test convenience: the respelled text, or null with the reason.</summary>
    public static string? TryRespell(string hlsl, out string? unsupported)
    {
        Result r = Respell(hlsl);
        unsupported = r.Unsupported;
        return r.Text;
    }

    /// <summary>
    /// Returns <paramref name="hlsl"/> in DX9 effect texture syntax, or the construct that
    /// stopped it. Text with no texture object comes back unchanged.
    /// </summary>
    public static Result Respell(string hlsl)
    {
        Result Reject(string what, int offset) => new(null, what, SourceLine(hlsl, offset));

        // ---- Shapes checked on slangc's text as emitted (exact offsets).
        Match param = ResourceParameter.Match(hlsl);
        if (param.Success)
        {
            return Reject(
                $"a texture or sampler passed as a function parameter ('{param.Groups["type"].Value} {param.Groups["name"].Value}'); " +
                "DX9 effects reach a texture only through a global sampler", param.Index);
        }

        // An array of textures ('Texture2D Arr[2];') is named as such, before its legitimate
        // '[i]' indexing could be mistaken for a subscript load or its call for the culprit.
        Match textureArray = AnyTextureDecl.Matches(hlsl).FirstOrDefault(m => m.Groups["array"].Success)
            ?? Match.Empty;
        if (textureArray.Success)
        {
            return Reject(
                $"the texture array '{textureArray.Groups["name"].Value}[...]'; a DX9 sampler_state binds exactly one texture",
                textureArray.Index);
        }

        var textureNames = AnyTextureDecl.Matches(hlsl)
            .Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (string name in textureNames)
        {
            foreach (Match sub in Regex.Matches(hlsl, $@"\b{Regex.Escape(name)}\s*\["))
            {
                if (IsDeclarationSite(hlsl, sub.Index))
                    continue;
                return Reject($"the subscript load '{name}[...]' (a texel fetch by integer coordinate, which SM3 cannot express)", sub.Index);
            }
        }

        foreach (Match decl in SamplerDecl.Matches(hlsl))
        {
            if (decl.Groups["space"].Success && decl.Groups["space"].Value != "0")
            {
                return Reject(
                    $"the register space on sampler '{decl.Groups["name"].Value}' (register({decl.Groups["reg"].Value}, space{decl.Groups["space"].Value})); DX9 has no register spaces",
                    decl.Index);
            }
        }

        // ---- The rewrite. Line structure is preserved, so offsets into the result map to the
        // same #line-relative source lines.
        var samplerToTexture = new Dictionary<string, string>(StringComparer.Ordinal);
        int failAt = 0;
        string? body = RewriteSampleCalls(hlsl, samplerToTexture, out string? unsupported, ref failAt);
        if (body is null)
            return Reject(unsupported!, failAt);

        Match leftoverCall = LeftoverCall.Match(body);
        if (leftoverCall.Success)
            return new(null, $"a '{leftoverCall.Groups["what"].Value}' call", SourceLine(body, leftoverCall.Index));

        // Non-2D texture types are named before an unused-sampler complaint could mask them.
        foreach (Match type in LeftoverType.Matches(body))
        {
            if (type.Groups["what"].Value == "SamplerState" || IsPlainTextureDecl(body, type.Index))
                continue;
            return new(null, $"a '{type.Groups["what"].Value}' resource (only a plain global 'Texture2D name;' has a DX9 form here)", SourceLine(body, type.Index));
        }

        foreach (Match decl in SamplerDecl.Matches(body))
        {
            string samp = decl.Groups["name"].Value;
            if (!samplerToTexture.ContainsKey(samp))
            {
                return new(null,
                    $"the sampler '{samp}', which no 'T.Sample({samp}, uv)' call uses directly (a DX9 sampler_state must name the one texture it samples)",
                    SourceLine(body, decl.Index));
            }
        }

        // Every texture2D ahead of every sampler: the texture declarations move, in order, to
        // the first texture-or-sampler declaration site; each original line is left blank so
        // the line structure (and every #line mapping) below it is unchanged.
        body = SamplerDecl.Replace(body, m =>
        {
            string samp = m.Groups["name"].Value;
            string reg = m.Groups["reg"].Success ? $" : register({m.Groups["reg"].Value})" : "";
            return $"sampler2D {samp}{reg} = sampler_state {{ Texture = <{samplerToTexture[samp]}>; }};";
        });

        MatchCollection textures = TextureDecl.Matches(body);
        if (textures.Count > 0)
        {
            Match firstSampler = RespelledSampler.Match(body);
            int at = firstSampler.Success ? Math.Min(firstSampler.Index, textures[0].Index) : textures[0].Index;
            var hoisted = new StringBuilder();
            foreach (Match t in textures)
                hoisted.Append("texture2D ").Append(t.Groups["name"].Value).Append("; ");
            var rebuilt = new StringBuilder(body.Length + hoisted.Length);
            int cursor = 0;
            if (at < textures[0].Index)
            {
                rebuilt.Append(body, 0, at).Append(hoisted);
                cursor = at;
            }
            for (int i = 0; i < textures.Count; i++)
            {
                Match t = textures[i];
                rebuilt.Append(body, cursor, t.Index - cursor);
                if (i == 0 && at == t.Index)
                    rebuilt.Append(hoisted.ToString().TrimEnd());
                cursor = t.Index + t.Length;
            }
            rebuilt.Append(body, cursor, body.Length - cursor);
            body = rebuilt.ToString();
        }

        Match leftover = LeftoverType.Match(body);
        if (leftover.Success)
            return new(null, $"a '{leftover.Groups["what"].Value}' declaration this respelling does not recognise", SourceLine(body, leftover.Index));
        return new(body, null, 0);
    }

    // True when the identifier at 'index' is the name in its own texture declaration
    // ('Texture2D Arr[2]'), not a use.
    private static bool IsDeclarationSite(string text, int index)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        string before = text[lineStart..index];
        return TextureTypeAtEnd.IsMatch(before);
    }

    private static bool IsPlainTextureDecl(string text, int index)
    {
        int lineStart = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        Match m = TextureDecl.Match(text, lineStart);
        return m.Success && m.Index == lineStart;
    }

    /// <summary>
    /// The Slang source line for <paramref name="offset"/> in slangc's emission, from the
    /// nearest preceding <c>#line</c> directive whose file is the piped source (<c>&lt;stdin&gt;</c>).
    /// 0 when the offset sits in text slangc attributes to another file (its core module).
    /// </summary>
    internal static int SourceLine(string text, int offset)
    {
        int line = 0;
        string? file = null;
        int directiveEnd = -1;
        foreach (Match m in LineDirective.Matches(text))
        {
            if (m.Index > offset)
                break;
            if (m.Groups["file"].Success)
                file = m.Groups["file"].Value;
            line = int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
            directiveEnd = text.IndexOf('\n', m.Index);
        }
        if (directiveEnd < 0 || file != "<stdin>")
            return 0;
        int newlines = 0;
        for (int i = directiveEnd + 1; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
                newlines++;
        }
        return line + newlines;
    }

    // Rewrites every 'T.Sample(S, uv)' in text to 'tex2D(S, uv)', recording each S -> T pair.
    // The uv argument is rewritten recursively (it can itself contain a Sample call).
    private static string? RewriteSampleCalls(
        string text, Dictionary<string, string> samplerToTexture, out string? unsupported, ref int failAt)
    {
        unsupported = null;
        var result = new StringBuilder(text.Length);
        int cursor = 0;
        foreach (Match m in SampleCall.Matches(text))
        {
            if (m.Index < cursor)
                continue; // inside an argument list already rewritten

            failAt = m.Index;
            string tex = m.Groups["tex"].Value;
            int open = m.Index + m.Length - 1;
            int close = MatchingParen(text, open);
            if (close < 0)
            {
                unsupported = $"an unterminated '{tex}.Sample(' call";
                return null;
            }

            List<string> args = SplitTopLevel(text, open + 1, close);
            if (args.Count != 2)
            {
                unsupported = $"a '{tex}.Sample' call with {args.Count} arguments (only Sample(sampler, uv) has a tex2D form)";
                return null;
            }

            string samp = args[0].Trim();
            if (!Identifier.IsMatch(samp))
            {
                unsupported = $"a '{tex}.Sample' call through the sampler expression '{samp}' (only a named global sampler has a DX9 sampler_state)";
                return null;
            }
            if (samplerToTexture.TryGetValue(samp, out string? existing) && existing != tex)
            {
                unsupported = $"the sampler '{samp}' used with two textures ('{existing}' and '{tex}'; a DX9 sampler binds exactly one)";
                return null;
            }
            samplerToTexture[samp] = tex;

            int nestedFail = 0;
            string? uv = RewriteSampleCalls(args[1], samplerToTexture, out unsupported, ref nestedFail);
            if (uv is null)
            {
                failAt = open + 1 + args[0].Length + 1 + nestedFail;
                return null;
            }
            result.Append(text, cursor, m.Index - cursor);
            result.Append("tex2D(").Append(samp).Append(',').Append(uv).Append(')');
            cursor = close + 1;
        }
        result.Append(text, cursor, text.Length - cursor);
        return result.ToString();
    }

    private static int MatchingParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string text, int start, int end)
    {
        var parts = new List<string>();
        int depth = 0, from = start;
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(text.Substring(from, i - from));
                from = i + 1;
            }
        }
        parts.Add(text.Substring(from, end - from));
        return parts;
    }
}
