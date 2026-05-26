#nullable enable

namespace ShadowDusk.Core;

public sealed record EffectParameterInfo(
    byte                          Class,
    byte                          Type,
    string                        Name,
    string?                       Semantic,
    IReadOnlyList<AnnotationInfo> Annotations,
    byte                          RowCount,
    byte                          ColumnCount,
    IReadOnlyList<int>            MemberIndices,
    IReadOnlyList<int>            ElementIndices
);
