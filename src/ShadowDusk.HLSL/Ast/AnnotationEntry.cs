#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>A single entry inside an annotation block: TypeName Key = Value;</summary>
public sealed record AnnotationEntry(string Type, string Name, string Value, SourceSpan Span);
