#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// One .mgfx parameter record. MonoGame 3.8.2's <c>Effect.ReadParameters</c> reads
/// <see cref="Elements"/> (array elements) and <see cref="Members"/> (struct members)
/// as RECURSIVE parameter collections — elements first, then members, each a full
/// nested parameter record (mirroring mgfxc's <c>EffectObject.WriteParameter</c>).
/// A value-typed leaf (Scalar/Vector/Matrix with no elements/members) additionally
/// carries a raw default-value blob of <c>RowCount*ColumnCount*4</c> bytes.
/// </summary>
public sealed record EffectParameterInfo(
    byte                          Class,
    byte                          Type,
    string                        Name,
    string?                       Semantic,
    IReadOnlyList<AnnotationInfo> Annotations,
    byte                          RowCount,
    byte                          ColumnCount,
    IReadOnlyList<EffectParameterInfo> Members,
    IReadOnlyList<EffectParameterInfo> Elements
);
