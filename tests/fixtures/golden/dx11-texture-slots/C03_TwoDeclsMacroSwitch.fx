#ifdef FOO
Texture2D M : register(t5);
#else
Texture2D M;
#endif
sampler2D SB;
SamplerState ss;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return M.Sample(ss, uv) * 0.5 + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
