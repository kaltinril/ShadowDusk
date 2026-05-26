#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>Source location range covering a contiguous span of characters in the input file.</summary>
public readonly record struct SourceSpan(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    /// <summary>Sentinel value indicating no known source location.</summary>
    public static readonly SourceSpan Unknown = new(0, 0, 0, 0);

    /// <inheritdoc/>
    public override string ToString() => $"({StartLine},{StartColumn})-({EndLine},{EndColumn})";
}
