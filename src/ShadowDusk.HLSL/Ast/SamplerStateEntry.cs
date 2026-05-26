#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>A single state key/value assignment inside an FX9 sampler_state block.</summary>
public sealed record SamplerStateEntry(string Key, string Value, SourceSpan Span);
