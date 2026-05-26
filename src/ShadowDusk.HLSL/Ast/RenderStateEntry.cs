#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>A single render-state key/value assignment inside an FX9 pass block.</summary>
public sealed record RenderStateEntry(string Key, string Value, SourceSpan Span);
