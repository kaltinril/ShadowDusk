// KniEffectTest companion (ShadowDusk-authored): a Texture2DArray, slice 1 sampled.
// The texture has two slices, slice 0 red and slice 1 green, so the pixel must be green.
#if defined(OPENGL)
#define SV_POSITION POSITION
#define PS_PROFILE ps_3_0
#elif defined(SM6)
#define PS_PROFILE ps_6_0
#else
#define PS_PROFILE ps_4_0
#endif

Texture2DArray Arr : register(t1);
SamplerState ArrSampler : register(s1);

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : SV_Target0
{
    return Arr.Sample(ArrSampler, float3(tc, 1));
}

technique T { pass P { PixelShader = compile PS_PROFILE MainPS(); } }
