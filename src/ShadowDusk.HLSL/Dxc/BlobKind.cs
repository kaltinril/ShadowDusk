#nullable enable

namespace ShadowDusk.HLSL.Dxc;

/// <summary>The container format of a compiled shader blob.</summary>
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
    /// <summary>
    /// A bare legacy D3D9 SM1–3 token stream (version token <c>0xFFFF____</c> ps /
    /// <c>0xFFFE____</c> vs, CTAB comment, instructions, <c>0x0000FFFF</c> end token) — what
    /// vkd3d-shader's D3D_BYTECODE target emits and what the FNA fx_2_0 container embeds.
    /// </summary>
    D3dBytecode,
}
