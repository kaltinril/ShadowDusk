sampler2D A; sampler2D B;
float4 F(float2 uv) { return tex2D(A, uv); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(B, uv) * F(uv) + F(uv*2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
