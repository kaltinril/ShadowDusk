sampler2D A; sampler2D B; float k;
float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return F(B, uv) * 0.5 + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
