#ifndef FOO
Texture2D M : register(t1);
#else
Texture2D M;
#endif
sampler2D SB;
sampler2D SA;
SamplerState ss;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return M.Sample(ss, uv) * tex2D(SB, uv) + tex2D(SA, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
