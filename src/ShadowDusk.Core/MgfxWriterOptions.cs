#nullable enable

namespace ShadowDusk.Core;

public sealed record MgfxWriterOptions(
    MgfxProfile Profile,
    byte        MgfxVersion = 10
);
