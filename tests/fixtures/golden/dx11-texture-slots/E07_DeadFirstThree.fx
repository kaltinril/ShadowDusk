sampler2D SC;
sampler2D SB;
sampler2D SA;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(SA, uv).x * 0.0 + tex2D(SB, uv) + tex2D(SC, uv) * tex2D(SA, uv * 3); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
