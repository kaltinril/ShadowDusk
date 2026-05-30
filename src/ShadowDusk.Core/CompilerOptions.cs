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

    /// <summary>
    /// Which backend compiles HLSL to SM5 DXBC when <see cref="Target"/> is
    /// <see cref="PlatformTarget.DirectX"/>. Defaults to the proven Windows-only
    /// d3dcompiler_47 oracle; set to <see cref="DxbcBackend.Vkd3d"/> for the
    /// cross-platform vkd3d-shader backend. Ignored for non-DirectX targets.
    /// </summary>
    public DxbcBackend DxbcBackend { get; init; } = DxbcBackend.D3DCompiler;
}
