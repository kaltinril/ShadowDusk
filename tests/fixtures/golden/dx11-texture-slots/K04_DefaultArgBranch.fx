sampler2D A; sampler2D B; float k;
float4 F(float2 uv, bool b = false) { float4 c = 0; if (b) c = tex2D(B, uv); return c; }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = F(uv); return c + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
