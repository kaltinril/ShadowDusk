// A6 residue sweep sample, derived from tests/fixtures/shaders/DeferredSprite.fx.
// Exercises: MULTIPLE RENDER TARGETS via a struct-typed pixel-shader return value
// (COLOR0/COLOR1 members), clip(), a legacy `sampler X = sampler_state { Texture =
// <tex>; };` alongside a bare `sampler s0;`. No shader in the shipped 21-file corpus
// or the Phase 65 Gum probes exercises MRT.
sampler s0;

texture _normalMap;
sampler _normalMapSampler = sampler_state { Texture = <_normalMap>; };

float _alphaCutoff;
float _alphaAsSelfIllumination;
float _selfIlluminationPower;

struct PixelMultiTextureOut
{
    float4 color : SV_Target0;
    float4 normal : SV_Target1;
};

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

[shader("fragment")]
PixelMultiTextureOut deferredSpritePixel(VertexShaderOutput input)
{
    PixelMultiTextureOut output;

    output.color = tex2D(s0, input.TexCoord) * input.Color;

    clip((output.color.a < _alphaCutoff) ? -1 : 1);

    output.normal = tex2D(_normalMapSampler, input.TexCoord);
    output.normal.a *= _alphaAsSelfIllumination * _selfIlluminationPower;

    return output;
}
