#nullable enable

namespace ShadowDusk.Core;

// IMPORTANT: MgfxProfile byte values are NOT the same as PlatformTarget ordinals.
// PlatformTarget.DirectX=0, PlatformTarget.OpenGL=1
// MgfxProfile.OpenGL=0,     MgfxProfile.DirectX11=1
// Always use MgfxProfile values when writing the binary format profile byte.
/// <summary>
/// The profile byte written into the MGFX binary header, identifying the shader backend an
/// effect was compiled for. <b>These byte values are deliberately NOT the same as
/// <see cref="PlatformTarget"/> ordinals</b> (<c>PlatformTarget.DirectX=0, OpenGL=1</c> vs
/// <c>MgfxProfile.OpenGL=0, DirectX11=1</c>) — always use these values when writing the
/// format's profile byte.
/// </summary>
public enum MgfxProfile : byte
{
    /// <summary>The OpenGL/GLSL profile (MGFX profile byte 0).</summary>
    OpenGL    = 0,
    /// <summary>The DirectX 11 / DXBC profile (MGFX profile byte 1).</summary>
    DirectX11 = 1,
    /// <summary>
    /// The Vulkan / SPIR-V profile. Value <c>80</c> matches real MonoGame 3.8.5's own
    /// <c>VulkanShaderProfile</c> (confirmed against its source, not guessed) — an
    /// earlier placeholder value of <c>3</c> was wrong.
    /// </summary>
    Vulkan    = 80,
}
