#nullable enable

namespace ShadowDusk.Core;

public sealed record AnnotationInfo(
    string  Name,
    byte    Type,
    string? StringValue,
    float?  FloatValue,
    int?    IntValue,
    bool?   BoolValue
);
