#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler.Slang;

/// <summary>Options for <see cref="SlangFrontend.ConvertToFx"/>.</summary>
public sealed class SlangConvertOptions
{
    /// <summary>The logical source name used in diagnostics. Defaults to <c>"&lt;memory&gt;.slang"</c>.</summary>
    public string SourceName { get; init; } = "<memory>.slang";

    /// <summary>Name of the synthesized <c>technique</c>. Defaults to <c>SlangEffect</c>.</summary>
    public string TechniqueName { get; init; } = "SlangEffect";
}

/// <summary>The successful product of the Slang frontend: <c>.fx</c> text for the unchanged pipeline.</summary>
/// <param name="FxText">The generated <c>.fx</c> effect source.</param>
/// <param name="Warnings">Non-fatal findings. Empty today; the slot exists for parity with the other frontends.</param>
public sealed record SlangFxConversion(string FxText, IReadOnlyList<ShaderError> Warnings);

/// <summary>
/// The <b>Slang input frontend</b> (issue #197's sibling, issue #198): accepts a <c>.slang</c>
/// source and produces ordinary <c>.fx</c> effect text for ShadowDusk's <b>unchanged</b>
/// faithful pipeline. ShadowDusk is a multi-input, multi-output shader compiler — input
/// languages (<c>.fx</c>, ShaderToy GLSL, Slang) are thin text frontends over ONE pipeline that
/// fans out to every supported runtime (MonoGame, KNI, FNA), OS (Windows, macOS, Linux), and
/// graphics backend (OpenGL, DX11, DX12, Vulkan) — and this frontend follows that shape
/// exactly.
///
/// <para><b>How it works — no Slang toolchain, anywhere, ever</b> (owner direction 2026-08-13,
/// superseding an earlier slangc-based route): Slang is a near-superset of HLSL — it keeps
/// HLSL's types, semantics, <c>cbuffer</c>/<c>register</c>, and function syntax — so the
/// supported input is <b>HLSL-compatible Slang</b>, and the shader body compiles through the
/// same DXC every <c>.fx</c> uses, with nothing new to ship. The frontend is pure managed text
/// transformation, so <c>.slang</c> input works everywhere the pipeline works, the
/// browser/WASM host included. <b>Slang-only language features</b> (module <c>import</c>s,
/// generics, <c>interface</c> conformances, <c>extension</c>s) are <b>rejected loudly by
/// name</b> (<c>SD0600</c>) — never silently miscompiled — which is the trade the project
/// chose, and matches the request as filed (<i>"write slang, but still go through the normal
/// compilation pipe… features not supported in HLSL are likely also not supported in
/// MonoGame"</i>).</para>
///
/// <para><b>The technique block is synthesized.</b> Slang has no <c>technique</c>/<c>pass</c>
/// concept, so entry points are declared the Slang way — <c>[shader("vertex")]</c> /
/// <c>[shader("fragment")]</c> attributes — and the frontend generates a one-pass technique
/// from them (both stages when both are present; pixel-only, the SpriteBatch shape, when only a
/// fragment entry exists). The attributes themselves are stripped from the body before compile
/// (fxc-lineage compilers reject them outside library targets). Shader-model selection follows
/// the ShaderToy frontend's measured convention: <c>#if SM4</c> → <c>vs/ps_4_0_level_9_1</c>
/// (mgfxc's DirectX_11 floor), else <c>vs/ps_3_0</c>.</para>
///
/// <para><b>No route through this frontend is `mgfxc`-equivalent</b> — `mgfxc` cannot read
/// Slang at all. The claim is simpler and stronger: the generated <c>.fx</c> compiles through
/// the same faithful pipeline as any other, to the same proven targets.</para>
/// </summary>
public static class SlangFrontend
{
    /// <summary>File extension the CLI auto-routes through this frontend.</summary>
    public const string Extension = ".slang";

    // The [shader("...")] attribute, to be stripped from the body after entry discovery.
    private static readonly Regex ShaderAttribute = new(
        """\[\s*shader\s*\(\s*"[a-z]+"\s*\)\s*\]\s*""", RegexOptions.Compiled);

    // Slang-only constructs with no HLSL meaning. Declaration keywords are line-anchored so an
    // identifier that merely CONTAINS the word (a variable named 'extension') cannot false-
    // positive; '__generic' is a reserved-prefix spelling no legal HLSL identifier uses.
    private static readonly (Regex Pattern, string Construct)[] SlangOnlyConstructs =
    [
        (new Regex(@"^\s*import\s+[\w.]+\s*;", RegexOptions.Compiled | RegexOptions.Multiline), "import"),
        (new Regex(@"^\s*module\s+[\w.]+\s*;", RegexOptions.Compiled | RegexOptions.Multiline), "module"),
        (new Regex(@"^\s*extension\b", RegexOptions.Compiled | RegexOptions.Multiline), "extension"),
        (new Regex(@"^\s*associatedtype\b", RegexOptions.Compiled | RegexOptions.Multiline), "associatedtype"),
        (new Regex(@"\b__generic\b", RegexOptions.Compiled), "__generic"),
    ];

    /// <summary>
    /// Converts HLSL-compatible Slang source to <c>.fx</c> effect text, or returns the loud
    /// diagnostics (<c>SD0600</c> for a Slang-only construct; <c>SD0602</c>–<c>SD0604</c> for
    /// entry-point problems). Pure text transformation — no toolchain, no process, no native.
    /// </summary>
    /// <param name="slangSource">The Slang source text.</param>
    /// <param name="options">Conversion options; see <see cref="SlangConvertOptions"/>.</param>
    public static Result<SlangFxConversion, ShaderError[]> ConvertToFx(
        string slangSource,
        SlangConvertOptions options)
    {
        // 1. Entry points, from the [shader(...)] attributes — the only authoritative statement
        //    of intent in a language with no technique/pass concept.
        var entries = SlangEntryScanner.Scan(slangSource, options.SourceName);
        if (entries.IsFailure)
            return Result<SlangFxConversion, ShaderError[]>.Fail(entries.Error);

        // 2. Slang-only constructs are rejected by name BEFORE DXC sees the body, so the author
        //    gets "this is the Slang feature that doesn't convert" instead of a cascade of
        //    downstream HLSL syntax errors. (Anything subtler falls through to DXC, whose own
        //    verbatim diagnostics remain the authority — this scan is a courtesy, not a gate.)
        string commentStripped = StripComments(slangSource);
        foreach ((Regex pattern, string construct) in SlangOnlyConstructs)
        {
            Match match = pattern.Match(commentStripped);
            if (!match.Success)
                continue;

            int line = 1 + commentStripped.AsSpan(0, match.Index).Count('\n');
            return Result<SlangFxConversion, ShaderError[]>.Fail(
            [
                new ShaderError(
                    File: options.SourceName, Line: line, Column: 1, Code: "SD0600",
                    Message: $"'{construct}' is a Slang-only language feature. ShadowDusk compiles " +
                             "the HLSL-compatible subset of Slang (the shader body goes through the " +
                             "same pipeline as every .fx, with nothing extra to install on any " +
                             "platform), and Slang-only features have no HLSL meaning to lower to. " +
                             "Rewrite this construct in plain HLSL terms."),
            ]);
        }

        // 3. Strip the [shader(...)] attributes: fxc-lineage compilers reject them outside
        //    library targets, and the technique block below carries the same information.
        string body = ShaderAttribute.Replace(slangSource, "");

        // 4. Assemble the .fx.
        SlangEntryPoint? vs = entries.Value.FirstOrDefault(e => e.Stage == SlangStage.Vertex);
        SlangEntryPoint? ps = entries.Value.FirstOrDefault(e => e.Stage == SlangStage.Fragment);

        var sb = new StringBuilder();
        sb.AppendLine($"// Generated from '{options.SourceName}' by ShadowDusk's Slang frontend.");
        sb.AppendLine("// The body below is the .slang source verbatim, minus the shader-stage attributes;");
        sb.AppendLine("// the technique block is synthesized from what they declared.");
        sb.AppendLine();

        // The ShaderToy frontend's measured convention, reasons and all: mgfxc's DirectX_11
        // profile REJECTS anything below SM 4.0 level 9.1, while its OpenGL profile caps at SM3
        // and ShadowDusk's FNA target is MojoShader SM2-3 — so gate on SM4 (which exactly the
        // DirectX profiles define), not on OPENGL.
        sb.AppendLine("#if SM4");
        sb.AppendLine("    #define VS_SHADERMODEL vs_4_0_level_9_1");
        sb.AppendLine("    #define PS_SHADERMODEL ps_4_0_level_9_1");
        sb.AppendLine("#else");
        sb.AppendLine("    #define VS_SHADERMODEL vs_3_0");
        sb.AppendLine("    #define PS_SHADERMODEL ps_3_0");
        sb.AppendLine("#endif");
        sb.AppendLine();
        sb.AppendLine(body.Trim());
        sb.AppendLine();
        sb.AppendLine($"technique {options.TechniqueName}");
        sb.AppendLine("{");
        sb.AppendLine("    pass P0");
        sb.AppendLine("    {");
        if (vs is not null)
            sb.AppendLine($"        VertexShader = compile VS_SHADERMODEL {vs.Name}();");
        if (ps is not null)
            sb.AppendLine($"        PixelShader = compile PS_SHADERMODEL {ps.Name}();");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return Result<SlangFxConversion, ShaderError[]>.Ok(
            new SlangFxConversion(sb.ToString(), []));
    }

    /// <summary>
    /// Replaces <c>//</c> and <c>/* */</c> comment contents with spaces (newlines preserved so
    /// reported line numbers stay true), so a Slang keyword inside a comment never rejects.
    /// </summary>
    private static string StripComments(string text)
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
                if (i < text.Length) { sb.Append(' '); i++; }
                if (i < text.Length) { sb.Append(' '); i++; }
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
