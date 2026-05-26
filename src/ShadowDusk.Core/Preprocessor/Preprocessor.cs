#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.Core.Preprocessor;

public sealed class Preprocessor
{
    private static readonly Regex IncludePattern =
        new(@"^\s*#\s*include\s*[""<]([^"">]+)["">]", RegexOptions.Compiled);

    private static readonly Regex PragmaOncePattern =
        new(@"^\s*#\s*pragma\s+once\s*$", RegexOptions.Compiled);

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
        ctx.VisitedFiles.Add(filePath);

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNumber = i + 1;
            string line = lines[i];

            string trimmedLine = line.TrimEnd('\r');

            var pragmaMatch = PragmaOncePattern.Match(trimmedLine);
            if (pragmaMatch.Success)
            {
                ctx.PragmaOnceFiles.Add(filePath);
                continue;
            }

            var includeMatch = IncludePattern.Match(trimmedLine);
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

                if (ctx.VisitedFiles.Contains(resolvedPath))
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

    private sealed class PreprocessorContext
    {
        // Linux has a case-sensitive file system; Windows and macOS do not.
        private static readonly StringComparer PathComparer =
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        public HashSet<string> VisitedFiles { get; } = new(PathComparer);
        public HashSet<string> PragmaOnceFiles { get; } = new(PathComparer);
    }
}
