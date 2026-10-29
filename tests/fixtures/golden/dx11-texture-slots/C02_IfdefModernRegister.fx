#define USE_HI 1
Texture2D M
#if USE_HI
 : register(t0)
#endif
;
sampler2D SB;
SamplerState ss;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(SB, uv) * 0.5 + M.Sample(ss, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
