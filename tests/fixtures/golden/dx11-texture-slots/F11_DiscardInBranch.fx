sampler2D SC;
sampler2D SB;
sampler2D SA;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = tex2D(SC, uv); if (c.a < 0.1) discard; return c * tex2D(SA, uv) + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
