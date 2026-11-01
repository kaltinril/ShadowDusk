sampler2D A : register(s2); sampler2D B : register(s1);
float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }
float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(A, uv) + tex2D(B, uv) + F(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 PS(); } }
