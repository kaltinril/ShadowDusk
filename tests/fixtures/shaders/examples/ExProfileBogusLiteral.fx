// =============================================================================
// ExProfileBogusLiteral.fx  —  ShadowDusk regression fixture (Phase 48)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 48).
//              Project-owned (same license as the repository).
// Purpose    : Pin the profile-SHAPED-but-bogus class — 'ps_9_9' matches the
//              '_<digit>_<digit>' shape the old regex keyed on, but is not a real
//              fxc/mgfxc profile. The cheap recognized-profile lookup must reject
//              it (SD0013) WITHOUT any macro expansion, since no macro can rescue a
//              literal that already looks like a profile.
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
        PixelShader = compile ps_9_9 MainPS();
    }
};
