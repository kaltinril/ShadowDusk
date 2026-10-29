#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// A single variable packed inside a <see cref="ConstantBufferReflection"/>: its name,
/// byte layout within the buffer, type shape, and (for structs) nested members.
/// </summary>
public sealed record VariableReflection
{
    /// <summary>The variable's name.</summary>
    public required string               Name           { get; init; }
    /// <summary>The variable's byte offset from the start of its constant buffer.</summary>
    public required int                  StartOffset    { get; init; }
    /// <summary>The variable's size in bytes.</summary>
    public required int                  SizeBytes      { get; init; }
    /// <summary>The variable's shape class (scalar, vector, matrix, …).</summary>
    public required EffectParameterClass ParameterClass { get; init; }
    /// <summary>The variable's element type.</summary>
    public required EffectParameterType  ParameterType  { get; init; }
    /// <summary>The number of rows (matrices and vectors); 1 for scalars.</summary>
    public required int                  Rows           { get; init; }
    /// <summary>The number of columns (matrices and vectors); 1 for scalars.</summary>
    public required int                  Columns        { get; init; }
    /// <summary>The array element count; 1 when not an array.</summary>
    public required int                  Elements       { get; init; }
    /// <summary>For struct variables, the nested member variables; otherwise <see langword="null"/>.</summary>
    public IReadOnlyList<VariableReflection>? Members   { get; init; }

    /// <summary>
    /// True for a matrix (or array of matrices) stored <c>row_major</c>: one buffer register per HLSL
    /// row, from a <c>row_major</c> modifier, <c>#pragma pack_matrix(row_major)</c> or the compile's
    /// packing flag. Internal: it decides the order of the default blob the writers get (issue #423
    /// follow-up), and on OpenGL, whose matrix registers ShadowDusk's own rewriter lays out, the
    /// pipeline ignores it.
    /// </summary>
    internal bool IsRowMajor { get; init; }
}
