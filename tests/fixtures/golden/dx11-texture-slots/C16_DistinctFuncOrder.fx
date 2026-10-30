sampler2D SB;
sampler2D SA;
sampler2D SC;
float4 F(float2 uv) { return tex2D(SC, uv); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 a = tex2D(SA, uv); float4 b = tex2D(SB, uv); if (a.r > 0.5) return F(uv); return a + b; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
