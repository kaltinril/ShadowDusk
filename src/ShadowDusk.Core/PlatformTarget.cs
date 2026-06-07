#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The MonoGame/KNI backend an effect is compiled for. Each target is a distinct emitted
/// artifact loaded by a different runtime path, so a shader must be compiled (and validated)
/// per target.
/// </summary>
public enum PlatformTarget
{
    /// <summary>
    /// DirectX 11 (Windows). Emits DXBC (SM ≤ 5) via the vkd3d-shader backend (cross-platform)
    /// or the Windows-only d3dcompiler_47 oracle — never DXC, which emits only SM6 DXIL.
    /// </summary>
    DirectX = 0,

    /// <summary>
    /// OpenGL / DesktopGL / WebGL. Emits GLSL via DXC → SPIR-V → SPIRV-Cross → managed
    /// MojoShader-dialect rewrite. The default library target.
    /// </summary>
    OpenGL  = 1,

    /// <summary>Metal (macOS/iOS). Not yet implemented — reserved for a future backend.</summary>
    Metal   = 2,

    /// <summary>Vulkan (SPIR-V). Not yet implemented — reserved for a future backend.</summary>
    Vulkan  = 3,
}
