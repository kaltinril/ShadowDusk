#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>Represents a parsed FX9 sampler declaration with its sampler_state block.</summary>
public sealed record SamplerInfo
{
    /// <summary>The declared sampler variable name.</summary>
    public required string Name { get; init; }

    /// <summary>"sampler", "sampler2D", "sampler3D", "samplerCUBE", "SamplerState", etc.</summary>
    public required string SamplerType { get; init; }

    /// <summary>The identifier inside Texture = &lt;X&gt; or Texture = X, if present.</summary>
    public required string? TextureReference { get; init; }

    /// <summary>All non-Texture state entries declared inside the sampler_state block.</summary>
    public required IReadOnlyList<SamplerStateEntry> StateEntries { get; init; }

    /// <summary>Source location of the entire sampler declaration.</summary>
    public required SourceSpan Span { get; init; }
}
