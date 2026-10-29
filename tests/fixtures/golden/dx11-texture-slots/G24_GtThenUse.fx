sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 x = tex2D(SC, uv); float4 y = tex2D(SB, uv); if (x.a > y.a) return x; return y; }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
