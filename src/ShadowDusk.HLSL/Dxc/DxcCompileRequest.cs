#nullable enable

using ShadowDusk.Core;
using Vortice.Dxc;

namespace ShadowDusk.HLSL.Dxc;

public sealed class DxcCompileRequest
{
    public required string HlslSource { get; init; }
    public required string SourceFileName { get; init; }
    public required string EntryPoint { get; init; }
    public required ShaderStage Stage { get; init; }
    public required PlatformTarget Platform { get; init; }
    public IReadOnlyList<(string Name, string? Value)> Macros { get; init; } = [];
    public IDxcIncludeHandler? IncludeHandler { get; init; }
    public DxcCompileOptions Options { get; init; } = new();
}
