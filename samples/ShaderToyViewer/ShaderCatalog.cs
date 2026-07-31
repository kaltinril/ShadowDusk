#nullable enable

using System.Collections.Generic;

namespace ShadowDusk.ShaderToyViewer;

/// <summary>
/// The shaders this sample bundles and cycles through. Each is an ANIMATED and/or INTERACTIVE
/// ShaderToy image shader copied from the converter's authored / CC0 corpus, so every one
/// exercises the runtime convert -> in-memory compile -> load -> render path end to end.
/// </summary>
public static class ShaderCatalog
{
    /// <summary>The bundled shaders, in cycle order, resolved to their on-disk <c>shaders/</c> paths.</summary>
    public static IReadOnlyList<ShaderSource> Bundled { get; } = new[]
    {
        ShaderSource.Bundled("Time animation (iTime pulse)", "time_animation.glsl"),
        ShaderSource.Bundled("Mouse interaction (iMouse glow)", "mouse_interaction.glsl"),
        ShaderSource.Bundled("Polar spiral (atan2 + iTime)", "atan_polar.glsl"),
        ShaderSource.Bundled("Neonwave road (CC0)", "neon.glsl"),
    };

    /// <summary>
    /// The list the interactive window cycles through: the bundled shaders, with the user-supplied
    /// <paramref name="external"/> file (if any) appended as an extra, hot-reloadable entry. When a
    /// file is given it is selected first so the user immediately sees their shader.
    /// </summary>
    /// <param name="external">An external file resolved from the command line, or <c>null</c>.</param>
    /// <param name="startIndex">Receives the index the window should start on.</param>
    public static IReadOnlyList<ShaderSource> Build(ShaderSource? external, out int startIndex)
    {
        if (external is null)
        {
            startIndex = 0;
            return Bundled;
        }

        var list = new List<ShaderSource>(Bundled.Count + 1) { external };
        list.AddRange(Bundled);
        startIndex = 0; // the external file is first
        return list;
    }
}
