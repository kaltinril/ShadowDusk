#nullable enable

namespace ShadowDusk.Core;

public sealed record CompiledShader(
    PlatformTarget Target,
    byte[] Data
);
