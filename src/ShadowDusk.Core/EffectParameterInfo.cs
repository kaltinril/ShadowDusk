#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// One .mgfx parameter record. MonoGame 3.8.2's <c>Effect.ReadParameters</c> reads
/// <see cref="Elements"/> (array elements) and <see cref="Members"/> (struct members)
/// as RECURSIVE parameter collections — elements first, then members, each a full
/// nested parameter record (mirroring mgfxc's <c>EffectObject.WriteParameter</c>).
/// A value-typed leaf (Scalar/Vector/Matrix with no elements/members) additionally
/// carries a raw default-value blob of <c>RowCount*ColumnCount*4</c> bytes: the
/// <see cref="DefaultValue"/> read off the HLSL initializer (<c>float R = 1;</c>), or zeros when
/// the declaration has none. For an array, each element leaf carries its own blob.
/// </summary>
/// <param name="Class">The MGFX parameter class (Scalar = 0, Vector = 1, Matrix = 2, Object = 3, Struct = 4).</param>
/// <param name="Type">The MGFX parameter type (Bool = 1, Int32 = 2, Single = 3, textures, ...).</param>
/// <param name="Name">The parameter name (empty for an array element).</param>
/// <param name="Semantic">The semantic, if any.</param>
/// <param name="Annotations">The annotations declared on the parameter.</param>
/// <param name="RowCount">The row count (1 for a scalar or vector).</param>
/// <param name="ColumnCount">The column count (1 for a scalar).</param>
/// <param name="Members">Struct members, each a full nested record.</param>
/// <param name="Elements">Array elements, each a full nested record.</param>
/// <param name="DefaultValue">
/// The default blob of a value-typed leaf, in the layout MonoGame's <c>EffectParameter.Data</c>
/// holds (a matrix column by column), or null for zeros. When set it must be exactly
/// <c>RowCount*ColumnCount*4</c> bytes; the writers refuse any other length.
/// </param>
public sealed record EffectParameterInfo(
    byte                          Class,
    byte                          Type,
    string                        Name,
    string?                       Semantic,
    IReadOnlyList<AnnotationInfo> Annotations,
    byte                          RowCount,
    byte                          ColumnCount,
    IReadOnlyList<EffectParameterInfo> Members,
    IReadOnlyList<EffectParameterInfo> Elements,
    byte[]?                       DefaultValue = null
);
