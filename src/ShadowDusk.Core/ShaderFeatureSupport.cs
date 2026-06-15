#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The single source of truth for which <see cref="ShaderFeatures"/> a shipping runtime is
/// render-proven to consume, and the guard that enforces ShadowDusk's hard rule: never emit bytes
/// no runtime can load. Requesting a feature with no downstream support is rejected loudly (SD0201)
/// rather than silently compiled into unloadable output.
/// </summary>
/// <remarks>
/// Add a flag to <see cref="RuntimeSupported"/> ONLY when a runtime is rung-4 render-proven to
/// consume it (and the GLSL rewriter is wired to lift the matching rejection). Until then every
/// feature is rejected. As of 2026-06 KNI's GL backend is still MojoShader (SM2-3): no shipping
/// runtime consumes vertex texture fetch, texture arrays, or un-down-converted modern GLSL, so
/// <see cref="RuntimeSupported"/> is <see cref="ShaderFeatures.None"/>.
/// </remarks>
public static class ShaderFeatureSupport
{
    /// <summary>
    /// The features at least one shipping runtime is render-proven to consume. Today: none, so any
    /// requested feature is rejected by <see cref="Validate"/>.
    /// </summary>
    public static ShaderFeatures RuntimeSupported => ShaderFeatures.None;

    /// <summary>
    /// Returns a loud <see cref="ShaderError"/> (SD0201) if <paramref name="requested"/> includes any
    /// feature no shipping runtime supports; otherwise <see langword="null"/>. This is the
    /// "don't allow a feature with no downstream consumer" guard.
    /// </summary>
    public static ShaderError? Validate(ShaderFeatures requested)
    {
        ShaderFeatures unsupported = requested & ~RuntimeSupported;
        if (unsupported == ShaderFeatures.None)
            return null;

        return new ShaderError(
            File: "",
            Line: 0,
            Column: 0,
            Code: "SD0201",
            Message: $"Shader feature(s) '{unsupported}' have no shipping runtime support yet and cannot be enabled. " +
                     "They are reserved for a future runtime proven to consume them; remove the feature from the capability profile.");
    }
}
