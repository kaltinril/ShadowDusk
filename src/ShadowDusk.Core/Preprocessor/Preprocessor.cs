#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// Flattens an HLSL source tree by expanding all <c>#include</c> directives (honoring
/// <c>#pragma once</c> and detecting circular includes) and prepending the platform macros,
/// producing a single self-contained <see cref="PreprocessedSource"/> for the compiler.
/// </summary>
public sealed class Preprocessor
{
    private static readonly Regex IncludePattern =
        new(@"^\s*#\s*include\s*[""<]([^"">]+)["">]", RegexOptions.Compiled);

    private static readonly Regex PragmaOncePattern =
        new(@"^\s*#\s*pragma\s+once\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Expands all includes in the source and prepends the platform macros.
    /// </summary>
    /// <param name="cleanedHlsl">The HLSL entry source (comments may still be present; the
    /// include scanner is comment-aware and ignores directives inside them).</param>
    /// <param name="originalFilePath">The entry source's path, used for diagnostics and relative includes.</param>
    /// <param name="macros">The platform macros to prepend.</param>
    /// <param name="includeResolver">The resolver used to fetch <c>#include</c> targets.</param>
    /// <param name="additionalPaths">Extra include search directories.</param>
    /// <returns>
    /// The flattened source on success, or a <see cref="ShaderError"/> on a missing or
    /// circular include.
    /// </returns>
    public Result<PreprocessedSource, ShaderError> Flatten(
        string cleanedHlsl,
        string originalFilePath,
        MacroSet macros,
        IIncludeResolver includeResolver,
        IReadOnlyList<string> additionalPaths)
    {
        var ctx = new PreprocessorContext();
        string prepend = macros.ToTextPrepend(originalFilePath);

        var bodyBuilder = new StringBuilder();
        var flattenResult = FlattenFile(cleanedHlsl, originalFilePath, ctx, includeResolver, additionalPaths, bodyBuilder);
        if (flattenResult.IsFailure)
            return Result<PreprocessedSource, ShaderError>.Fail(flattenResult.Error);

        string fullText = prepend + bodyBuilder.ToString();
        return Result<PreprocessedSource, ShaderError>.Ok(
            new PreprocessedSource(fullText, macros.ToDxcFlags(), originalFilePath));
    }

    private Result<Unit, ShaderError> FlattenFile(
        string text,
        string filePath,
        PreprocessorContext ctx,
        IIncludeResolver includeResolver,
        IReadOnlyList<string> additionalPaths,
        StringBuilder output)
    {
        // Cycle detection uses an include STACK (push on entry, pop on exit), not a
        // visited set: a DIAMOND include (a → {b, c} → common) is legal — fxc/mgfxc
        // accept it (header guards / #pragma once neutralize the duplication; our
        // flatten leaves #if/#define lines for DXC to evaluate) — while a true cycle
        // (a → b → a, or a self-include) must still fail SD0002.
        ctx.IncludeStack.Add(filePath);
        try
        {
            // Tracks an open /* ... */ block comment across lines so a directive inside
            // one (e.g. '/* #include "ghost.fxh" */') is never honored.
            bool inBlockComment = false;

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int lineNumber = i + 1;
                string line = lines[i];

                string trimmedLine = line.TrimEnd('\r');

                // Directive DETECTION runs on the comment-stripped view of the line;
                // OUTPUT always uses the original line text, so non-directive lines
                // (including commented-out directives) pass through verbatim.
                string scanLine = StripCommentsForScan(trimmedLine, ref inBlockComment);

                var pragmaMatch = PragmaOncePattern.Match(scanLine);
                if (pragmaMatch.Success)
                {
                    ctx.PragmaOnceFiles.Add(filePath);
                    continue;
                }

                var includeMatch = IncludePattern.Match(scanLine);
                if (includeMatch.Success)
                {
                    string includePath = includeMatch.Groups[1].Value;
                    var resolveResult = includeResolver.Resolve(includePath, filePath, additionalPaths);
                    if (resolveResult.IsFailure)
                    {
                        var err = resolveResult.Error;
                        IReadOnlyList<string> searched = err.SearchedPaths ?? [];
                        return Result<Unit, ShaderError>.Fail(
                            ShaderError.IncludeNotFound(filePath, lineNumber, includePath, searched));
                    }

                    string resolvedPath = resolveResult.Value.FilePath;

                    if (ctx.PragmaOnceFiles.Contains(resolvedPath))
                        continue;

                    if (ctx.IncludeStack.Contains(resolvedPath))
                        return Result<Unit, ShaderError>.Fail(
                            ShaderError.CircularInclude(filePath, lineNumber, includePath));

                    output.AppendLine($"#line 1 \"{resolvedPath.Replace('\\', '/')}\"");

                    var recurseResult = FlattenFile(
                        resolveResult.Value.Text,
                        resolvedPath,
                        ctx,
                        includeResolver,
                        additionalPaths,
                        output);

                    if (recurseResult.IsFailure)
                        return recurseResult;

                    output.AppendLine($"#line {lineNumber + 1} \"{filePath.Replace('\\', '/')}\"");
                    continue;
                }

                output.Append(trimmedLine);
                output.Append('\n');
            }

            return Result<Unit, ShaderError>.Ok(default);
        }
        finally
        {
            ctx.IncludeStack.Remove(filePath);
        }
    }

    /// <summary>
    /// Returns the line with comment text blanked out (for directive scanning only),
    /// updating <paramref name="inBlockComment"/> for <c>/* ... */</c> comments that span
    /// lines. String literals are skipped over so a <c>/*</c> or <c>//</c> inside one can
    /// never toggle comment state (e.g. <c>string s = "a /* b";</c>).
    /// </summary>
    private static string StripCommentsForScan(string line, ref bool inBlockComment)
    {
        // Fast path: nothing comment- or string-like on this line.
        if (!inBlockComment && line.IndexOf('/') < 0 && line.IndexOf('"') < 0)
            return line;

        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            if (inBlockComment)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    sb.Append("  ");
                    i += 2;
                }
                else
                {
                    sb.Append(' ');
                    i++;
                }
                continue;
            }

            char c = line[i];

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                // Line comment — blank the rest of the line.
                sb.Append(' ', line.Length - i);
                break;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                inBlockComment = true;
                sb.Append("  ");
                i += 2;
                continue;
            }

            if (c == '"')
            {
                // Copy the string literal verbatim (honoring \" escapes) so its
                // contents can neither open a comment nor end the scan early.
                sb.Append(c);
                i++;
                while (i < line.Length)
                {
                    sb.Append(line[i]);
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i++;
                        sb.Append(line[i]);
                    }
                    else if (line[i] == '"')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private sealed class PreprocessorContext
    {
        // Linux has a case-sensitive file system; Windows and macOS do not.
        private static readonly StringComparer PathComparer =
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        /// <summary>The chain of files currently being flattened (push/pop), for cycle detection.</summary>
        public HashSet<string> IncludeStack { get; } = new(PathComparer);

        public HashSet<string> PragmaOnceFiles { get; } = new(PathComparer);
    }
}
