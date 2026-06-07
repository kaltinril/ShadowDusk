#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// Resolves an HLSL <c>#include</c> directive to its source text. Supply a custom
/// implementation via <see cref="CompilerOptions.IncludeResolver"/> to control where includes
/// come from — e.g. <see cref="InMemoryIncludeResolver"/> to compile without touching disk
/// (WASM/in-browser) or <see cref="FileSystemIncludeResolver"/> for normal file access.
/// </summary>
public interface IIncludeResolver
{
    /// <summary>
    /// Resolves a single <c>#include</c> reference to its file path and contents.
    /// </summary>
    /// <param name="includePath">The path as written in the <c>#include</c> directive.</param>
    /// <param name="includingFilePath">The file that issued the <c>#include</c>, or <see langword="null"/>.</param>
    /// <param name="additionalSearchPaths">Extra directories to search, in order.</param>
    /// <returns>
    /// The resolved file (path + text) on success, or a <see cref="ShaderError"/>
    /// (e.g. <see cref="ShaderErrorKind.IncludeNotFound"/>) on failure.
    /// </returns>
    Result<IncludeResolvedFile, ShaderError> Resolve(
        string includePath,
        string? includingFilePath,
        IReadOnlyList<string> additionalSearchPaths);
}
