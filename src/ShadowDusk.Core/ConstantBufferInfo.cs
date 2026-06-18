#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// One effect constant buffer as the MGFX writer serializes it: its name, byte size, and the
/// effect parameters it contains (by index into the effect's flattened parameter list) with
/// each parameter's byte offset within the buffer.
/// </summary>
/// <param name="Name">The constant-buffer name (e.g. <c>$Globals</c>).</param>
/// <param name="SizeInBytes">The buffer's total size in bytes.</param>
/// <param name="ParameterIndices">Indices into the effect's parameter list of the parameters in this buffer.</param>
/// <param name="ParameterOffsets">Each parameter's byte offset within the buffer, parallel to <paramref name="ParameterIndices"/>.</param>
public sealed record ConstantBufferInfo(
    string                Name,
    int                   SizeInBytes,
    IReadOnlyList<int>    ParameterIndices,
    IReadOnlyList<ushort> ParameterOffsets
);
