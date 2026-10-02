#nullable enable

using System;
using System.Collections.Generic;
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
/// from it. Anything texture-shaped this does not model (<c>SampleLevel</c>/<c>SampleGrad</c>/
/// <c>Load</c>, a non-2D texture, one sampler shared by two textures, a <c>Sample</c> call
/// with an offset argument) is rejected as <c>SD0627</c> by name, never passed through.</para>
/// </remarks>
internal static class SlangFx2TextureRespeller
{
    /// <summary>The diagnostic code for a texture construct this respelling does not model.</summary>
    public const string UnsupportedCode = "SD0627";

    private static readonly Regex TextureDecl = new(
        """^[ \t]*Texture2D(?:\s*<[^>;{}]*>)?\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\([^)]*\))?\s*;[ \t]*(?=\r?$)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SamplerDecl = new(
        """^[ \t]*SamplerState\s+(?<name>[A-Za-z_]\w*)\s*(?<reg>:\s*register\s*\(\s*s\d+\s*\))?\s*;[ \t]*(?=\r?$)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SampleCall = new(
        """\b(?<tex>[A-Za-z_]\w*)\s*\.\s*Sample\s*\(""",
        RegexOptions.Compiled);

    private static readonly Regex Identifier = new("""^[A-Za-z_]\w*$""", RegexOptions.Compiled);

    // Texture-object spellings left behind after the respelling = a shape it does not model.
    // Calls are checked first, so the message names the call rather than its now-unused sampler.
    private static readonly Regex LeftoverCall = new(
        """\.\s*(?<what>Sample\w*|Load|Gather\w*|GetDimensions|CalculateLevelOfDetail\w*)\s*\(""",
        RegexOptions.Compiled);

    private static readonly Regex LeftoverType = new(
        """\b(?<what>(?:RW)?Texture(?:1D|2D|3D|Cube)(?:Array|MS|MSArray)?|SamplerState|SamplerComparisonState)\b""",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="hlsl"/> in DX9 effect texture syntax, or null with the construct
    /// that stopped it (for an <c>SD0627</c> message) in <paramref name="unsupported"/>.
    /// Text with no texture object comes back unchanged.
    /// </summary>
    public static string? TryRespell(string hlsl, out string? unsupported)
    {
        var samplerToTexture = new Dictionary<string, string>(StringComparer.Ordinal);
        string? body = RewriteSampleCalls(hlsl, samplerToTexture, out unsupported);
        if (body is null)
            return null;

        Match leftoverCall = LeftoverCall.Match(body);
        if (leftoverCall.Success)
        {
            unsupported = $"'{leftoverCall.Groups["what"].Value}'";
            return null;
        }

        // Non-2D texture types are named before an unused-sampler complaint could mask them.
        Match nonTwoD = LeftoverType.Match(TextureDecl.Replace(body, ""));
        if (nonTwoD.Success && nonTwoD.Groups["what"].Value != "SamplerState")
        {
            unsupported = $"'{nonTwoD.Groups["what"].Value}'";
            return null;
        }

        string? declError = null;
        body = TextureDecl.Replace(body, m => $"texture2D {m.Groups["name"].Value};");
        body = SamplerDecl.Replace(body, m =>
        {
            string samp = m.Groups["name"].Value;
            if (!samplerToTexture.TryGetValue(samp, out string? tex))
            {
                declError ??= $"sampler '{samp}', which no '.Sample(' call uses (a DX9 sampler_state must name the texture it samples)";
                return m.Value;
            }
            string reg = m.Groups["reg"].Success ? " " + m.Groups["reg"].Value.Trim() : "";
            return $"sampler2D {samp}{reg} = sampler_state {{ Texture = <{tex}>; }};";
        });
        if (declError is not null)
        {
            unsupported = declError;
            return null;
        }

        Match leftover = LeftoverType.Match(body);
        if (leftover.Success)
        {
            unsupported = $"'{leftover.Groups["what"].Value}'";
            return null;
        }
        return body;
    }

    // Rewrites every 'T.Sample(S, uv)' in text to 'tex2D(S, uv)', recording each S -> T pair.
    // The uv argument is rewritten recursively (it can itself contain a Sample call).
    private static string? RewriteSampleCalls(string text, Dictionary<string, string> samplerToTexture, out string? unsupported)
    {
        unsupported = null;
        var result = new StringBuilder(text.Length);
        int cursor = 0;
        foreach (Match m in SampleCall.Matches(text))
        {
            if (m.Index < cursor)
                continue; // inside an argument list already rewritten

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
                unsupported = $"'{tex}.Sample' with {args.Count} arguments (only Sample(sampler, uv) has a tex2D equivalent)";
                return null;
            }

            string samp = args[0].Trim();
            if (!Identifier.IsMatch(samp))
            {
                unsupported = $"'{tex}.Sample' with the sampler expression '{samp}' (only a named global sampler has a DX9 sampler_state binding)";
                return null;
            }
            if (samplerToTexture.TryGetValue(samp, out string? existing) && existing != tex)
            {
                unsupported = $"sampler '{samp}' used with two textures ('{existing}' and '{tex}'); a DX9 sampler binds exactly one texture";
                return null;
            }
            samplerToTexture[samp] = tex;

            string? uv = RewriteSampleCalls(args[1], samplerToTexture, out unsupported);
            if (uv is null)
                return null;
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
