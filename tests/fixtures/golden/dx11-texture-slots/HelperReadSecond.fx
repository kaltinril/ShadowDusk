sampler2D A;
sampler2D B;
float4 F(float2 uv) { return tex2D(B, uv); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 a = tex2D(A, uv); return a * 0.5 + F(uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
