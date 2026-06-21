// =============================================================================
// ExProfileUndefinedMacro.fx  —  ShadowDusk regression fixture (Phase 48)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 48).
//              Project-owned (same license as the repository).
// Purpose    : Pin the second Phase-48 reproduction — a stock MonoGame shader
//              whose standard '#if OPENGL ... #define PS_SHADERMODEL ps_3_0 ...'
//              header has been REMOVED, leaving 'PS_SHADERMODEL' undefined. mgfxc
//              fails with "unrecognized compiler target 'PS_SHADERMODEL'";
//              ShadowDusk used to compile it anyway (silent SM3 fallback). After
//              macro expansion the token is still 'PS_SHADERMODEL' (undefined), so
//              it must now be rejected with SD0013.
// Expect     : COMPILE FAILURE with diagnostic code SD0013.
// =============================================================================
Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

float4 MainPS(float2 uv : TEXCOORD0) : COLOR0
{
    return tex2D(SpriteTextureSampler, uv);
}

technique BasicColorDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
