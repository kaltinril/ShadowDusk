#nullable enable

using System.Collections.Generic;

namespace ShadowDusk.ShaderToy.Sample;

/// <summary>One bundled ShaderToy shader: a friendly display name and the <c>.glsl</c> file
/// (relative to the sample's <c>shaders/</c> output folder) it loads at runtime.</summary>
/// <param name="DisplayName">Human-readable name shown in the overlay / window title.</param>
/// <param name="FileName">The <c>.glsl</c> file name under <c>shaders/</c>.</param>
public sealed record ShaderEntry(string DisplayName, string FileName);

/// <summary>
/// The shaders this sample bundles and cycles through. Each is an ANIMATED and/or INTERACTIVE
/// ShaderToy image shader copied from the converter's authored / CC0 corpus, so every one
/// exercises the runtime convert -> in-memory compile -> load -> render path end to end.
/// </summary>
public static class ShaderCatalog
{
    /// <summary>The bundled shaders, in cycle order.</summary>
    public static readonly IReadOnlyList<ShaderEntry> Entries = new[]
    {
        new ShaderEntry("Time animation (iTime pulse)", "time_animation.glsl"),
        new ShaderEntry("Mouse interaction (iMouse glow)", "mouse_interaction.glsl"),
        new ShaderEntry("Polar spiral (atan2 + iTime)", "atan_polar.glsl"),
        new ShaderEntry("Neonwave road (CC0)", "neon.glsl"),
    };
}
