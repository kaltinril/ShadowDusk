// =============================================================================
// ExProfileSm3OnDirectX.fx  —  ShadowDusk regression fixture (Phase 51 A10)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 51 A10).
//              Project-owned (same license as the repository).
// Purpose    : The LITERAL half of the SD0015 reject set. A pass that names a
//              legacy SM3 profile outright — the shape most real DesktopGL/FNA
//              shaders ship with (Nez, Gum) — is compilable by mgfxc for OpenGL
//              but REJECTED for DirectX_11:
//                  Invalid profile 'ps_3_0'. Pixel shader 'MainPS' must be
//                  SM 4.0 level 9.1 or higher!
//              ShadowDusk used to compile it for DirectX anyway, so the effect
//              built here and then failed the consumer's real Content Pipeline
//              build. No macro expansion is involved, so this exercises the
//              cheap literal path of the floor check.
// Expect     : REJECT SD0015 on DirectX_11. COMPILE SUCCESS on OpenGL and FNA
//              (both of which cap at SM3, so ps_3_0 is correct for them).
// =============================================================================

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

struct VertexShaderOutput
{
    float4 Position : POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    return tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile ps_3_0 MainPS();
    }
};
