sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; [unroll] for (int i = 0; i < 3; i++) { c += tex2D(SB, uv + i * 0.1) * tex2D(SC, uv - i * 0.1); } return c; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
