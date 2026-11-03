#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.Core;

/// <summary>The source language <see cref="ShaderFormatDetector"/> recognized a shader as.</summary>
public enum ShaderFormat
{
    /// <summary>Not a shader language ShadowDusk reads, or ambiguous (see <see cref="ShaderFormatDetection.IsAmbiguous"/>).</summary>
    Unknown,

    /// <summary>An HLSL effect (<c>.fx</c>): source with a <c>technique</c> block.</summary>
    FxEffect,

    /// <summary>
    /// HLSL-compatible Slang: source with a <c>[shader("...")]</c> entry attribute, or a Slang-only
    /// <c>import</c> / <c>module</c> declaration. A file that is valid as both Slang and an
    /// <c>.fx</c> effect (it has a <c>technique</c> block) is reported as <see cref="FxEffect"/>.
    /// </summary>
    Slang,

    /// <summary>
    /// A fragment shader in GLSL: a ShaderToy-style <c>mainImage</c> entry, or a plain-GLSL
    /// parameterless <c>void main()</c>. Converted by the separate <c>ShadowDusk.ShaderToy</c> package.
    /// </summary>
    Glsl,
}

/// <summary>The result of <see cref="ShaderFormatDetector.Detect"/>.</summary>
/// <param name="Format">The recognized format, or <see cref="ShaderFormat.Unknown"/>.</param>
/// <param name="IsAmbiguous">
/// True when the source carries the signals of two formats that cannot both be right (an HLSL
/// <c>technique</c> block and a GLSL entry point) and no name hint settled it.
/// <see cref="Format"/> is then <see cref="ShaderFormat.Unknown"/>. Ambiguous input is a real
/// error to report, never a reason to hand the shader to something else.
/// </param>
/// <param name="Reason">One sentence saying which signal decided the result, for diagnostics.</param>
public readonly record struct ShaderFormatDetection(ShaderFormat Format, bool IsAmbiguous, string Reason)
{
    /// <summary>True when a format was recognized (<see cref="Format"/> is not <see cref="ShaderFormat.Unknown"/>).</summary>
    public bool IsRecognized => Format != ShaderFormat.Unknown;
}

/// <summary>
/// Identifies a shader's source language from its text alone, so callers that receive a shader
/// from a stream, a byte buffer, or a package whose files lost their extensions can still route
/// it. It is a cheap structural check on comment- and string-stripped text, never a parse or a
/// compile: the frontend that receives the shader does the real validation and fails loudly if
/// the guess was wrong.
/// </summary>
/// <remarks>
/// <para>Rules, in order:</para>
/// <list type="number">
/// <item>A <c>technique</c> block AND a GLSL entry (<c>mainImage</c>, or a parameterless
/// <c>void main()</c>): ambiguous, unless the name hint's extension picks one side.</item>
/// <item>A <c>technique</c> block: <see cref="ShaderFormat.FxEffect"/>. This wins over Slang
/// markers, because HLSL-compatible Slang with a technique block compiles as an effect.</item>
/// <item>A <c>[shader("...")]</c> attribute, or a line-leading <c>import x;</c> /
/// <c>module x;</c>: <see cref="ShaderFormat.Slang"/>.</item>
/// <item>A GLSL entry: <see cref="ShaderFormat.Glsl"/>.</item>
/// <item>No signal: the name hint's extension if it names a format, else
/// <see cref="ShaderFormat.Unknown"/>.</item>
/// </list>
/// <para>The name hint never overrides a content signal; it only settles rules 1 and 5.
/// Recognized hint extensions: <c>.fx</c>; <c>.slang</c>; <c>.glsl</c>, <c>.frag</c>,
/// <c>.fs</c>, <c>.glslf</c>.</para>
/// </remarks>
public static class ShaderFormatDetector
{
    private static readonly string[] GlslExtensions = [".glsl", ".frag", ".fs", ".glslf"];

    private static readonly Regex ShaderAttribute = new(@"\[\s*shader\s*\(", RegexOptions.Compiled);
    private static readonly Regex SlangImportOrModule = new(
        @"^\s*(?:import|module)\s+[\w.]+\s*;", RegexOptions.Compiled | RegexOptions.Multiline);

    // `void main()` or `void main(void)`: the GLSL fragment entry. An HLSL function named main
    // takes parameters (its inputs and outputs), so it never matches.
    private static readonly Regex GlslVoidMain = new(
        @"(?<![\w])void\s+main\s*\(\s*(?:void\s*)?\)", RegexOptions.Compiled);

    /// <summary>
    /// Detects the source language of <paramref name="source"/>. Never throws for any input
    /// text; a null <paramref name="source"/> throws <see cref="ArgumentNullException"/>.
    /// </summary>
    /// <param name="source">The shader source text.</param>
    /// <param name="nameHint">
    /// Optional file name or path. Only its extension is read, and only to settle ambiguous
    /// content or content with no signal at all.
    /// </param>
    public static ShaderFormatDetection Detect(string source, string? nameHint = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        string stripped = StripCommentsAndStrings(source);
        bool hasTechnique = ContainsWord(stripped, "technique");
        bool hasGlslEntry = ContainsWord(stripped, "mainImage") || GlslVoidMain.IsMatch(stripped);
        bool hasSlangMarker = ShaderAttribute.IsMatch(stripped) || SlangImportOrModule.IsMatch(stripped);
        ShaderFormat hinted = FormatFromName(nameHint);

        if (hasTechnique && hasGlslEntry)
        {
            return hinted is ShaderFormat.FxEffect or ShaderFormat.Glsl
                ? new ShaderFormatDetection(hinted, false,
                    $"the source has both an HLSL 'technique' block and a GLSL entry point; the name '{nameHint}' chose {hinted}.")
                : new ShaderFormatDetection(ShaderFormat.Unknown, true,
                    "the source has both an HLSL 'technique' block and a GLSL 'mainImage' / 'void main()' entry point.");
        }

        if (hasTechnique)
            return new ShaderFormatDetection(ShaderFormat.FxEffect, false, "the source has an HLSL 'technique' block.");

        if (hasSlangMarker)
            return new ShaderFormatDetection(ShaderFormat.Slang, false,
                "the source has a Slang '[shader(...)]' entry attribute or an 'import' / 'module' declaration.");

        if (hasGlslEntry)
            return new ShaderFormatDetection(ShaderFormat.Glsl, false,
                "the source has a GLSL 'mainImage' or 'void main()' entry point.");

        return hinted != ShaderFormat.Unknown
            ? new ShaderFormatDetection(hinted, false,
                $"the source has no format signal; the name '{nameHint}' chose {hinted}.")
            : new ShaderFormatDetection(ShaderFormat.Unknown, false,
                "the source has no HLSL 'technique' block, no Slang '[shader(...)]' entry attribute, and no GLSL 'mainImage' / 'void main()' entry point.");
    }

    private static ShaderFormat FormatFromName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return ShaderFormat.Unknown;

        string ext = Path.GetExtension(name);
        if (ext.Equals(".fx", StringComparison.OrdinalIgnoreCase))
            return ShaderFormat.FxEffect;
        if (ext.Equals(".slang", StringComparison.OrdinalIgnoreCase))
            return ShaderFormat.Slang;
        foreach (string glsl in GlslExtensions)
        {
            if (ext.Equals(glsl, StringComparison.OrdinalIgnoreCase))
                return ShaderFormat.Glsl;
        }
        return ShaderFormat.Unknown;
    }

    // Whole-word, case-sensitive (GLSL/HLSL identifiers are case-sensitive) match of an identifier.
    private static bool ContainsWord(string text, string word)
    {
        int from = 0;
        while (true)
        {
            int idx = text.IndexOf(word, from, StringComparison.Ordinal);
            if (idx < 0)
                return false;

            bool leftOk = idx == 0 || !IsIdentChar(text[idx - 1]);
            int end = idx + word.Length;
            bool rightOk = end >= text.Length || !IsIdentChar(text[end]);
            if (leftOk && rightOk)
                return true;

            from = idx + 1;
        }
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Replaces // line comments, /* */ block comments, and double-quoted string literals with spaces
    // so a keyword inside a comment or string never trips the sniff. Newlines are kept, so the
    // line-anchored Slang patterns still see line starts.
    private static string StripCommentsAndStrings(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                // Consume the closing */ (or run off the end on an unterminated comment).
                if (i < text.Length) { sb.Append(' '); i++; }
                if (i < text.Length) { sb.Append(' '); i++; }
                continue;
            }

            if (c == '"')
            {
                sb.Append(' '); i++;
                while (i < text.Length && text[i] != '"' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(' '); i++; }
                    sb.Append(' '); i++;
                }
                if (i < text.Length && text[i] == '"') { sb.Append(' '); i++; }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
