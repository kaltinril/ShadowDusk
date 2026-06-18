#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// Settings for the <see cref="MgfxWriter"/>: the backend <see cref="MgfxProfile"/> to stamp
/// into the header and the MGFX container version to emit.
/// </summary>
/// <param name="Profile">The backend profile byte written into the MGFX header.</param>
/// <param name="MgfxVersion">The MGFX container version; defaults to <c>10</c> (the most backwards-compatible).</param>
public sealed record MgfxWriterOptions(
    MgfxProfile Profile,
    byte        MgfxVersion = 10
);
