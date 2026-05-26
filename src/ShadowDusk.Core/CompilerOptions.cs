#nullable enable

using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Core;

public sealed class CompilerOptions
{
    public PlatformTarget Target { get; init; } = PlatformTarget.OpenGL;
    public IIncludeResolver? IncludeResolver { get; init; }
    public IReadOnlyList<string> AdditionalIncludePaths { get; init; } = [];
    public string? SourceFileName { get; init; }
    public bool Debug { get; init; }
    public int MgfxVersion { get; init; } = 10;
}
