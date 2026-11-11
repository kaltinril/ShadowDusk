sampler2D TexSampler;
float4 Tint;

float4 PSMain(float2 uv : TEXCOORD0) : COLOR0
{
    return tex2D(TexSampler, uv) * Tint;
}
