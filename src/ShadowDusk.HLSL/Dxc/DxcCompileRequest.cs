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
    /// <summary>The preprocessed HLSL source to compile.</summary>
    public required string HlslSource { get; init; }

    /// <summary>The logical file name reported in diagnostics and used for include resolution.</summary>
    public required string SourceFileName { get; init; }

    /// <summary>The entry-point function name to compile.</summary>
    public required string EntryPoint { get; init; }

    /// <summary>The shader stage (vertex/pixel/…) the entry point belongs to.</summary>
    public required ShaderStage Stage { get; init; }

    /// <summary>The target platform that selects the output dialect (SPIR-V for OpenGL, DXBC/DXIL for DirectX).</summary>
    public required PlatformTarget Platform { get; init; }

    /// <summary>Preprocessor macros to define, as <c>(name, value)</c> pairs; a <see langword="null"/> value defines the macro with no value.</summary>
    public IReadOnlyList<(string Name, string? Value)> Macros { get; init; } = [];

    /// <summary>Optional handler that resolves <c>#include</c> directives; <see langword="null"/> disables includes.</summary>
    public IDxcIncludeHandler? IncludeHandler { get; init; }

    /// <summary>Fine-grained compile tuning (warning tolerance, debug-info embedding).</summary>
    public DxcCompileOptions Options { get; init; } = new();
}
