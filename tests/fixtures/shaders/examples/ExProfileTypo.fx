// =============================================================================
// ExProfileTypo.fx  —  ShadowDusk regression fixture (Phase 48)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 48).
//              Project-owned (same license as the repository).
// Purpose    : Pin the Phase-48 reject class — a typo'd compile target token
//              ('A') that is neither a known profile nor a macro that expands to
//              one. mgfxc/fxc hard-error ("unrecognized compiler target 'A'");
//              ShadowDusk used to silently fall back to SM3 and compile. The fix
//              must now reject this with SD0013 on every target.
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
        PixelShader = compile A MainPS();
    }
};
