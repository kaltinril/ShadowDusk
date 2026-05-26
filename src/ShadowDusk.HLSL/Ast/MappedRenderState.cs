#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>Result of mapping an FX9 render-state key/value to a MonoGame target.</summary>
public sealed record MappedRenderState(string MonoGameTarget, string NormalizedValue);
