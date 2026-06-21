// =============================================================================
// ExProfileStageMismatch.fx  —  ShadowDusk regression fixture (Phase 48, W3)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 48).
//              Project-owned (same license as the repository).
// Purpose    : Pin the Phase-48 W3 cross-stage reject — a pixel profile bound to
//              the pass's VertexShader slot. Here the VertexShader slot uses
//              PS_SHADERMODEL (which expands to ps_3_0 / ps_4_0_level_9_1), a
//              realistic copy-paste error. mgfxc/fxc reject a cross-stage compile
//              binding; ShadowDusk used to ignore the declared prefix and compile
//              by slot. The GL/DX/Vulkan path must now reject this with SD0014.
//              (The FNA path reports the same condition as SD0300 via
//              ResolveFnaProfile; that pre-existing behavior is covered by
//              FnaProfilePolicyTests and is unchanged by W3.)
// Expect     : COMPILE FAILURE with diagnostic code SD0014 on OpenGL / DirectX_11.
// =============================================================================
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

struct VSOutput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
};

VSOutput MainVS(float3 pos : POSITION0, float4 color : COLOR0)
{
    VSOutput o;
    o.Position = float4(pos, 1.0);
    o.Color    = color;
    return o;
}

float4 MainPS(VSOutput input) : COLOR0
{
    return input.Color;
}

technique T
{
    pass P0
    {
        // DELIBERATE cross-stage error: a ps_* profile in the VertexShader slot.
        VertexShader = compile PS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
};
