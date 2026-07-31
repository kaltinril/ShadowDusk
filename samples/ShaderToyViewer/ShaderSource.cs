#nullable enable

using System;
using System.IO;

namespace ShadowDusk.ShaderToyViewer;

/// <summary>
/// One loadable shader, identified by its absolute file <see cref="Path"/>. A <see cref="ShaderSource"/>
/// is either a bundled catalog shader (copied next to the binary under <c>shaders/</c>) or an arbitrary
/// external file the user pointed the sample at on the command line. Both flow through the SAME runtime
/// path (<see cref="SampleCompiler.Build(Microsoft.Xna.Framework.Graphics.GraphicsDevice, ShaderSource)"/>),
/// so an external ShaderToy/GLSL file is a first-class catalog citizen, just with hot-reload enabled.
/// </summary>
/// <param name="DisplayName">Human-readable name shown in the window title.</param>
/// <param name="Path">Absolute path to the <c>.glsl</c>/<c>.frag</c>/<c>.fs</c>/<c>.txt</c> source.</param>
/// <param name="IsExternal">True for a user-supplied file (watched for hot-reload); false for a bundled shader.</param>
public sealed record ShaderSource(string DisplayName, string Path, bool IsExternal)
{
    /// <summary>The file extensions the sample accepts as a ShaderToy / plain-GLSL image shader.</summary>
    public static readonly string[] AcceptedExtensions = { ".glsl", ".frag", ".fs", ".txt" };

    /// <summary>Builds a <see cref="ShaderSource"/> for a bundled shader under the binary's <c>shaders/</c> folder.</summary>
    public static ShaderSource Bundled(string displayName, string fileName) =>
        new(displayName, System.IO.Path.Combine(SampleCompiler.ShadersDirectory, fileName), IsExternal: false);

    /// <summary>
    /// Resolves and validates a user-supplied path into an external <see cref="ShaderSource"/>. Returns
    /// <c>false</c> with a human-readable <paramref name="error"/> when the file is missing or has an
    /// extension the sample does not accept, so the caller can report it instead of crashing.
    /// </summary>
    public static bool TryFromPath(string path, out ShaderSource? source, out string error)
    {
        source = null;
        error = string.Empty;

        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Not a valid path: \"{path}\" ({ex.GetType().Name})";
            return false;
        }

        if (!File.Exists(full))
        {
            error = $"Shader file not found:\n  {full}";
            return false;
        }

        string ext = System.IO.Path.GetExtension(full);
        if (Array.FindIndex(AcceptedExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) < 0)
        {
            error =
                $"Unsupported shader extension \"{ext}\" for:\n  {full}\n" +
                $"Accepted: {string.Join(", ", AcceptedExtensions)}";
            return false;
        }

        source = new ShaderSource(System.IO.Path.GetFileName(full), full, IsExternal: true);
        return true;
    }
}
