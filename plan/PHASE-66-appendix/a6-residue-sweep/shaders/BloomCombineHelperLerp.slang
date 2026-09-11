// A6 residue sweep sample, derived from tests/fixtures/shaders/third-party/Nez/BloomCombine.fx.
// Exercises: a THIRD distinct legacy sampler shape — `sampler X = sampler_state {
// Texture = <tex>; };` with no address-mode states at all (HeatDistortion's had
// AddressU/AddressV; this one has none) — plus a bare `sampler s0;`, a helper
// function, dot()/lerp()/saturate().
sampler s0;

texture _baseMap;
sampler _baseMapSampler = sampler_state { Texture = <_baseMap>; };

float _bloomIntensity;
float _baseIntensity;
float _bloomSaturation;
float _baseSaturation;

float4 adjustSaturation(float4 color, float saturation)
{
    float grey = dot(color.rgb, float3(0.3, 0.59, 0.11));
    return lerp(grey, color, saturation);
}

[shader("fragment")]
float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : SV_Target
{
    float4 bloom = tex2D(s0, texCoord);
    float4 base = tex2D(_baseMapSampler, texCoord);

    bloom = adjustSaturation(bloom, _bloomSaturation) * _bloomIntensity;
    base = adjustSaturation(base, _baseSaturation) * _baseIntensity;

    base *= (1 - saturate(bloom));

    return base + bloom;
}
