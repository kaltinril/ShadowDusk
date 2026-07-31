// =============================================================================
// ExProfileSm6OnDirectX.fx  —  ShadowDusk regression fixture (Phase 51 A10)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 51 A10).
//              Project-owned (same license as the repository).
// Purpose    : The COUNTER-INTUITIVE half of the SD0015 reject set. mgfxc's
//              DirectX_11 profile does not mean "SM 4.0 level 9.1 or ABOVE" in
//              the numeric sense: its accepted set tops out at SM5, so an SM6
//              profile is refused with the very same message:
//                  Invalid profile 'ps_6_0'. Pixel shader 'MainPS' must be
//                  SM 4.0 level 9.1 or higher!
//              This fixture exists so that anyone tempted to reimplement the
//              floor as a `major >= 4` comparison is caught by a test rather
//              than by a consumer's failed Content Pipeline build. (SM6 IS the
//              right profile for the Vulkan and DirectX_12 targets, which have
//              their own, different floors — see docs/validation-matrix.md §8.)
// Expect     : REJECT SD0015 on DirectX_11.
// =============================================================================

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    return tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
}

technique Sm6Drawing
{
    pass P0
    {
        PixelShader = compile ps_6_0 MainPS();
    }
};
