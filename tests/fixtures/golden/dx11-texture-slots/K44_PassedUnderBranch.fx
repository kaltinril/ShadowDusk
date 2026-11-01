sampler2D A; sampler2D B; float k;
float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }
float4 PS(float2 uv : TEXCOORD0) : COLOR0 { float4 c = tex2D(A, uv); if (k > 0) c += F(B, uv); return c + tex2D(B, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 PS(); } }
