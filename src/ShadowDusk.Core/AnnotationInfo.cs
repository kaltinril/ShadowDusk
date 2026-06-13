#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// A single FX annotation (a <c>&lt;key = value;&gt;</c> attached to a parameter or
/// technique), as the <see cref="MgfxWriter"/> serializes it. Exactly one of the typed
/// value fields is populated, selected by <see cref="Type"/>.
/// </summary>
/// <param name="Name">The annotation key.</param>
/// <param name="Type">
/// The MGFX <c>EffectParameterType</c> ordinal that tells the reader which value field to
/// read. Only the three annotation-bearing values occur here: <c>2</c> = Int32 (read
/// <see cref="IntValue"/>), <c>3</c> = Single/float (read <see cref="FloatValue"/>),
/// <c>4</c> = String (read <see cref="StringValue"/>).
/// </param>
/// <param name="StringValue">The value when <see cref="Type"/> is <c>4</c>; otherwise <see langword="null"/>.</param>
/// <param name="FloatValue">The value when <see cref="Type"/> is <c>3</c>; otherwise <see langword="null"/>.</param>
/// <param name="IntValue">The value when <see cref="Type"/> is <c>2</c>; otherwise <see langword="null"/>.</param>
/// <param name="BoolValue">The boolean value, when applicable; otherwise <see langword="null"/>.</param>
public sealed record AnnotationInfo(
    string  Name,
    byte    Type,
    string? StringValue,
    float?  FloatValue,
    int?    IntValue,
    bool?   BoolValue
);
