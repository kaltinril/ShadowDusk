#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Cli;

/// <summary>The input language the CLI was asked to treat the source as (the <c>--input-format</c> value).</summary>
internal enum InputFormat
{
    /// <summary>Auto-detect from extension, then a content sniff (the default; never requires a flag).</summary>
    Auto,

    /// <summary>Force the HLSL <c>.fx</c> path (skip the converter).</summary>
    Fx,

    /// <summary>Force the ShaderToy / GLSL path (route through <see cref="ShadowDusk.ShaderToy.ShaderToyConverter"/>).</summary>
    Glsl,

    /// <summary>Force the Slang path (route through <see cref="ShadowDusk.Compiler.Slang.SlangFrontend"/>).</summary>
    Slang,
}

/// <summary>The resolved input kind after detection: a real <c>.fx</c> effect, ShaderToy/GLSL, or Slang to convert.</summary>
internal enum InputKind
{
    Fx,
    Glsl,
    Slang,
}

/// <summary>
/// Decides whether a source file is an HLSL <c>.fx</c> effect or a ShaderToy/GLSL image shader, so the
/// CLI can route the latter through the <see cref="ShadowDusk.ShaderToy.ShaderToyConverter"/> front-end
/// before the unchanged compile pipeline. Detection is seamless (no required flag): extension-first, then
/// the library's <see cref="ShaderFormatDetector"/> content sniff for unknown extensions, and the
/// <c>--input-format</c> override on top.
/// </summary>
internal static class InputFormatDetector
{
    // De-facto ShaderToy / glslViewer / KodeLife / desktop-export fragment-shader extensions.
    private static readonly string[] GlslExtensions = { ".glsl", ".frag", ".fs", ".glslf" };

    /// <summary>
    /// Resolves <paramref name="text"/> at <paramref name="sourcePath"/> to an <see cref="InputKind"/>,
    /// honoring an explicit <paramref name="requested"/> format first. Returns a loud, located
    /// <see cref="ShaderError"/> only when the input is genuinely ambiguous or unclassifiable.
    /// </summary>
    public static Result<InputKind, ShaderError> Detect(string sourcePath, string text, InputFormat requested)
    {
        // 1. Explicit override (escape hatch) — honored verbatim, never required for correct output.
        switch (requested)
        {
            case InputFormat.Fx:
                return Result<InputKind, ShaderError>.Ok(InputKind.Fx);
            case InputFormat.Glsl:
                return Result<InputKind, ShaderError>.Ok(InputKind.Glsl);
            case InputFormat.Slang:
                return Result<InputKind, ShaderError>.Ok(InputKind.Slang);
        }

        // 2. Extension signal.
        string ext = Path.GetExtension(sourcePath);

        // A .fx is ALWAYS FX, full stop — never sniffed — so this change cannot regress a single
        // existing .fx invocation (backwards-compat guarantee).
        if (ext.Equals(".fx", StringComparison.OrdinalIgnoreCase))
            return Result<InputKind, ShaderError>.Ok(InputKind.Fx);

        if (GlslExtensions.Any(g => ext.Equals(g, StringComparison.OrdinalIgnoreCase)))
            return Result<InputKind, ShaderError>.Ok(InputKind.Glsl);

        // .slang is unambiguous — Slang's own conventional extension, and nothing else uses it.
        if (ext.Equals(".slang", StringComparison.OrdinalIgnoreCase))
            return Result<InputKind, ShaderError>.Ok(InputKind.Slang);

        // 3. Content sniff (the tie-breaker for unknown / no extension, e.g. a ShaderToy shader saved
        //    as .txt or piped in): the library's own ShaderFormatDetector, so the CLI and the
        //    library route the same text the same way. The extension was handled above, so no
        //    name hint is passed.
        ShaderFormatDetection detection = ShaderFormatDetector.Detect(text);

        // Genuinely ambiguous: looks like an HLSL effect AND a ShaderToy shader. Fail loudly rather
        // than silently pick a route.
        if (detection.IsAmbiguous)
            return Ambiguous(sourcePath,
                "input has BOTH an HLSL 'technique' block and a ShaderToy 'mainImage'/'void main' entry; " +
                "pass --input-format fx or --input-format glsl to choose.");

        switch (detection.Format)
        {
            case ShaderFormat.FxEffect:
                return Result<InputKind, ShaderError>.Ok(InputKind.Fx);
            case ShaderFormat.Slang:
                return Result<InputKind, ShaderError>.Ok(InputKind.Slang);
            case ShaderFormat.Glsl:
                return Result<InputKind, ShaderError>.Ok(InputKind.Glsl);
        }

        // Neither signal: we cannot classify it. Fail loudly (never a silent wrong route).
        return Ambiguous(sourcePath,
            "could not detect input format: no HLSL 'technique' block and no ShaderToy 'mainImage' / " +
            "'void main' entry point found. Pass --input-format fx or --input-format glsl.");
    }

    private static Result<InputKind, ShaderError> Ambiguous(string sourcePath, string message) =>
        Result<InputKind, ShaderError>.Fail(new ShaderError(
            File: sourcePath,
            Line: 0,
            Column: 0,
            // SD0005, not SD0002: SD0002 is exclusively "circular #include" in the
            // registry, and reusing it here made this condition read as one
            // (bug-hunt 2026-07-27 N13; one code = one condition).
            Code: "SD0005",
            Message: message));
}
