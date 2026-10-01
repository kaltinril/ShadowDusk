// Slang twin of Scanlines.fx.
Texture2D s0Texture;
SamplerState s0;
float _attenuation;
float _linesFactor;

[shader("fragment")]
float4 mainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float4 color = s0Texture.Sample(s0, uv);
    float scanline = sin(uv.y * _linesFactor) * _attenuation;
    color.rgb -= scanline;
    return color;
}
