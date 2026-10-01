// Slang twin of Sepia.fx.
Texture2D s0Texture;
SamplerState s0;
float3 _sepiaTone;

[shader("fragment")]
float4 PixelShaderFunction(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float4 tex = s0Texture.Sample(s0, uv);
    float grayScale = dot(tex.rgb, float3(0.3, 0.59, 0.11));
    tex.rgb = grayScale * _sepiaTone;
    return tex;
}
