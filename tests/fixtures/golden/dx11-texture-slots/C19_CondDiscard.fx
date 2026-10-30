sampler2D SB;
sampler2D SA;
sampler2D SC;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 a = tex2D(SA, uv); float4 c = tex2D(SC, uv); clip(tex2D(SB, uv).a - 0.5); return a * c; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
