#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The XNA-reimplementation runtime a consumer's game is running on, classified from the loaded
/// framework assembly's simple name (all three share the <c>Microsoft.Xna.Framework</c> namespace,
/// so the assembly name is the only reliable fork discriminator). Used by
/// <see cref="RuntimeProfileDetector"/> to recommend a <see cref="CapabilityProfile"/>.
/// </summary>
public enum DetectedRuntime
{
    /// <summary>Could not classify the runtime (ambiguous or no XNA assembly). Falls back to the v10 default.</summary>
    Unknown = 0,

    /// <summary>MonoGame (<c>MonoGame.Framework</c>).</summary>
    MonoGame,

    /// <summary>KNI, nkast's fork (<c>nkast.Xna.Framework[.*]</c>).</summary>
    Kni,

    /// <summary>FNA (<c>FNA</c>), the D3D9 fx_2_0 / MojoShader lineage.</summary>
    Fna,
}
