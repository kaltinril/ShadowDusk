// =============================================================================
// ExProfileLevel9Header.fx  —  ShadowDusk regression fixture (Phase 48)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 48).
//              Project-owned (same license as the repository).
// Purpose    : The ACCEPT guard for Phase 48 (work item W0). The standard MonoGame
//              cross-platform header expands PS_SHADERMODEL / VS_SHADERMODEL to the
//              Direct3D feature-level-9 profiles 'vs_4_0_level_9_1' /
//              'ps_4_0_level_9_1' on the DirectX branch. KnownProfiles MUST include
//              those, or enabling recognized-profile rejection would wrongly reject
//              every stock MonoGame DirectX shader. This fixture must keep compiling
//              on all targets, byte-for-byte unchanged.
// Expect     : COMPILE SUCCESS on OpenGL, DirectX_11, and FNA.
// =============================================================================
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    return tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
}

technique BasicColorDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
