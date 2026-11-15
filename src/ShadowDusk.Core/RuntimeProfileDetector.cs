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
/// <para>
/// A game that has already chosen KNIFX asks <see cref="RecommendKnifx(Assembly)"/> instead, which
/// picks the KNIFX version (v11 or v12) its own KNI loads.
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

        // KNI's assemblies are named "Xna.Framework" and "Xna.Framework.*" (read off the
        // nkast.Xna.Framework* 4.2.9001 and 4.3.9001 packages); "nkast.Xna.Framework*" is the
        // PACKAGE id, kept here for callers that pass one. Real XNA is
        // "Microsoft.Xna.Framework*", which matches neither.
        if (xnaAssemblySimpleName.Equals("Xna.Framework", StringComparison.OrdinalIgnoreCase)
            || xnaAssemblySimpleName.StartsWith("Xna.Framework.", StringComparison.OrdinalIgnoreCase)
            || xnaAssemblySimpleName.StartsWith("nkast.Xna.Framework", StringComparison.OrdinalIgnoreCase))
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
        //
        // Only the targets the closed CapabilityProfile set actually models are mapped. A
        // fall-through `else` used to hand back the OpenGL profile for Vulkan, DirectX12,
        // and Metal — and because CompilationPipeline applies a set Profile's
        // GraphicsTarget OVER options.Target, that silently rewrote the requested backend:
        // a DesktopVK or WindowsDX12 game got a MojoShader-GLSL .mgfx its runtime cannot
        // load, compiled with exit 0 and no diagnostic, and PlatformTarget.Metal bypassed
        // the pipeline's own loud SD0200 rejection. Refusing loudly is the only honest
        // answer: Vulkan and DirectX12 compile correctly by setting Target directly and
        // leaving Profile null, which is what the message says.
        return target switch
        {
            PlatformTarget.DirectX => CapabilityProfile.MonoGameDX_SM5,
            PlatformTarget.OpenGL  => CapabilityProfile.MonoGameGL_3_8_2,
            _ => throw new ArgumentOutOfRangeException(
                nameof(target), target,
                $"No CapabilityProfile models the {target} target, and returning a profile for a " +
                "different backend would silently compile the wrong output. Leave " +
                "CompilerOptions.Profile null and set CompilerOptions.Target directly — that is " +
                "the supported path for Vulkan and DirectX12."),
        };
    }

    /// <summary>
    /// The KNIFX profile for a KNI of version <paramref name="kniVersion"/>: KNIFX v12
    /// (<see cref="CapabilityProfile.KniGL_4_03"/>) for KNI 4.3 and later, KNIFX v11
    /// (<see cref="CapabilityProfile.KniGL_4_02"/>) for anything older or unknown. KNI 4.2.x
    /// refuses v12, and KNI 4.3.x loads v11 but prints "needs to be rebuilt", so the version is
    /// what decides; v11 is the answer when it is not known, because every KNI loads it.
    /// </summary>
    /// <param name="kniVersion">The KNI assembly version, or null when it is not known.</param>
    public static CapabilityProfile RecommendKnifx(Version? kniVersion) =>
        kniVersion is not null && (kniVersion.Major > 4 || (kniVersion.Major == 4 && kniVersion.Minor >= 3))
            ? CapabilityProfile.KniGL_4_03
            : CapabilityProfile.KniGL_4_02;

    /// <summary>
    /// The KNIFX profile for the KNI the game is running on, read off a loaded KNI assembly:
    /// <c>RuntimeProfileDetector.RecommendKnifx(typeof(Game).Assembly)</c>. For a game that
    /// compiles to KNIFX at runtime, this picks the container version its own KNI loads without
    /// a warning (see <see cref="RecommendKnifx(Version)"/>), so the game never names one.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="kniAssembly"/> is not a KNI assembly:
    /// only KNI loads KNIFX, so there is no profile to recommend.</exception>
    public static CapabilityProfile RecommendKnifx(Assembly kniAssembly)
    {
        ArgumentNullException.ThrowIfNull(kniAssembly);
        AssemblyName name = kniAssembly.GetName();
        if (Classify(name.Name) != DetectedRuntime.Kni)
        {
            throw new ArgumentException(
                $"'{name.Name}' is not a KNI assembly (KNI's are named Xna.Framework and Xna.Framework.*). " +
                "KNIFX loads only on KNI; use Recommend for other runtimes.",
                nameof(kniAssembly));
        }

        return RecommendKnifx(name.Version);
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
