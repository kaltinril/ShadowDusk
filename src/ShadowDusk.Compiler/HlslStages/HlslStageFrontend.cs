#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Compiler.HlslStages;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler;

/// <summary>
/// The <b>two-source HLSL frontend</b>: accepts a vertex shader and a pixel shader as two
/// separate HLSL texts and produces ordinary <c>.fx</c> effect text for ShadowDusk's
/// <b>unchanged</b> faithful pipeline, exactly as the Slang and ShaderToy frontends do for their
/// inputs. Everything downstream (every target, the MGFX and KNIFX containers, FNA) is the
/// existing pipeline, so a pair compiles to the bytes the equivalent hand-merged <c>.fx</c> would.
/// </summary>
/// <remarks>
/// <para><b>How it works.</b> Each source is split into its top-level declarations (structs,
/// constant buffers, resources, globals, functions, macros, directives). The vertex declarations
/// come first, then the pixel declarations that are not duplicates. A declaration both sources
/// make is written once when the two are identical (ignoring whitespace and comments) and
/// rejected when they differ (<c>SD0664</c> for a struct, function, constant or macro,
/// <c>SD0665</c> for a constant buffer, texture, sampler or uniform parameter, whose names are
/// the effect's parameter names). A function with the same name but a different parameter list
/// is an overload and is kept. A <c>#line</c> marker before every declaration makes a compiler
/// error point at the original file and line, not at the generated effect. Finally a one-pass
/// <c>technique</c> naming the two entry points is appended, with the shader-model block the
/// Slang and ShaderToy frontends use.</para>
/// <para><b>Entry points.</b> HLSL does not mark them, so the caller names them
/// (<see cref="HlslStageSources.VertexEntry"/>, <see cref="HlslStageSources.PixelEntry"/>,
/// both <c>main</c> by default). When both are the same name, each is renamed to
/// <c>&lt;name&gt;_VS</c> / <c>&lt;name&gt;_PS</c> (only at calls and definitions, never at a
/// member access); a rename that cannot be done safely is refused (<c>SD0663</c>), never guessed.</para>
/// <para><b>What it does not check.</b> Each source must be self-contained as if compiled alone;
/// the merge does not verify that, nor that the vertex outputs and the pixel inputs agree (the
/// pipeline links them by semantic). Macros one source defines are visible to the other.
/// A column after a renamed entry point on the same line shifts by the length of the suffix.
/// Comments that sit between declarations (not inside one) are not carried into the generated effect.</para>
/// </remarks>
public static class HlslStageFrontend
{
    private static readonly Regex IdentifierName = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);

    /// <summary>
    /// Merges the two sources into <c>.fx</c> text, or returns the loud diagnostics:
    /// <c>SD0660</c> (empty source), <c>SD0661</c> (entry point not found), <c>SD0662</c> (entry point
    /// defined more than once), <c>SD0663</c> (entry point cannot be renamed safely), <c>SD0664</c> and
    /// <c>SD0665</c> (conflicting declarations), <c>SD0666</c> (unsupported source structure) and
    /// <c>SD0667</c> (invalid name). Pure text transformation: no process, no native.
    /// </summary>
    /// <param name="sources">The two sources and their settings.</param>
    public static Result<HlslStageFxConversion, ShaderError[]> ConvertToFx(HlslStageSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var errors = new List<ShaderError>();
        ValidateNames(sources, errors);
        if (string.IsNullOrWhiteSpace(sources.VertexSource))
            errors.Add(Empty(sources.VertexName, "vertex"));
        if (string.IsNullOrWhiteSpace(sources.PixelSource))
            errors.Add(Empty(sources.PixelName, "pixel"));
        if (errors.Count > 0)
            return Fail(errors);

        string vertexSource = sources.VertexSource.Replace("\r\n", "\n").Replace('\r', '\n');
        string pixelSource = sources.PixelSource.Replace("\r\n", "\n").Replace('\r', '\n');

        // 1. Entry points exist, are unambiguous, and are renamed apart when they share a name.
        string vertexEntry = sources.VertexEntry;
        string pixelEntry = sources.PixelEntry;
        Result<List<HlslDeclaration>, ShaderError[]> vertexParts = HlslSourceSplitter.Split(vertexSource, sources.VertexName);
        Result<List<HlslDeclaration>, ShaderError[]> pixelParts = HlslSourceSplitter.Split(pixelSource, sources.PixelName);
        if (vertexParts.IsFailure) errors.AddRange(vertexParts.Error);
        if (pixelParts.IsFailure) errors.AddRange(pixelParts.Error);
        if (errors.Count > 0)
            return Fail(errors);

        CheckEntry(vertexParts.Value, vertexEntry, sources.VertexName, "vertex", errors);
        CheckEntry(pixelParts.Value, pixelEntry, sources.PixelName, "pixel", errors);
        RejectTechniques(vertexParts.Value, sources.VertexName, errors);
        RejectTechniques(pixelParts.Value, sources.PixelName, errors);
        if (errors.Count > 0)
            return Fail(errors);

        List<HlslDeclaration> vertexDeclarations = vertexParts.Value;
        List<HlslDeclaration> pixelDeclarations = pixelParts.Value;

        if (vertexEntry == pixelEntry)
        {
            string baseName = vertexEntry;
            string vertexRenamed = FreshName(baseName + "_VS", vertexSource, pixelSource);
            string pixelRenamed = FreshName(baseName + "_PS", vertexSource, pixelSource, vertexRenamed);

            Result<string, ShaderError[]> vertexText = RenameEntry(vertexSource, sources.VertexName, baseName, vertexRenamed);
            Result<string, ShaderError[]> pixelText = RenameEntry(pixelSource, sources.PixelName, baseName, pixelRenamed);
            if (vertexText.IsFailure) errors.AddRange(vertexText.Error);
            if (pixelText.IsFailure) errors.AddRange(pixelText.Error);
            if (errors.Count > 0)
                return Fail(errors);

            vertexParts = HlslSourceSplitter.Split(vertexText.Value, sources.VertexName);
            pixelParts = HlslSourceSplitter.Split(pixelText.Value, sources.PixelName);
            if (vertexParts.IsFailure) return Fail(vertexParts.Error);
            if (pixelParts.IsFailure) return Fail(pixelParts.Error);
            vertexDeclarations = vertexParts.Value;
            pixelDeclarations = pixelParts.Value;
            vertexEntry = vertexRenamed;
            pixelEntry = pixelRenamed;
        }

        // 2. Merge.
        List<(HlslDeclaration Declaration, string File)>? merged = Merge(
            vertexDeclarations, sources.VertexName, pixelDeclarations, sources.PixelName, errors);
        if (merged is null)
            return Fail(errors);

        // 3. Assemble the .fx.
        var sb = new StringBuilder();
        sb.Append($"// Generated by ShadowDusk's two-source HLSL frontend from '{sources.VertexName}' (vertex) and").Append('\n');
        sb.Append($"// '{sources.PixelName}' (pixel). Each declaration below is the original text verbatim, preceded by a").Append('\n');
        sb.Append("// #line marker so compiler errors point at the original file and line; the technique is synthesized.").Append('\n');
        sb.Append('\n');

        // The convention the Slang and ShaderToy frontends measured: gate on SM4, which exactly the
        // DirectX profiles define. mgfxc's DirectX_11 profile rejects anything below SM 4.0 level 9.1,
        // its OpenGL profile caps at SM3, and ShadowDusk's FNA target is MojoShader SM2-3.
        sb.Append("#if SM4").Append('\n');
        sb.Append("    #define VS_SHADERMODEL vs_4_0_level_9_1").Append('\n');
        sb.Append("    #define PS_SHADERMODEL ps_4_0_level_9_1").Append('\n');
        sb.Append("#else").Append('\n');
        sb.Append("    #define VS_SHADERMODEL vs_3_0").Append('\n');
        sb.Append("    #define PS_SHADERMODEL ps_3_0").Append('\n');
        sb.Append("#endif").Append('\n');
        sb.Append('\n');

        foreach ((HlslDeclaration declaration, string file) in merged)
        {
            sb.Append("#line ").Append(declaration.StartLine).Append(" \"").Append(EscapeForLine(file)).Append('"').Append('\n');
            // Keep the original column: pad to where the declaration started.
            sb.Append(' ', declaration.StartColumn - 1).Append(declaration.Text).Append('\n');
        }

        sb.Append('\n');
        sb.Append($"technique {sources.TechniqueName}").Append('\n');
        sb.Append("{").Append('\n');
        sb.Append("    pass P0").Append('\n');
        sb.Append("    {").Append('\n');
        sb.Append($"        VertexShader = compile VS_SHADERMODEL {vertexEntry}();").Append('\n');
        sb.Append($"        PixelShader = compile PS_SHADERMODEL {pixelEntry}();").Append('\n');
        sb.Append("    }").Append('\n');
        sb.Append("}").Append('\n');

        return Result<HlslStageFxConversion, ShaderError[]>.Ok(
            new HlslStageFxConversion(sb.ToString(), vertexEntry, pixelEntry, []));
    }

    // ---- Validation ------------------------------------------------------------------------------

    private static void ValidateNames(HlslStageSources sources, List<ShaderError> errors)
    {
        void Check(string value, string what, string file)
        {
            if (!IdentifierName.IsMatch(value))
            {
                errors.Add(new ShaderError(File: file, Line: 0, Column: 0, Code: "SD0667",
                    Message: $"'{value}' is not a valid HLSL identifier for the {what}."));
            }
        }

        Check(sources.VertexEntry, "vertex entry point", sources.VertexName);
        Check(sources.PixelEntry, "pixel entry point", sources.PixelName);
        Check(sources.TechniqueName, "technique name", sources.VertexName);

        foreach ((string name, string what) in new[] { (sources.VertexName, "vertex"), (sources.PixelName, "pixel") })
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\n') || name.Contains('\r'))
            {
                errors.Add(new ShaderError(File: "<memory>", Line: 0, Column: 0, Code: "SD0667",
                    Message: $"The logical {what} file name must be a single non-empty line."));
            }
        }
    }

    private static ShaderError Empty(string file, string stage) =>
        new(File: file, Line: 0, Column: 0, Code: "SD0660",
            Message: $"The {stage} source is empty. Pass the HLSL text of the {stage} shader.");

    private static void CheckEntry(
        List<HlslDeclaration> declarations, string entry, string file, string stage, List<ShaderError> errors)
    {
        List<HlslDeclaration> definitions = declarations
            .Where(d => d.Kind == HlslDeclarationKind.Function && d.Name == entry).ToList();
        if (definitions.Count == 0)
        {
            bool prototypeOnly = declarations.Any(d => d.Kind == HlslDeclarationKind.Prototype && d.Name == entry);
            errors.Add(new ShaderError(File: file, Line: 1, Column: 1, Code: "SD0661",
                Message: prototypeOnly
                    ? $"The {stage} entry point '{entry}' is only declared, never defined, in this source."
                    : $"The {stage} entry point '{entry}' is not defined in this source. Name the function with " +
                      "HlslStageSources." + (stage == "vertex" ? "VertexEntry" : "PixelEntry") + "."));
            return;
        }

        if (definitions.Count > 1)
        {
            HlslDeclaration second = definitions[1];
            errors.Add(new ShaderError(File: file, Line: second.StartLine, Column: second.StartColumn, Code: "SD0662",
                Message: $"The {stage} entry point '{entry}' is defined {definitions.Count} times in this source " +
                         $"(first at line {definitions[0].StartLine}). An entry point must be a single function; " +
                         "rename the others."));
        }
    }

    private static void RejectTechniques(List<HlslDeclaration> declarations, string file, List<ShaderError> errors)
    {
        foreach (HlslDeclaration technique in declarations.Where(d => d.Kind == HlslDeclarationKind.Technique))
        {
            errors.Add(new ShaderError(File: file, Line: technique.StartLine, Column: technique.StartColumn,
                Code: HlslSourceSplitter.UnsupportedStructureCode,
                Message: "A stage source must not contain a technique block: the frontend writes the technique " +
                         "from the two entry points. To compile an effect that already has one, pass it to " +
                         "EffectCompiler as a single .fx."));
        }
    }

    // ---- Entry rename ----------------------------------------------------------------------------

    private static string FreshName(string preferred, string a, string b, string? alsoTaken = null)
    {
        string name = preferred;
        while (name == alsoTaken
               || Regex.IsMatch(a, $@"\b{Regex.Escape(name)}\b")
               || Regex.IsMatch(b, $@"\b{Regex.Escape(name)}\b"))
        {
            name += "_";
        }

        return name;
    }

    /// <summary>
    /// Renames the function <paramref name="from"/> to <paramref name="to"/> at its definition and calls
    /// only: an identifier followed by <c>(</c> and not preceded by <c>.</c>. A name that also appears on
    /// a preprocessor line is refused, because a macro could make the rename wrong.
    /// </summary>
    private static Result<string, ShaderError[]> RenameEntry(string source, string file, string from, string to)
    {
        string masked = SlangSourceMask.Mask(source);
        int[] lineStarts = HlslSourceSplitter.LineStarts(source);
        Regex word = new($@"\b{Regex.Escape(from)}\b");

        int pos = 0;
        while (pos < masked.Length)
        {
            int eol = masked.IndexOf('\n', pos);
            if (eol < 0)
                eol = masked.Length;
            string line = masked[pos..eol];
            if (line.TrimStart().StartsWith('#') && word.IsMatch(line))
            {
                (int l, int c) = HlslSourceSplitter.Position(lineStarts, pos + word.Match(line).Index);
                return Result<string, ShaderError[]>.Fail([new ShaderError(File: file, Line: l, Column: c,
                    Code: "SD0663",
                    Message: $"The entry point name '{from}' appears on a preprocessor line, so renaming it to " +
                             $"'{to}' is not safe. Give the two entry points different names " +
                             "(HlslStageSources.VertexEntry / PixelEntry).")]);
            }

            pos = eol + 1;
        }

        Regex call = new($@"(?<![\w.]){Regex.Escape(from)}(?=\s*\()");
        var sb = new StringBuilder(source.Length + 16);
        int last = 0;
        foreach (Match m in call.Matches(masked))
        {
            sb.Append(source, last, m.Index - last).Append(to);
            last = m.Index + m.Length;
        }

        sb.Append(source, last, source.Length - last);
        return Result<string, ShaderError[]>.Ok(sb.ToString());
    }

    // ---- Merge -----------------------------------------------------------------------------------

    private static List<(HlslDeclaration, string)>? Merge(
        List<HlslDeclaration> vertex, string vertexFile,
        List<HlslDeclaration> pixel, string pixelFile,
        List<ShaderError> errors)
    {
        var result = new List<(HlslDeclaration, string)>();
        var seen = new Dictionary<string, (HlslDeclaration Declaration, string File)>(StringComparer.Ordinal);

        foreach (HlslDeclaration declaration in vertex)
        {
            result.Add((declaration, vertexFile));
            foreach (string key in KeysOf(declaration))
                seen.TryAdd(key, (declaration, vertexFile));
        }

        foreach (HlslDeclaration declaration in pixel)
        {
            bool skip = false;
            bool conflict = false;
            foreach (string key in KeysOf(declaration))
            {
                if (!seen.TryGetValue(key, out var existing))
                    continue;

                if (existing.Declaration.Normalized == declaration.Normalized)
                {
                    skip = true;
                    continue;
                }

                conflict = true;
                bool resource = declaration.IsResourceLike || existing.Declaration.IsResourceLike;
                string what = Describe(declaration);
                errors.Add(new ShaderError(File: pixelFile, Line: declaration.StartLine, Column: declaration.StartColumn,
                    Code: resource ? "SD0665" : "SD0664",
                    Message: resource
                        ? $"The {what} '{NameForMessage(declaration, key)}' is declared in both sources with different " +
                          $"text ({existing.File} line {existing.Declaration.StartLine}). Constant buffers, textures, " +
                          "samplers and uniform parameters are one effect parameter each, so both stages must " +
                          "declare them identically. Make the two declarations identical."
                        : $"The {what} '{NameForMessage(declaration, key)}' is declared in both sources with different " +
                          $"text ({existing.File} line {existing.Declaration.StartLine}). Make the two declarations " +
                          "identical, or give one of them another name."));
                break;
            }

            if (conflict)
                continue;

            if (skip)
                continue;

            result.Add((declaration, pixelFile));
            foreach (string key in KeysOf(declaration))
                seen.TryAdd(key, (declaration, pixelFile));
        }

        return errors.Count > 0 ? null : result;
    }

    private static IEnumerable<string> KeysOf(HlslDeclaration d)
    {
        switch (d.Kind)
        {
            case HlslDeclarationKind.Directive:
            case HlslDeclarationKind.Conditional:
                // Identical lines (a #pragma, an #include) collapse; there is no name to conflict on.
                yield return "text:" + d.Normalized;
                break;
            case HlslDeclarationKind.Macro:
                yield return "macro:" + d.Name;
                break;
            case HlslDeclarationKind.Struct:
            case HlslDeclarationKind.Typedef:
                yield return "type:" + d.Name;
                break;
            case HlslDeclarationKind.Function:
            case HlslDeclarationKind.Prototype:
                // Overloads are legal HLSL: the same name with a different parameter list is a different function.
                yield return (d.Kind == HlslDeclarationKind.Function ? "func:" : "proto:") + d.Name + ParameterList(d.Normalized);
                break;
            case HlslDeclarationKind.ConstantBuffer:
            case HlslDeclarationKind.Resource:
            case HlslDeclarationKind.Parameter:
                foreach (string name in d.Names)
                    yield return "param:" + name;
                break;
            case HlslDeclarationKind.Constant:
                foreach (string name in d.Names)
                    yield return "const:" + name;
                break;
        }
    }

    private static string ParameterList(string normalized)
    {
        int open = normalized.IndexOf('(');
        if (open < 0)
            return "";
        int depth = 0;
        for (int i = open; i < normalized.Length; i++)
        {
            if (normalized[i] == '(') depth++;
            else if (normalized[i] == ')' && --depth == 0)
                return normalized[open..(i + 1)];
        }

        return normalized[open..];
    }

    private static string Describe(HlslDeclaration d) => d.Kind switch
    {
        HlslDeclarationKind.Struct => "struct",
        HlslDeclarationKind.Typedef => "typedef",
        HlslDeclarationKind.ConstantBuffer => "constant buffer",
        HlslDeclarationKind.Resource => "texture, sampler or buffer",
        HlslDeclarationKind.Parameter => "uniform parameter",
        HlslDeclarationKind.Constant => "constant",
        HlslDeclarationKind.Macro => "macro",
        HlslDeclarationKind.Function => "function",
        _ => "declaration",
    };

    private static string NameForMessage(HlslDeclaration d, string key)
    {
        int colon = key.IndexOf(':');
        string name = colon >= 0 ? key[(colon + 1)..] : key;
        int paren = name.IndexOf('(');
        return paren >= 0 ? name[..paren] : name;
    }

    private static string EscapeForLine(string file) => file.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static Result<HlslStageFxConversion, ShaderError[]> Fail(IEnumerable<ShaderError> errors) =>
        Result<HlslStageFxConversion, ShaderError[]>.Fail([.. errors]);
}
