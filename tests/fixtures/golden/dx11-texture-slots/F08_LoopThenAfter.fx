sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; [loop] for (int i = 0; i < 4; i++) { c += tex2D(SC, uv * i); } return c * tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
