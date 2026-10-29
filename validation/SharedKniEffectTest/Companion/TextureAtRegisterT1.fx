// KniEffectTest companion (ShadowDusk-authored): a lone texture at an explicit register(t1).
// SpriteBatch binds its white texture to slot 0; the effect's Tex1 parameter is set to a green
// texture, so the pixel must be green. KNI's DeferredBasicEffect test uses explicit t0-t2 too.
#if defined(OPENGL)
#define SV_POSITION POSITION
#define PS_PROFILE ps_3_0
#elif defined(SM6)
#define PS_PROFILE ps_6_0
#else
#define PS_PROFILE ps_4_0
#endif

Texture2D Tex1 : register(t1);
SamplerState Tex1Sampler : register(s1);

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : SV_Target0
{
    return Tex1.Sample(Tex1Sampler, tc);
}

technique T { pass P { PixelShader = compile PS_PROFILE MainPS(); } }
