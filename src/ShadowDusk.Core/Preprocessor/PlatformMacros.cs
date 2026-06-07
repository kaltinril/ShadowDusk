#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// Provides the standard preprocessor macros (e.g. <c>MGFX</c>, <c>GLSL</c>/<c>HLSL</c>,
/// per-target flags) that shaders branch on for each <see cref="PlatformTarget"/>, mirroring
/// the macros <c>mgfxc</c> defines.
/// </summary>
public static class PlatformMacros
{
    /// <summary>
    /// Returns the macro set for the given platform target.
    /// </summary>
    /// <param name="platform">The target backend.</param>
    /// <returns>The macros to define for that target.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown for an unsupported target (e.g. Metal, which is not yet implemented).
    /// </exception>
    public static MacroSet For(PlatformTarget platform) => platform switch
    {
        PlatformTarget.DirectX => new MacroSet([new("MGFX"), new("HLSL"), new("SM4")]),
        PlatformTarget.OpenGL  => new MacroSet([new("MGFX"), new("GLSL"), new("OPENGL")]),
        PlatformTarget.Vulkan  => new MacroSet([new("MGFX"), new("HLSL"), new("VULKAN"), new("SM6")]),
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };
}
