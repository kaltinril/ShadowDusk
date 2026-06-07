#nullable enable

namespace ShadowDusk.HLSL.Dxc;

/// <summary>The container format of a blob DXC produced.</summary>
public enum BlobKind
{
    /// <summary>A SPIR-V module (the OpenGL/Vulkan path's intermediate).</summary>
    Spirv,
    /// <summary>
    /// DXC's bytecode container. Note DXC emits Shader-Model-6 DXIL, not the SM ≤ 5 DXBC_TPF
    /// that MonoGame's DX11 runtime loads — the shipping DirectX path uses the vkd3d-shader
    /// backend for DXBC, not DXC.
    /// </summary>
    Dxbc,
}
