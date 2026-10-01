// Slang twin of MultiTextureOverlay.fx: two children, overlay blend.
Texture2D s0Texture;
SamplerState s0;
Texture2D _secondTexture;
SamplerState _secondTextureSampler;

float blendOverlay(float color, float blend)
{
    return color < 0.5 ? (2.0 * color * blend) : (1.0 - 2.0 * (1.0 - color) * (1.0 - blend));
}

[shader("fragment")]
float4 PixelShaderFunction(float4 pos : SV_Position, float2 coords : TEXCOORD0) : SV_Target
{
    float4 color = s0Texture.Sample(s0, coords);
    float4 mask = _secondTexture.Sample(_secondTextureSampler, coords);
    return float4(blendOverlay(color.r, mask.r), blendOverlay(color.g, mask.g), blendOverlay(color.b, mask.b), 1.0);
}
