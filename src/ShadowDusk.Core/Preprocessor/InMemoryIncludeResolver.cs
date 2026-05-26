#nullable enable

namespace ShadowDusk.Core.Preprocessor;

public sealed class InMemoryIncludeResolver : IIncludeResolver
{
    private readonly IReadOnlyDictionary<string, string> _files;

    public InMemoryIncludeResolver(IReadOnlyDictionary<string, string> files)
    {
        _files = files;
    }

    public Result<IncludeResolvedFile, ShaderError> Resolve(
        string includePath,
        string? includingFilePath,
        IReadOnlyList<string> additionalSearchPaths)
    {
        var tried = new List<string>();

        if (includingFilePath is not null)
        {
            string dir = Path.GetDirectoryName(includingFilePath) ?? string.Empty;
            string siblingKey = NormalizeKey(Path.Combine(dir, includePath));
            tried.Add(siblingKey);
            if (_files.TryGetValue(siblingKey, out string? siblingText))
                return Result<IncludeResolvedFile, ShaderError>.Ok(new IncludeResolvedFile(siblingKey, siblingText));
        }

        foreach (string searchPath in additionalSearchPaths)
        {
            string candidate = NormalizeKey(Path.Combine(searchPath, includePath));
            tried.Add(candidate);
            if (_files.TryGetValue(candidate, out string? candidateText))
                return Result<IncludeResolvedFile, ShaderError>.Ok(new IncludeResolvedFile(candidate, candidateText));
        }

        tried.Add(includePath);
        if (_files.TryGetValue(includePath, out string? directText))
            return Result<IncludeResolvedFile, ShaderError>.Ok(new IncludeResolvedFile(includePath, directText));

        int includingLine = 0;
        string includingFile = includingFilePath ?? string.Empty;
        return Result<IncludeResolvedFile, ShaderError>.Fail(
            ShaderError.IncludeNotFound(includingFile, includingLine, includePath, tried));
    }

    private static string NormalizeKey(string path) =>
        path.Replace('\\', '/');
}
