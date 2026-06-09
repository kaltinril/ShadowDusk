#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The consumer runtime/loader an effect is compiled for. Each target is a distinct emitted
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

    /// <summary>
    /// FNA (the XNA4 reimplementation). Emits the legacy D3D9 Effects Framework binary
    /// ("fx_2_0", version token 0xFEFF0901, conventional extension <c>.fxb</c>) with embedded
    /// SM1–3 bytecode compiled by vkd3d-shader's D3D_BYTECODE backend. FNA parses it at load
    /// time via FNA3D/MojoShader — one artifact serves every FNA graphics backend, so this is
    /// the only FNA member. <see cref="CompilerOptions.MgfxVersion"/> and
    /// <see cref="CompilerOptions.DxbcBackend"/> are ignored for this target.
    /// </summary>
    Fna     = 4,
}
