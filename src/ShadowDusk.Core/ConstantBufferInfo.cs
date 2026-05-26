#nullable enable

namespace ShadowDusk.Core;

public sealed record ConstantBufferInfo(
    string                Name,
    int                   SizeInBytes,
    IReadOnlyList<int>    ParameterIndices,
    IReadOnlyList<ushort> ParameterOffsets
);
