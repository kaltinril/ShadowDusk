sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 a = tex2D(SC, uv); a *= tex2D(SB, uv); return a; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
