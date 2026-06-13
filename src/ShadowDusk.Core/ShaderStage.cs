#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// Which programmable shader stage a single compile request targets. The ordinals are
/// stable (<c>Vertex = 0</c>, <c>Pixel = 1</c>) and are used to pick the matching shader
/// profile/entry point when invoking the backend compiler.
/// </summary>
public enum ShaderStage { Vertex = 0, Pixel = 1 }
