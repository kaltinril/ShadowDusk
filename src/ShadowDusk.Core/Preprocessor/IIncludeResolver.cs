#nullable enable

namespace ShadowDusk.Core.Preprocessor;

public interface IIncludeResolver
{
    Result<IncludeResolvedFile, ShaderError> Resolve(
        string includePath,
        string? includingFilePath,
        IReadOnlyList<string> additionalSearchPaths);
}
