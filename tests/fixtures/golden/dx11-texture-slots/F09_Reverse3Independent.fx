sampler2D SC;
sampler2D SB;
sampler2D SA;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(SA, uv) * tex2D(SB, uv) + tex2D(SC, uv) * 2; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
