#nullable enable

using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;

namespace ShadowDusk.ContentPipeline;

/// <summary>
/// MonoGame content importer for HLSL effect source. Reads a <c>.fx</c> (or <c>.fxh</c>) file and
/// hands its text to <see cref="ShadowDuskEffectProcessor"/>. Ships in both the tools-only
/// <c>ShadowDusk.MgcbPlugin</c> package (<c>.mgcb</c> / MGCB) and the library
/// <c>ShadowDusk.ContentPipeline</c> package (MonoGame 3.8.5 Content Builder), from this one file.
/// <para>
/// Deliberately identical in behavior to MonoGame's stock <c>EffectImporter</c> - importing is
/// "read the file", and the two are interchangeable. It exists so that a <c>.mgcb</c> which
/// <c>/reference:</c>s only the ShadowDusk plugin has a complete importer + processor pair, and
/// so the editor offers the ShadowDusk processor as the default for a <c>.fx</c> imported with it.
/// In a 3.8.5 Content Builder the instance must be passed explicitly (auto-discovery by
/// extension picks MonoGame's stock <c>EffectImporter</c> when both are loaded).
/// </para>
/// </summary>
[ContentImporter(
    ".fx", ".fxh",
    DisplayName = "ShadowDusk Effect Importer",
    DefaultProcessor = nameof(ShadowDuskEffectProcessor),
    // The imported payload is the raw source text; caching it buys nothing and costs an
    // intermediate file per effect. Matches MonoGame's stock EffectImporter.
    CacheImportedData = false)]
public sealed class ShadowDuskEffectImporter : ContentImporter<EffectContent>
{
    // Installs the plugin-directory native fallback before anything can P/Invoke. See
    // PluginNativeLibraryResolver for why an MGCB host cannot find our natives otherwise.
    static ShadowDuskEffectImporter() => PluginNativeLibraryResolver.Register();

    /// <summary>
    /// Reads <paramref name="filename"/> and returns its text as <see cref="EffectContent"/>.
    /// </summary>
    /// <param name="filename">The effect source path MGCB resolved for this build item.</param>
    /// <param name="context">MGCB's importer context (unused - importing touches nothing else).</param>
    /// <returns>The effect source, tagged with a <see cref="ContentIdentity"/> for diagnostics.</returns>
    public override EffectContent Import(string filename, ContentImporterContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);

        // The path exactly as MGCB gave it, made absolute: diagnostics and #include resolution
        // both key off it, and two same-named includes from different directories must stay
        // distinguishable (the CLI's N15 rule).
        string fullPath = Path.GetFullPath(filename);

        return new EffectContent
        {
            Identity   = new ContentIdentity(fullPath, "ShadowDusk"),
            EffectCode = File.ReadAllText(fullPath),
        };
    }
}
