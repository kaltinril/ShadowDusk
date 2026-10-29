sampler2D A;
sampler2D B;
float4 F(float2 uv) { return tex2D(B, uv); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 f = F(uv); return tex2D(A, uv) * 0.5 + f; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
