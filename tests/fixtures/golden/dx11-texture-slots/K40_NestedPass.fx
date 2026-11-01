sampler2D A; sampler2D B;
float4 G(sampler2D t, float2 uv) { return tex2D(t, uv * 3); }
float4 F(sampler2D s, float2 uv) { return G(s, uv) + tex2D(s, uv); }
float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) * F(B, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 PS(); } }
