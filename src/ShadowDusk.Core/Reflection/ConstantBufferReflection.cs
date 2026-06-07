#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>A reflected constant (uniform) buffer: its name, size, bind slot, and variables.</summary>
public sealed record ConstantBufferReflection
{
    /// <summary>The constant buffer's name.</summary>
    public required string                        Name      { get; init; }
    /// <summary>The total size of the buffer in bytes.</summary>
    public required int                           SizeBytes { get; init; }
    /// <summary>The register/bind slot the buffer occupies.</summary>
    public required int                           BindSlot  { get; init; }
    /// <summary>The variables packed into the buffer, in offset order.</summary>
    public required IReadOnlyList<VariableReflection> Variables { get; init; }
}
