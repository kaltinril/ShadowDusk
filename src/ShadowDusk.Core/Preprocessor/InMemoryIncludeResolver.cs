#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// An <see cref="IIncludeResolver"/> that resolves <c>#include</c> directives from an
/// in-memory dictionary of file path → contents, with no disk access. Ideal for the
/// WASM/in-browser host and for tests, where the entire shader source set is held in memory.
/// </summary>
public sealed class InMemoryIncludeResolver : IIncludeResolver
{
    private readonly IReadOnlyDictionary<string, string> _files;

    /// <summary>
    /// Creates a resolver over the given map of file path → source text. Keys are matched
    /// using forward-slash-normalized paths.
    /// </summary>
    /// <param name="files">The virtual file set: path → contents.</param>
    public InMemoryIncludeResolver(IReadOnlyDictionary<string, string> files)
    {
        _files = files;
    }

    /// <inheritdoc/>
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
