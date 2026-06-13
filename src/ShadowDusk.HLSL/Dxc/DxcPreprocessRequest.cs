#nullable enable

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// A DXC preprocess-only request (the <c>-P</c> path): expand <c>#include</c>s,
/// <c>#define</c>s and conditional directives into a single flat HLSL text WITHOUT
/// compiling. No entry point, stage, or profile is involved — preprocessing is
/// stage-agnostic.
/// </summary>
/// <remarks>
/// Used by the zero-technique fallback in <c>CompilationPipeline</c>: the MonoGame
/// stock effects (BasicEffect.fx etc.) declare their techniques only through the
/// <c>TECHNIQUE(name, vs, ps)</c> macro from <c>Macros.fxh</c>, so the raw pre-parse
/// sees zero literal <c>technique</c> blocks. Running DXC's preprocessor first expands
/// the macro into a literal <c>technique { pass { ... } }</c> block the pre-parser can
/// then read.
/// </remarks>
public sealed class DxcPreprocessRequest
{
    /// <summary>The HLSL source to preprocess (already <c>#include</c>-flattened upstream).</summary>
    public required string HlslSource { get; init; }

    /// <summary>The original source path, preserved for diagnostics.</summary>
    public required string SourceFileName { get; init; }

    /// <summary>The macro defines to apply (the target's <c>PlatformMacros</c> set as -D pairs).</summary>
    public IReadOnlyList<(string Name, string? Value)> Macros { get; init; } = [];
}
