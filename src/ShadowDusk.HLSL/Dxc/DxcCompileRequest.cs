#nullable enable

using ShadowDusk.Core;
using Vortice.Dxc;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// A single HLSL → SPIR-V/DXIL compile request for the DXC frontend: the source and its
/// logical file name, the entry point and <see cref="ShaderStage"/> to compile, the
/// <see cref="PlatformTarget"/> that selects the output dialect, plus optional
/// preprocessor macros, an include handler, and tuning via <see cref="DxcCompileOptions"/>.
/// </summary>
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
