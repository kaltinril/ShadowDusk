#nullable enable

using System;
using System.Reflection;

namespace ShadowDusk.Core;

/// <summary>
/// The runtime-detection advisory (Phase 35 auto-select seam 6): given a consumer's loaded XNA
/// framework assembly, recommend the <see cref="CapabilityProfile"/> to compile for. This is the
/// "auto-detect the runtime, give the newest <i>proven</i> experience" helper the consumer's game
/// calls; the resulting profile is passed to <see cref="CompilerOptions.Profile"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure and XNA-free: it classifies by assembly <i>name</i> (reflection only), so ShadowDusk keeps
/// no MonoGame/KNI/FNA reference. The recommendation is <b>conservative by design</b>: it returns
/// the universally-loadable MGFX v10 contract (or fx_2_0 for FNA) for every runtime today. The newer
/// containers (MGFX v11 on MonoGame 3.8.5, KNIFX on KNI 4.02) are render-proven but <b>not yet
/// auto-selected</b> -- 3.8.5 is pre-release and KNIFX feature-parity (optimized matrices) is pending
/// a KNIFXC golden -- so they stay opt-in via <see cref="CompilerOptions.Profile"/> until promoted
/// here. Auto-detect only ever returns a profile already proven for the detected runtime; it never
/// silently upgrades a consumer to an unproven format.
/// </para>
/// <para>
/// To promote a newer container to auto-selection (a deliberate version event), change the single
/// <see cref="Recommend(DetectedRuntime, PlatformTarget)"/> mapping below, e.g. KNI -&gt;
/// <see cref="CapabilityProfile.KniGL_4_02"/> once KNIFX parity is render-proven.
/// </para>
/// </remarks>
public static class RuntimeProfileDetector
{
    /// <summary>
    /// Classifies the runtime from its XNA framework assembly simple name. All three reimplementations
    /// share the <c>Microsoft.Xna.Framework</c> namespace, so the assembly name is the discriminator.
    /// </summary>
    public static DetectedRuntime Classify(string? xnaAssemblySimpleName)
    {
        if (string.IsNullOrWhiteSpace(xnaAssemblySimpleName))
            return DetectedRuntime.Unknown;

        // KNI must be checked before MonoGame: nkast's assemblies are "nkast.Xna.Framework[.*]".
        if (xnaAssemblySimpleName.StartsWith("nkast.Xna.Framework", StringComparison.OrdinalIgnoreCase))
            return DetectedRuntime.Kni;

        if (xnaAssemblySimpleName.StartsWith("MonoGame.Framework", StringComparison.OrdinalIgnoreCase))
            return DetectedRuntime.MonoGame;

        if (xnaAssemblySimpleName.Equals("FNA", StringComparison.OrdinalIgnoreCase)
            || xnaAssemblySimpleName.StartsWith("FNA.", StringComparison.OrdinalIgnoreCase))
            return DetectedRuntime.Fna;

        return DetectedRuntime.Unknown;
    }

    /// <summary>
    /// Recommends the proven <see cref="CapabilityProfile"/> to compile for the detected runtime and
    /// graphics <paramref name="target"/>. Conservative: see the type remarks.
    /// </summary>
    public static CapabilityProfile Recommend(DetectedRuntime runtime, PlatformTarget target)
    {
        // FNA loads only the fx_2_0 .fxb, regardless of the requested MGFX-style target.
        if (runtime == DetectedRuntime.Fna || target == PlatformTarget.Fna)
            return CapabilityProfile.Fna_Fx2;

        // Every MGFX-lineage runtime (MonoGame, KNI, or unknown) gets the universally-loadable
        // MGFX v10 contract. Promote a runtime to its newer container here once it is fully proven.
        return target == PlatformTarget.DirectX
            ? CapabilityProfile.MonoGameDX_SM5
            : CapabilityProfile.MonoGameGL_3_8_2;
    }

    /// <summary>Recommends a profile from the runtime assembly's simple name.</summary>
    public static CapabilityProfile Recommend(string? xnaAssemblySimpleName, PlatformTarget target)
        => Recommend(Classify(xnaAssemblySimpleName), target);

    /// <summary>
    /// Recommends a profile by reflecting the simple name off a loaded XNA assembly, e.g.
    /// <c>RuntimeProfileDetector.Recommend(typeof(Game).Assembly, PlatformTarget.OpenGL)</c>.
    /// </summary>
    public static CapabilityProfile Recommend(Assembly xnaAssembly, PlatformTarget target)
    {
        ArgumentNullException.ThrowIfNull(xnaAssembly);
        return Recommend(xnaAssembly.GetName().Name, target);
    }
}
