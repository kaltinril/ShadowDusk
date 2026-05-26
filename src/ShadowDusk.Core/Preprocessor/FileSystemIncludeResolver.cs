#nullable enable

namespace ShadowDusk.Core.Preprocessor;

public sealed class FileSystemIncludeResolver : IIncludeResolver
{
    public Result<IncludeResolvedFile, ShaderError> Resolve(
        string includePath,
        string? includingFilePath,
        IReadOnlyList<string> additionalSearchPaths)
    {
        var tried = new List<string>();

        if (includingFilePath is not null)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(includingFilePath)) ?? string.Empty;
            string candidate = Path.GetFullPath(Path.Combine(dir, includePath));
            tried.Add(candidate);
            if (File.Exists(candidate))
                return Result<IncludeResolvedFile, ShaderError>.Ok(
                    new IncludeResolvedFile(candidate, File.ReadAllText(candidate)));
        }

        foreach (string searchPath in additionalSearchPaths)
        {
            string candidate = Path.GetFullPath(Path.Combine(searchPath, includePath));
            tried.Add(candidate);
            if (File.Exists(candidate))
                return Result<IncludeResolvedFile, ShaderError>.Ok(
                    new IncludeResolvedFile(candidate, File.ReadAllText(candidate)));
        }

        int includingLine = 0;
        string includingFile = includingFilePath ?? string.Empty;
        return Result<IncludeResolvedFile, ShaderError>.Fail(
            ShaderError.IncludeNotFound(includingFile, includingLine, includePath, tried));
    }
}
