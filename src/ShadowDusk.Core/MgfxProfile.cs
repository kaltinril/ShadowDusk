#nullable enable

namespace ShadowDusk.Core;

// IMPORTANT: MgfxProfile byte values are NOT the same as PlatformTarget ordinals.
// PlatformTarget.DirectX=0, PlatformTarget.OpenGL=1
// MgfxProfile.OpenGL=0,     MgfxProfile.DirectX11=1
// Always use MgfxProfile values when writing the binary format profile byte.
public enum MgfxProfile : byte
{
    OpenGL    = 0,
    DirectX11 = 1,
    Vulkan    = 3,
}
