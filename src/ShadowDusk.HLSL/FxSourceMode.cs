#nullable enable

namespace ShadowDusk.HLSL;

/// <summary>How the FX9 pre-parser treats legacy D3D9/SM3 constructs in the shader body.</summary>
public enum FxSourceMode
{
    /// <summary>Rewrite D3D9 constructs forward to SM4 for DXC (sampler_state → SamplerState +
    /// Texture2D, texture → Texture2D, tex2D → .Sample, COLOR → SV_Target). The default —
    /// the MonoGame OpenGL/DirectX/Vulkan pipeline.</summary>
    RewriteToSm4 = 0,

    /// <summary>Preserve D3D9 constructs verbatim for an SM1–3 backend (the FNA fx_2_0 target —
    /// vkd3d's D3D_BYTECODE accepts sampler_state initializers, texture declarations, tex2D and
    /// COLOR semantics natively). Technique/pass and parameter-annotation blocks are still
    /// stripped and captured as metadata.</summary>
    PreserveSm3 = 1,
}
