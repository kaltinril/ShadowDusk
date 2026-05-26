Texture2D    Albedo        : register(t0);
SamplerState AlbedoSampler : register(s0);

float4 PSMain(float2 uv : TEXCOORD0) : SV_Target
{
    return Albedo.Sample(AlbedoSampler, uv);
}
